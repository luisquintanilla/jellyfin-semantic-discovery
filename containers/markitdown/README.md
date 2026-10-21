# MarkItDown MCP HTTP Wrapper Container

A private, reproducible container that converts PDF and EPUB documents with
[MarkItDown](https://github.com/microsoft/markitdown)
to Markdown text over a small HTTP/JSON-RPC API. It exists in this repository
to give the Jellyfin semantic-discovery demo a local, offline conversion
service for the PDF/EPUB fixtures under `assets/fixtures/`.

The image is validated by the repository's end-to-end workflow. The checked
source has been built on a Linux Docker engine and exercised through `/health`
and `/mcp` with the generated PDF and EPUB fixtures.

## Contents

| File | Purpose |
|---|---|
| `Dockerfile` | Multi-stage-free, single-stage build: pinned Python base image + hash-locked `pip install` + non-root runtime user. |
| `requirements.lock.txt` | Fully hash-locked (`--require-hashes`-compatible) dependency set for `markitdown[pdf]==0.1.7` and its transitive dependencies. |
| `server.py` | Standard-library-only (`http.server`) HTTP server exposing `GET /health` and `POST /mcp`. |
| `README.md` | This file. |

## Why a narrow wrapper instead of `markitdown-mcp`?

The task's preference is the official `markitdown-mcp` package. At the time
this container was authored, `markitdown-mcp` was published on PyPI as
**`0.0.1a4`** - an alpha pre-release - and it pulls in the `mcp` Python SDK
and its own (independently fast-moving) dependency tree on top of
`markitdown` itself. Locking an alpha package plus a second large SDK's
transitive dependency graph would work against the goal of a small,
reproducible, hash-pinned surface.

Per the task's explicit fallback allowance ("if the exact package or
transport is incompatible, implement a narrow HTTP wrapper around official
MarkItDown"), `server.py` instead:

* depends only on the **stable** `markitdown[pdf]==0.1.7` release (plus its
  own transitive dependencies, all hash-locked in `requirements.lock.txt`),
  and
* implements just enough of the JSON-RPC 2.0 / MCP request shape
  (`initialize`, `tools/list`, `tools/call`) by hand, using only
  `http.server` from the standard library, for a client to discover and
  invoke a single tool: `convert_to_markdown`.

This is a deliberately narrow shim, not a general-purpose MCP server
implementation. In particular:

* It does not implement MCP transports other than plain HTTP POST
  request/response (no Server-Sent Events / streaming, no stdio transport).
* `initialize` echoes back whatever `protocolVersion` the client requests
  (defaulting to `"2024-11-05"` if none is given) instead of asserting a
  single hard-coded protocol version, since no single current MCP spec
  revision was treated as authoritative for this narrow use case.
* `tools/call` -> `convert_to_markdown` accepts one argument, `source`, which
  must be a bounded PDF or EPUB base64 `data:` URI. Local paths and network
  URLs are rejected before MarkItDown is invoked.

If a future stable `markitdown-mcp` release becomes available with a
reasonably small dependency footprint, replacing `server.py` with that
package (and regenerating `requirements.lock.txt` accordingly) would be a
reasonable follow-up; this wrapper was designed to be easy to delete once
that's true.

## Endpoints

* `GET /health` -> `200 OK`, `{"status": "ok", "service": "...", "version": "..."}`.
* `POST /mcp` -> JSON-RPC 2.0 body. Supported methods:
  * `initialize` - returns `serverInfo`/`capabilities`.
  * `tools/list` - returns the single `convert_to_markdown` tool definition (JSON Schema included).
  * `tools/call` with `params.name == "convert_to_markdown"` and
    `params.arguments.source` (a bounded PDF/EPUB base64 `data:` URI) - returns
    `{"content": [{"type": "text", "text": "<markdown>"}], "isError": false}`
    on success, or a JSON-RPC `error` object (`INVALID_PARAMS` for rejected
    source parameters, `CAPACITY_UNAVAILABLE` for a full conversion gate, and
    `CONVERSION_FAILED` (`-32002`) only when every wrapped converter cause is
    a known deterministic parser/input rejection, and `INTERNAL_ERROR` for
    unknown or infrastructure failures)
    on failure.
  * `notifications/initialized` (and any method with no `id`) - treated as a
    JSON-RPC notification: no response body, `202 Accepted`.

  The service rejects local paths, `file:` URIs, HTTP(S) URLs, and every other
  scheme before invoking MarkItDown. The demo reader sends PDF/EPUB bytes as
  base64 `data:` URIs, so the container does not receive a shared media mount.
  `MAX_DOCUMENT_BYTES` defaults to 8 MiB, `MAX_REQUEST_BYTES` to 12 MiB, and
  `MAX_CONCURRENT_CONVERSIONS` to 2. Conversion failures log only media type,
  decoded byte count, and SHA-256; document bytes are never logged. Successful
  structured `INFO` records are written to stdout; warnings and failures are
  written to stderr.

  EPUB bytes receive a bounded structural preflight before MarkItDown:
  exactly one regular `META-INF/container.xml` (maximum 64 KiB), parsed with
  pinned `defusedxml.minidom`, must name at least one safe contained existing
  rootfile. Malformed XML, missing rootfiles, and unsafe/missing package paths
  return deterministic `CONVERSION_FAILED`; filesystem, import, memory, and
  unknown infrastructure failures remain `INTERNAL_ERROR`.

  The application-level egress guard rejects non-loopback DNS resolution,
  `gethostbyname`/`gethostbyname_ex`/`gethostbyaddr`/`getnameinfo`,
  `socket.create_connection`, direct `socket.connect`/`connect_ex`, and
  destination-bearing `sendto`/`sendmsg`; `/egress-canary` exercises every
  primitive. This is defense in depth inside the Python process, not a
  container firewall or a claim about native-extension syscalls.

  Header/body socket settings are idle timeouts, not total wall-clock
  deadlines. A slow byte drip can retain a handler thread, and synchronous
  `MarkItDown.convert()` cannot be killed by the .NET caller's timeout. Keep
  this unauthenticated service private and restart it to remediate a stuck
  parser. Encoded-byte limits also do not fully bound decompressed EPUB or
  parser-native allocations.

  Successful conversion logs label `ru_maxrss` as a process-lifetime
  high-water and also include process-start, before/after high-water, and
  current `/proc/self/status` RSS values in KiB. They are not per-document
  allocation deltas.

## Base image pin

```
FROM python:3.13-slim-bookworm@sha256:2f2e5a876c71a6757f55ec57f2add0225ddaf01c802a33fcc29073943f94d907
```

This digest was verified live (not invented) by querying the Docker
Registry HTTP API V2 directly:

1. Obtained an anonymous pull token:
   `GET https://auth.docker.io/token?service=registry.docker.io&scope=repository:library/python:pull`
2. Fetched the multi-arch manifest list for the tag:
   `GET https://registry-1.docker.io/v2/library/python/manifests/3.13-slim-bookworm`
   (with `Accept: application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.index.v1+json`)
   and read the `linux/amd64` entry's `digest`.
3. Repeated step 2 in a second, independent request and confirmed the same
   digest was returned both times.
4. Resolved the `linux/amd64`-specific manifest by digest
   (`GET .../manifests/sha256:2f2e5a876c71a6757f55ec57f2add0225ddaf01c802a33fcc29073943f94d907`)
   and confirmed it returned a real image manifest (a `config` descriptor
   plus 4 layers), i.e. the digest is genuinely pullable, not stale.
5. Cross-checked against the public Docker Hub v2 tags API
   (`GET https://hub.docker.com/v2/repositories/library/python/tags/3.13-slim-bookworm`),
   which reported the same digest for `architecture: amd64`, `status: active`,
   `last_pushed: 2026-09-01T02:14:04Z`.

**This tag is mutable.** `python:3.13-slim-bookworm` is periodically
re-pushed by the upstream `python` image maintainers (for example, to pick
up Debian security patches), which changes the digest it points to. Pinning
`FROM ...@sha256:...` here means this Dockerfile always builds from the
exact image verified above, even after the tag moves - but it also means the
digest must be deliberately refreshed (by repeating the steps above) to pick
up upstream security patches. There is no cadence enforced by this repo for
that refresh; treat it as a manual, reviewed action.

## Dependency lock (`requirements.lock.txt`)

Generated with [`uv`](https://github.com/astral-sh/uv):

```powershell
$requirement = Join-Path $env:TEMP 'markitdown-requirement.txt'
'markitdown[pdf]==0.1.7' | Set-Content -LiteralPath $requirement -Encoding ascii
python -m pip install 'uv==0.11.25'
uv pip compile --generate-hashes `
  --python-platform x86_64-manylinux_2_28 --python-version 3.13 `
  --output-file requirements.lock.txt $requirement
Remove-Item -LiteralPath $requirement
```

`uv` itself is pinned to `0.11.25`. The target options match the
`linux/amd64` / Python 3.13 target of the pinned base image, so the lock
file resolves to the same wheels the container will actually install,
independent of the host platform used to run `uv`.

The resulting file pins **exact versions with `--hash=sha256:...` entries**
for every package in the dependency graph (27 packages, including
`markitdown`, `pdfminer-six`, `pypdfium2`, `pillow`, `onnxruntime`, `numpy`,
and their own transitive dependencies). `Dockerfile` installs it with
`pip install --require-hashes -r requirements.lock.txt`, which causes `pip`
to refuse to install anything not covered by a matching hash - so the lock
file is both the dependency manifest and the supply-chain pin.

A representative subset of the hashes (`beautifulsoup4`, `certifi`,
`markitdown`, `numpy`, `requests`, `urllib3`) was cross-verified against the
public PyPI JSON API (`https://pypi.org/pypi/<name>/<version>/json`) to
confirm the recorded hashes match what PyPI itself reports for the released
artifacts.

To regenerate (e.g. to pick up a security fix in a transitive dependency),
re-run the `uv pip compile` command above from a machine with network access
to PyPI, review the diff, and re-verify a sample of hashes against the PyPI
JSON API before committing.

## Validation performed

* **`server.py` syntax-checked** with `python -m py_compile server.py`
  (local Python 3.12; the container targets Python 3.13, so this is a
  best-effort syntax/compile check, not a full runtime-environment match).
* **`server.py` executed directly** on the host (`python server.py`) with
  `markitdown[pdf]==0.1.7` installed locally, and exercised end-to-end over
  real HTTP requests:
  * `GET /health` returned `200 OK` with the expected JSON body.
  * `POST /mcp` `initialize` and `tools/list` returned well-formed JSON-RPC
    responses, including the `convert_to_markdown` tool's JSON Schema.
  * `POST /mcp` `tools/call` -> `convert_to_markdown` was run against the
    **actual generated fixtures** in
    `assets/fixtures/generated/library/Books/`, converting both
    `Field Notes on Static (2024).pdf` and
    `Notes from the Choir Loft (2024).epub` to Markdown, with each fixture's
    distinctive retrieval phrase present verbatim in the conversion output.
  * Local-path and loopback-URL inputs were rejected with JSON-RPC
    `INVALID_PARAMS`, and a failed conversion did not log the base64 payload.
* **Base image digest** verified live against the Docker Registry HTTP API
  V2 and the Docker Hub v2 tags API, as described above (not invented, and
  re-checked twice for stability).
* **Dependency hashes** cross-checked for a representative subset of
  packages against the public PyPI JSON API.
* **Docker build/run was performed** with the digest-pinned base image. Both
  fixture documents were sent as bounded base64 `data:` URIs and converted
  successfully. To repeat the check:
  ```
  docker build -t markitdown-mcp-wrapper containers/markitdown
  docker run --rm -p 8080:8080 markitdown-mcp-wrapper
  curl http://localhost:8080/health
  ```

## License / source notes

`server.py`, `Dockerfile`, and this `README.md` are original work created for
this repository and are covered by the repository's MIT license (see the
root `LICENSE` file). They contain no copied third-party source code.

The built image contains and redistributes the pinned Python base, Debian
packages, MarkItDown, and the dependency graph in `requirements.lock.txt`
under multiple licenses. `THIRD-PARTY-NOTICES.md` is copied to `/licenses`;
Debian notices remain under `/usr/share/doc`, and package metadata/license
files remain under Python's `site-packages/*-dist-info` directories. Consult
those authoritative files before redistribution.

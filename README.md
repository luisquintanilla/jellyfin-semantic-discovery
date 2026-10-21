# Jellyfin Semantic Discovery

Native, local semantic discovery for Jellyfin 12:

- semantic metadata search for Movie, Series, and Book items;
- subtitle/transcript full-content search with a deterministic SRT/WebVTT
  reader;
- PDF/EPUB full-content search through a private MarkItDown service; and
- content-based More Like This for Movie, Series, and Book.

The plugin augments Jellyfin's built-in UI and APIs. It does **not** add a chat
model, LLM, cloud API, replacement UI, injected web script, passage snippet,
or seek-to-timestamp behavior. Search returns Jellyfin items, not passages.

## Verified quick start

### Prerequisites

- Windows x64 with Docker Desktop running Linux/amd64 containers, or a
  Linux/amd64 Docker host.
- Exact .NET SDK `10.0.303` (`global.json` disables roll-forward).
- PowerShell 7.
- Network access on the first run for NuGet, the pinned Hugging Face model,
  and digest-pinned container layers.
- Approximately 1 GB of free space for images, the 90 MB model, Jellyfin
  state, and build output.

Restore and build once:

```powershell
dotnet tool restore --configfile NuGet.Config
dotnet restore Jellyfin.SemanticDiscovery.slnx --configfile NuGet.Config --locked-mode
dotnet build Jellyfin.SemanticDiscovery.slnx --no-restore
```

Start the AppHost with the pinned local Aspire CLI:

```powershell
dotnet tool run aspire -- start --apphost .\src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj --non-interactive --nologo
```

This exact AppHost path and command shape is exercised by
`scripts/Test-EndToEnd.ps1`. Aspire assigns a random loopback proxy port.
Wait for `jellyfin-bootstrap` to finish, then read:

```powershell
Get-Content .\.data\jellyfin-login.json
dotnet tool run aspire -- describe jellyfin --apphost .\src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj --format Json --non-interactive --nologo
```

The login file contains the local administrator URL, username, and generated
password. It is ignored by Git. On Unix, the bootstrap creates its directory
as `0700` and the file as `0600`; on Windows it relies on inherited directory
ACLs. Do not publish it.

Stop while preserving the index and Jellyfin state:

```powershell
dotnet tool run aspire -- stop --apphost .\src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj --non-interactive --nologo
```

Run the same start command again to restart. To reset the demo, stop it,
confirm no AppHost remains, and delete only this repository's `.data`
directory:

```powershell
Remove-Item -LiteralPath .\.data -Recurse -Force
```

For the fully automated clean + persistent restart gate:

```powershell
pwsh -NoProfile -File .\scripts\Test-EndToEnd.ps1
```

That script uses `.data\e2e`, not the normal `.data` tree, and writes sanitized
machine-readable evidence under ignored `artifacts\e2e`.

## What to try

The bootstrap creates three controlled libraries and six parent items:

| Library | Items | Discovery proof |
| --- | --- | --- |
| Semantic Movies | The Signal Garden; Orbit Almanac | metadata-only and subtitle-only search; Movie similarity |
| Semantic Series | Harbor Static; Tidal Register | episode subtitle search aggregated to Series; Series similarity |
| Semantic Books | Field Notes on Static; Notes from the Choir Loft | PDF and EPUB content search; Book similarity API |

Use Jellyfin's normal search field for phrases such as `quiet cartouche hums
twice before dawn` or `marigold ledger returns before frost`.

Stock `jellyfin-web` v12 renders More Like This for Movie and Series. It
hard-codes the supported client item types and exits before requesting similar
items for Book, so this repository does not claim a native Book UI row. The
server and plugin do support Book through:

```text
GET /Items/{bookId}/Similar?Limit=1
```

No web client is patched or replaced.

The default `synthetic` profile is the offline CI/security/access-control
oracle and remains intentionally tiny. The separate showcase below is explicit
opt-in and does not weaken or replace that deterministic baseline.

## Opt-in real showcase

The separately gated `showcase` profile is now available for realistic local
demonstrations. It preserves the synthetic profile unchanged and is never run
by default CI:

```powershell
pwsh -NoProfile -File .\scripts\Test-Showcase.ps1 -AcceptLicenses
```

Release evidence is produced only by an explicit isolated cold-cache run:

```powershell
pwsh -NoProfile -File .\scripts\Test-Showcase.ps1 `
    -AcceptLicenses -ColdCache -PublishEvidence
```

That mode uses dedicated `showcase-publication-*` state, proves seven
`Reused=false` acquisitions, then seven `Reused=true` restart validations,
and purges only that owned publication state. The ordinary retained showcase
cache is not deleted.

It reports consent scope before acquisition and downloads exactly
`432,489,421` locked bytes into `.data\showcase-cache`: Sintel + official SRT,
Spring, Coffee Run, AI for Teachers, Introduction to Digital Humanism, and
The Adventures of Sherlock Holmes. `.data\showcase-runtime` holds the separate
Jellyfin/model/index state. No real Series are invented; synthetic Series
remain the Series/access-control proof.

The cache is explicit opt-in, persistent, ignored by Git and Docker contexts,
validated on every start, and mounted read-only into Jellyfin. Lock-derived
semantic sidecars bind each subtitle/PDF/EPUB to its exact size and SHA-256;
the readers validate the same buffered bytes they parse or send to MarkItDown,
closing the cache-validation-to-ingest gap. A fresh runtime performs the long
initial 6,649-chunk ingest; later runs reuse the cache and vectors. Use
`-ReuseValidatedRuntime` only when intentionally reusing an already measured
runtime. Purge only the owned profile:

```powershell
pwsh -NoProfile -File .\scripts\Purge-Showcase.ps1
```

The source and rights contract is
`assets/showcase/sources.lock.json`. Authoritative rights come from Blender,
Pressbooks, Springer/Crossref, and Project Gutenberg's own policy/embedded
license—not Internet Archive or Wikimedia catalog presence alone. The
textbooks are classified mixed-license. Local extraction can include
exception-marked captions, but the runtime does not determine per-element
rights. No bulk extracted text, chunks, vectors, book screenshots, covers,
figures, logos, or source binaries are redistributed; the source lock includes
eight short exact-text anchors across four source artifacts solely to verify
the pinned extraction path.
This is not legal advice, and non-U.S. Project Gutenberg users must verify
local law. Sherlock's title-specific statement, `Public domain in the USA.`,
is validated from `OEBPS/content.opf` inside the exact hash-pinned EPUB.

Cold acquisition evidence separates lock-defined `PermittedHosts` from
observed transport facts. Managed redirects record hosts actually requested;
curl records only its reported final effective host rule and redirect count.
Warm restart evidence omits transport observations entirely and contains only
deterministic source/cache/generated identities.

Measured C# evidence (`assets/showcase/evidence/measured-retrieval.json`) uses
the exact pinned model/runtime and lock-derived projections. The clean run
embedded six metadata records plus 6,649 chunks (`6,655` ingest embeddings);
restart embedded zero additional records or chunks and preserved the vector
payload hash:

| Type | Pair | Cosine |
| --- | --- | ---: |
| Movie | Spring ↔ Coffee Run | `0.40228748` |
| Movie | Coffee Run ↔ Sintel | `0.33845967` |
| Movie | Sintel ↔ Spring | `0.2937084` |
| Book | AI for Teachers ↔ Digital Humanism | `0.4974855` |
| Book | AI for Teachers ↔ Sherlock Holmes | `0.29229748` |
| Book | Digital Humanism ↔ Sherlock Holmes | `0.250362` |

Default recommendation floor `0.50` admits none. The isolated showcase floor
`0.38` admits four directed semantic results: Spring↔Coffee Run and
AI-for-Teachers↔Digital-Humanism. It excludes Sintel and Sherlock. Jellyfin 12
always invokes every local similar-items provider, so native Sintel
`/Similar` falls through to the built-in `Local Genre/Tag` provider and returns
Spring after Semantic Discovery returns zero; native Sherlock returns zero.
The API does not expose provider attribution. This platform boundary is
recorded rather than misrepresented as semantic success. Source metadata was
not changed to force rankings.

All six source-derived queries returned the intended typed owner through the
C# retrieval path and ranked first through native Jellyfin Search/Hints on
repeated identical calls. The unrelated query control returned no result.
Direct C# scores and native API ordering are separate evidence artifacts;
Search/Hints does not expose the plugin's raw cosine/fused score. Showcase
queries use its generated administrator; synthetic tests remain the
authorization oracle.

Retrieval evidence is input-derived: `SnapshotContentId`, `IndexIdentity`, and
`VectorPayloadSha256` are retained, while the timestamp-dependent raw snapshot
file hash is intentionally excluded.

Measured serialized JSON-RPC requests were `18,238,812` bytes (AI for
Teachers), `37,396,552` bytes (Digital Humanism), and `445,723` bytes
(Sherlock), under the showcase-only 48 MiB hard request cap. The largest
observed MarkItDown process-lifetime high-water was `411,796 KiB`; the cold
Jellyfin process peak working set was `1,650,130,944` bytes. Around the exact
`37,396,552`-byte Digital Humanism request, Jellyfin's current working set was
`897,290,240` bytes before and `859,422,720` bytes after. Separately sampled
process high-water values are recorded before and after without assuming they
are a per-request delta.
The evidence separately records client bytes and server KiB; these remain
process working-set observations, not managed-allocation or per-document delta
claims.
Conversion concurrency is one. Raw image/audio content is not embedded—only
lock-derived metadata, Sintel's SRT, and book text.

Jellyfin's proxy-aware HTTP clients receive black-hole HTTP(S)/ALL proxy
settings with `NO_PROXY` limited to MarkItDown and loopback. Initial and restart
canaries prove external HTTP failed while the internal converter remained
reachable. Aspire 13.5.3 DCP still attaches an egress-capable bridge and does
not support an internal/no-route network, so direct-socket egress remains a
declared limitation. MarkItDown separately denies Python address-resolution
APIs, `create_connection`, direct `socket.connect`/`connect_ex`, and
destination-bearing `sendto`/`sendmsg`.

## Architecture

```text
Jellyfin 12 native search/similar APIs
                |
                v
       Semantic Discovery plugin
         |                  |
         v                  v
 stable metadata       content readers
 projection            - SRT/WebVTT cues
                       - PDF/EPUB -> MarkItDown /mcp
         \                  /
          v                v
      MiniLM ONNX embedding runtime
           (local CPU, 384 floats)
                    |
                    v
      metadata + content VectorData collections
                    |
                    v
 deterministic fusion -> Jellyfin access filter -> item IDs
```

| Project | Responsibility |
| --- | --- |
| `Jellyfin.SemanticDiscovery.Core` | tokenizer/ONNX inference, projection, chunking, snapshot, vector search, fusion |
| `Jellyfin.SemanticDiscovery.Plugin` | Jellyfin providers, access filtering, task, native config/status page |
| `Jellyfin.SemanticDiscovery.Setup` | model install, curated plugin install, deterministic Jellyfin bootstrap |
| `Jellyfin.SemanticDiscovery.AppHost` | pinned resources, mounts, health, dependencies, generated secret |
| `Core.Tests`, `IntegrationTests`, `EndToEndTests` | deterministic contracts through live Jellyfin API proof |

Aspire orchestrates:

- official Jellyfin 12 container
  `12.0.20260908-012347`;
- locally built, private MarkItDown container;
- checksum-pinned model setup;
- locked fixture generation;
- curated plugin installation; and
- idempotent Jellyfin setup, scan, semantic rebuild, and credential output.

Jellyfin config/cache use worktree-scoped bind directories. Model and media
mounts are read-only. Jellyfin must update its plugin `meta.json` during load,
so the narrowly scoped plugin directory is writable; making that mount
read-only causes Jellyfin 12 startup to fail with `Read-only file system`.

## Retrieval behavior

- One query embedding is generated and reused for metadata and content
  search.
- Access-eligible item IDs are computed from the same immutable index
  generation before top-K vector comparison. Metadata filters on item ID and
  content filters on owner ID, so hidden or out-of-scope vectors cannot crowd
  out eligible results.
- Content chunks are fused by owner, deduplicated, and ordered
  deterministically.
- Exact title remains Jellyfin's score `100`; semantic scores remain below it.
- Semantic results below calibrated floors are omitted.
- Movie/Series/Book recommendations use the source metadata vector, exclude
  the source and Jellyfin-provided exclusions, apply the recommendation floor,
  and recheck the current typed Jellyfin item for the requesting user.
- Local similar providers are always enabled by Jellyfin. Remote-provider
  configuration does not apply to them.

Semantic Discovery is an **internal** search provider so Jellyfin's SQL
lexical results remain. Jellyfin 12 discards all internal-provider results if
any external provider returns a result; that host behavior is a known
limitation.

User-facing requests without a resolvable Jellyfin user fail closed. The
native controllers resolve an omitted `UserId` from the authenticated
principal; a non-admin cannot override another user. API-key/system calls
without a user may therefore receive no semantic results.

## Indexing, storage, and privacy

The index is server-global. `index.json` persists:

- projected metadata text and metadata vectors;
- extracted subtitle/transcript text, language, and cue tick bounds;
- extracted PDF/EPUB chunk text and vectors; and
- model, tokenizer, projection, and embedding-algorithm identities.

This includes items that a particular user may not access. Library, parental
rating, tag, type, media, and parent access are enforced at **query time**.
The snapshot is not encrypted and is not per-user. Protect the Jellyfin plugin
configuration directory with filesystem permissions and the same retention
policy as Jellyfin metadata. Delete the configured snapshot (or the demo
`.data` tree after stopping Jellyfin) to remove persisted extracted text and
vectors.

Snapshot publication is prepare -> atomic disk replace -> synchronous,
non-cancellable in-memory publication. Startup validates dimensions, finite
unit vectors, owner relationships, deterministic chunk keys/hashes, metadata
projection hashes, and the complete index identity. Invalid/corrupt snapshots
are ignored and rebuilt; permission and I/O failures remain visible.

## MarkItDown boundary

The private service accepts only bounded base64 `data:` URIs with
`application/pdf` or `application/epub+zip`. Local paths, `file:` URIs,
HTTP(S) URLs, redirects, ambient proxies, query strings, malformed framing,
and all other schemes are rejected. The client uses
`AllowAutoRedirect=false` and `UseProxy=false`.
Before invoking MarkItDown for EPUBs, the service opens only the already
bounded ZIP bytes, requires one regular `META-INF/container.xml` under a
64 KiB limit, parses it with `defusedxml.minidom`, and requires a safe,
contained, existing rootfile entry. Malformed XML and missing/unsafe rootfiles
are deterministic `-32002` failures.

Defaults:

- source file: 8 MiB;
- JSON request: 12 MiB;
- response: 16 MiB;
- concurrent conversions: 2; and
- HTTP client wait: 2 minutes.

Jellyfin sends document bytes over plaintext HTTP on Aspire's private
container network. The service is unauthenticated and must not be exposed
publicly. Pointing `MarkItDownEndpoint` at a remote service would exfiltrate
document bytes. Input/header/body socket settings are idle timeouts, not
proven wall-clock deadlines. `HttpClient.Timeout` bounds the caller's wait but
cannot kill a synchronous `MarkItDown.convert()` already running; a hung parser
holds its conversion slot until the container is restarted.

Other compressed EPUB contents and parser-native allocations are not fully
sandboxed by the outer encoded-byte limit. This is a small private-network
demo service, not a hostile-document isolation boundary. Capacity, transport,
protocol, and converter-infrastructure failures retain prior
same-source chunks. A MarkItDown `FileConversionException` becomes JSON-RPC
`-32002` only when every wrapped cause is a known deterministic parser/input
rejection; corrupt source bytes therefore cannot silently retain old chunks,
while disk/import/resource failures remain transient.
Failure logs record source kind, decoded size, content hash, and exception
type—not document bytes or base64.

## Plugin configuration

The native plugin page exposes:

- **Enabled** — fail-safe master switch;
- **Maximum semantic results** — server-enforced `1..100` cap; and
- read-only status, generation, identity, item/chunk counts, and last refresh.

`ModelDirectory`, `SnapshotPath`, and `MarkItDownEndpoint` are also available
in plugin XML/environment-driven configuration. Changing those paths requires
a Jellyfin restart so the lazy runtime is recreated. Runtime readiness uses
JSON-RPC `tools/list` at the exact configured MCP endpoint; it does not derive
or require a root `/health` route, so reverse-proxy path prefixes remain
supported. Rebuild through the native **Rebuild semantic discovery index**
scheduled task.

AppHost supplies:

- `SEMANTIC_DISCOVERY_MODEL_DIRECTORY`;
- `SEMANTIC_DISCOVERY_MARKITDOWN_ENDPOINT`; and
- `SEMANTIC_DISCOVERY_STARTUP_SMOKE=true`.

## Reproducibility and pins

| Component | Pin |
| --- | --- |
| .NET SDK | `10.0.303`, no roll-forward |
| Aspire CLI | `13.5.3` in `.config/dotnet-tools.json` |
| Jellyfin packages | `12.0.0` |
| Jellyfin image | OCI index `sha256:baba6304...`; selected linux/amd64 child `sha256:97150310...` |
| all-MiniLM-L6-v2 | revision `1110a243fdf4706b3f48f1d95db1a4f5529b4d41` |
| model / vocab | SHA-256 `6fd5d72f...` / `07eced37...` |
| ONNX Runtime | `1.29.0` |
| Microsoft.ML.Tokenizers | `2.0.0` |
| VectorData abstractions / in-memory | `10.9.0` / `1.0.1` |
| MarkItDown | `0.1.7`, fully hash-locked Python graph |
| fixture generator | PowerShell 7.5 + Ubuntu snapshot + FFmpeg 6.1.1 measured closure |

Direct and transitive NuGet resolution is committed in per-project
`packages.lock.json` files and CI restores with `--locked-mode`.
`assets/model-manifest.json`, `containers/fixture-generator/toolchain-lock.json`,
and `assets/fixtures/SHA256SUMS` carry the other content identities. Fixture
MP4 byte-reproducibility is claimed only for the locked Linux/amd64 generator
image/toolchain measured by CI, not arbitrary hosts.

The model setup downloads binaries directly; this repository does not
redistribute the model. See `THIRD-PARTY-NOTICES.md`, the container notices,
and package metadata for licenses and provenance.

## Validation

```powershell
pwsh -NoProfile -File .\scripts\Test-PowerShellSyntax.ps1
pwsh -NoProfile -File .\scripts\Test-FixtureReproducibility.ps1
pwsh -NoProfile -File .\scripts\Test-MarkItDown.ps1
dotnet test .\tests\Jellyfin.SemanticDiscovery.Core.Tests --no-restore
dotnet test .\tests\Jellyfin.SemanticDiscovery.IntegrationTests --no-restore
pwsh -NoProfile -File .\scripts\Smoke-Plugin.ps1
pwsh -NoProfile -File .\scripts\Test-EndToEnd.ps1
```

CI requires the model-backed tests rather than skipping them. No benchmark or
retrieval-quality numbers are committed unless measured by an actual run.

## Troubleshooting

- **Plugin graph fails to load:** every private managed dependency must be in
  `meta.json` `assemblies`. Do not ship host-owned Jellyfin,
  Microsoft.Extensions infrastructure, System.Text.Json, or EF assemblies.
- **`libonnxruntime.so` missing:** native ORT libraries must be flat beside the
  plugin DLL. The resolver deliberately refuses global fallback.
- **Read-only plugin mount:** Jellyfin rewrites plugin `meta.json`; keep only
  that scoped mount writable. Model/media remain read-only.
- **Book row absent:** stock Jellyfin web v12 does not request Book similar
  items. Use the API; do not patch the web client.
- **Book content stale after service error:** transient MarkItDown failures
  intentionally retain same-source chunks and surface status. Restart the
  private service for a hung parser.
- **No semantic results with an external search provider:** Jellyfin suppresses
  internal results when an external provider returns anything.
- **Reset failed:** stop the exact AppHost first. Safety scripts refuse broad,
  unowned, or out-of-scope recursive deletion.

## License

Repository-authored source and synthetic fixtures are licensed under MIT.
Third-party components retain their own licenses; see
`THIRD-PARTY-NOTICES.md`.

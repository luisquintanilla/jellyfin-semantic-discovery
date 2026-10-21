# Opt-in real showcase profile

This profile is separate from the deterministic synthetic CI oracle. It
downloads no files unless the operator explicitly runs:

```powershell
pwsh -NoProfile -File .\scripts\Test-Showcase.ps1 -AcceptLicenses
```

The publication gate is intentionally more expensive and starts from an
isolated empty cache:

```powershell
pwsh -NoProfile -File .\scripts\Test-Showcase.ps1 `
    -AcceptLicenses -ColdCache -PublishEvidence
```

It proves seven cold downloads and seven restart reuses, then safely removes
only `.data/showcase-publication-cache` and
`.data/showcase-publication-runtime`. It never purges the retained operator
cache.

The source lock selects exactly three Movies and three Books (seven files
including Sintel's SRT), no Series, totaling `432,489,421` source bytes.
Original binaries, extracted book text, content chunks, vectors, covers, and
remote artwork are ignored and are never committed.

`sources.lock.json` is the machine-readable acquisition/rights record. Each
artifact has an exact URL, retrieval date, byte count, SHA-256, content types,
and redirect host policy. Each item has human-reviewed authoritative metadata
and rights URLs, a short evidence quote, attribution, scope/exclusions, and a
hash of the normalized committed evidence record. Normal runs do not scrape or
interpret mutable rights pages.

Rights records are review evidence, not automated legal clearance. The two
textbooks are mixed-license publisher originals processed locally; the runtime
does not classify per-element rights. Project Gutenberg asserts U.S. public
domain and has a registered trademark. Users must verify applicable rights in
their jurisdiction. Checked-in anchors are a narrow publication allowlist;
book screenshots, figures, captions, covers, logos, and permission-only
material are excluded.

For Sherlock, the human-reviewed record is title-specific: acquisition
validates `Public domain in the USA.` from `OEBPS/content.opf` in the exact
SHA-256-pinned EPUB using a bounded, DTD-disabled XML extraction. Project
Gutenberg trademark, descriptive-link, no-redistribution, and non-U.S.
jurisdiction caveats remain explicit. The Spring evidence record preserves the
authoritative page's word `un` verbatim and identifies it as an apparent
upstream typo rather than silently changing the quote.

The downloader uses GET, identity encoding, no ambient proxy/decompression,
manual bounded redirects, public-address validation at connect time, exact
streamed size/hash checks, restrictive temp files where Unix modes apply, and
same-directory atomic publication. Pressbooks and Springer use narrowly scoped
curl transports because their current bot/IDP flows reject .NET HTTP; those
transports pin validated DNS targets, bound redirects/time, and validate the
same exact bytes. A dedicated ownership marker is published before the first
download so the purge command can remove a failed partial acquisition; the
completion manifest remains separate and is published only after all artifacts
and sidecars validate. Non-empty unowned output directories are refused.
The lock's permitted host rules are distinct from observations: managed HTTP
records request hosts it actually visited; curl contributes only its reported
final effective host rule and redirect count. In particular, a direct Springer
success does not claim that the IDP host was contacted.

Validated cache and runtime are separate:

- `.data/showcase-cache` — exact source bytes, receipts, lock-derived
  NFO/OPF/semantic sidecars;
- `.data/showcase-runtime` — model, Jellyfin config, and semantic snapshot.

Each semantic sidecar records the exact size and SHA-256 of its subtitle,
PDF, or EPUB. The content readers validate the same bounded byte buffer they
parse or serialize, so a cache mutation after acquisition validation fails
before subtitle parsing or any MarkItDown HTTP request.

Purge both safely:

```powershell
pwsh -NoProfile -File .\scripts\Purge-Showcase.ps1
```

The measured, sanitized evidence under this directory contains no source
binaries, extracted text, chunks, vectors, credentials, cookies, payload
bodies, or remote artwork. The source lock outside that evidence directory
contains eight short, human-reviewed, exact-text validation anchors across
four source artifacts; it does not contain bulk extracted book content.

The evidence manifest classifies and hashes cold acquisition, restart reuse,
direct C# retrieval, native Jellyfin search/similar outcomes, runtime policy,
separate client/server process memory, and network canaries. It binds them to
immutable base commit `43f393b6b419eb95afb0a485756649d4a280a372` plus a
canonical implementation-content digest, avoiding a self-referential final
commit hash.
Cold network observations are explicitly non-deterministic. Warm restart
evidence is deterministic and omits transport/hop fields. Direct C# retrieval
evidence excludes the raw snapshot-file hash because snapshot `CreatedUtc`
changes; stable logical snapshot, index, and vector-payload identities remain.

The isolated `0.38` semantic floor yields Spring↔Coffee Run and AI for
Teachers↔Digital Humanism, while Sintel and Sherlock produce no semantic
result. Native Jellyfin returns zero for Sherlock. Native Sintel falls through
to Jellyfin 12's always-enabled `Local Genre/Tag` provider and returns Spring;
the native API exposes no provider attribution, so this is documented as a
platform limitation rather than claimed as a semantic recommendation.

Jellyfin receives a black-hole proxy policy and a tested external HTTP canary,
with only internal MarkItDown/loopback in `NO_PROXY`. Aspire DCP does not
provide a supported internal/no-route network, so direct-socket Jellyfin
egress remains possible and no stronger isolation is claimed. MarkItDown's
Python process additionally blocks and tests address-resolution APIs,
`create_connection`, direct `connect`/`connect_ex`, and destination-bearing
`sendto`/`sendmsg`.

The bundled runtime proves converter reachability with MCP JSON-RPC
`tools/list` at the exact configured endpoint. No root `/health` path is
derived, preserving custom path-prefixed reverse proxies.

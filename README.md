# Jellyfin Semantic Discovery

Local semantic search and similar-item recommendations for an unchanged
Jellyfin 12 server.

The in-process plugin and every assembly Jellyfin loads target `net10.0`.
Aspire, setup tools, the extraction service, and tests target `net11.0`.
Aspire 17 starts Jellyfin, Ollama, the required models, document extraction,
fixture setup, plugin installation, and Jellyfin bootstrap.

## Five-minute start

Prerequisites:

- .NET SDK `11.0.100`;
- Docker Desktop or another Linux-container Docker host;
- PowerShell 7;
- `pdftoppm` (Poppler) on `PATH` for local PDF page rasterization; and
- the final nine demo assets delivered according to
  `assets/demo/manifest.json`.

All three PDF books, three Movies, and three Audio items are checked in and
hash-sealed. Startup validates every declared media file and fails closed on a
missing file, size mismatch, hash mismatch, or incomplete license record.

```powershell
dotnet tool restore --configfile .\NuGet.Config
dotnet restore .\Jellyfin.SemanticDiscovery.slnx `
  --configfile .\NuGet.Config --locked-mode
dotnet build .\Jellyfin.SemanticDiscovery.slnx --no-restore

$env:SEMANTIC_DISCOVERY_PROFILE = "demo"
$env:SEMANTIC_DISCOVERY_HARDWARE_PROFILE = "standard"
dotnet tool run aspire -- start `
  --apphost .\src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj `
  --non-interactive --nologo
```

Aspire persists Ollama models in its Docker volume. The first run pulls
`embeddinggemma` and the selected Qwen3-VL model. Wait for
`jellyfin-bootstrap` to finish, then read:

```powershell
Get-Content .\.data\jellyfin-login.json
```

The file contains the generated local administrator credentials and is ignored
by Git.

If Poppler is installed outside `PATH`, set
`SEMANTIC_DISCOVERY_PDF_RASTER_EXECUTABLE` to the absolute `pdftoppm`
executable path before starting Aspire.

## Hardware profiles

| Profile | Vision model | Use |
| --- | --- | --- |
| `standard` | `qwen3-vl:8b-instruct` | Default; bounded non-thinking 8B visual extraction |
| `lower-memory` | `qwen3-vl:4b-instruct` | Explicit bounded non-thinking 4B option |

Set `SEMANTIC_DISCOVERY_HARDWARE_PROFILE` before starting Aspire. There is no
silent model fallback: startup or extraction fails if the exact configured
model is unavailable.

`embeddinggemma` is always required for semantic indexing. The application
chunk tokenizer is separately pinned to
`Microsoft.ML.Tokenizers`/O200k `2.0.0`; it is not presented as Ollama's model
tokenizer.

## What is indexed

- Movie metadata plus SRT/WebVTT subtitles;
- Audio metadata plus SRT/WebVTT transcripts;
- Series metadata plus episode subtitle ownership aggregated to the Series;
- Book metadata plus completed PDF extraction artifacts.

Metadata and content remain separate vector representations. Content chunks
retain owner, source, heading, page, source offsets, and text/visual
provenance. Query-time access checks run before vector results are returned.
Excluded IDs and source IDs are enforced for similar-item queries. Exact
lexical title matches keep precedence because the plugin remains an
`IInternalSearchProvider` and maps semantic scores into a capped lower band.
This is also required by Jellyfin 12: if an external search provider returns
results, Jellyfin discards internal-provider results.

The index identity includes the exact Ollama model digest, extraction profile,
application tokenizer identity, projection contract, and chunking pipeline.
Snapshots are published atomically and reused after restart only when that
identity and every persisted vector validate.

## Document extraction control plane

`Jellyfin.SemanticDiscovery.Extraction` is a `net11.0` Minimal API service.

| Endpoint | Contract |
| --- | --- |
| `POST /extractions` | Accepts only manifest item ID, expected source SHA-256, and `Text`, `Vision`, or `Hybrid`; returns `202` and a status URI |
| `GET /extractions/{jobId}` | Returns persisted job state and completed artifact location |
| `DELETE /extractions/{jobId}` | Requests cancellation and returns `202` |
| `GET /health/live` | Process liveness |
| `GET /health/ready` | Startup reconciliation readiness |

The bounded `Channel<T>` queue is in-memory and is **not durable**. Job state
and completed artifacts are persisted atomically. On restart, the
`BackgroundService` reconciles persisted pending/running work back into the
queue. Default concurrency is one.

Before processing, the service validates manifest-root containment, reparse
points, `.pdf` extension, expected SHA-256, exact size, configured size limit,
declared page count, and configured page limit. PdfPig reads the deterministic
text layer. Poppler rasterizes pages locally. Only rendered page images—not raw
PDF files—are sent to
`CommunityToolkit.DocumentExtraction.VisionLanguageModel` through the local
Ollama Qwen3-VL endpoint. Text and visual results are merged with provenance,
converted into DataIngestion documents/chunks, and atomically published under
an identity derived from source SHA-256, extraction mode, and exact model for
indexing.

## Token and tensor techniques

- Unicode-scalar whitespace normalization retains normalized-to-source offsets.
- Token counts and budgets come from the pinned application tokenizer.
- Heading context and deterministic overlap are preserved at chunk boundaries.
- Boundary tests cover normalization, offsets, token limits, overlap, Unicode,
  and unbroken segments.
- Embeddings are dimension-checked and finite-checked before
  `TensorPrimitives` L2 normalization and cosine similarity.
- Similar-item candidates use a deterministic two-dimensional `Tensor<float>`
  recommendation matrix; hot loops use spans over its backing storage.

## Native Jellyfin limits

Search returns Jellyfin items, not passages. The sample does not add chat,
seek-to-timestamp, snippets, a replacement UI, injected JavaScript, or Media
Studio changes.

The server plugin provides typed semantic search and local similar-item
providers for Movie, Audio, Series, and Book. The stock Jellyfin web client is
verified to render native similar-item rows for Movie, Audio, and Series. The
unchanged web client does not render a Book row; Book similar results remain
available through:

```text
GET /Items/{itemId}/Similar
```

The API does not identify which local similar-item provider produced a result,
and Jellyfin can continue to built-in providers when this provider returns no
eligible semantic result.

## Privacy and network boundary

Media, rendered pages, metadata, chunks, vectors, and queries remain on the
local machine and are sent only among Jellyfin, the extraction service, and
Ollama. Initial NuGet, container, and model pulls require network access.

Aspire service references and local endpoints define the intended boundary,
but the default Docker bridge is not a cryptographic no-egress sandbox. Host
Docker policy, firewall policy, and proxy configuration remain responsible for
enforcing stronger network isolation.

## Storage and reset

Repository-owned state is under `.data`:

- Jellyfin config/cache and generated credentials;
- atomic extraction job state and published artifacts; and
- semantic index snapshots.

Stop Aspire before deleting `.data`:

```powershell
dotnet tool run aspire -- stop `
  --apphost .\src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj `
  --non-interactive --nologo
Remove-Item -LiteralPath .\.data -Recurse -Force
```

This does not remove the Aspire-managed
`jellyfin-semantic-discovery-ollama` Docker volume. Keep that volume to avoid
downloading models again. To reset models, stop Aspire and remove only that
named volume; do not delete unrelated Docker volumes.

## Final asset delivery

`assets/demo/manifest.json` contains exactly three Movie, three Audio, and
three Book slots. The Book corpus is delivered as three original eight-page
CC BY 4.0 PDFs, with hashes and authorship in
`assets/demo/LICENSE-AUTHORSHIP.txt`. Its deterministic 8B/4B extraction and
retrieval checks are declared in `assets/demo/extraction-benchmark.json`, with
measured hardware, timing, memory, chunk, heading, provenance, and anchor
results in `assets/demo/extraction-benchmark-results.json`.

The approved Movie/Audio release is retained byte-for-byte with MP4/FLAC, VTT,
and NFO sidecars. The approved Book release is retained byte-for-byte with
PDFs and OPF metadata. No prompts, storyboards, editable sources, generators,
download caches, rights-refresh workflow, or production receipts are included.

Tiny synthetic CI/security fixtures remain under `assets/fixtures`, including
Series coverage. They are test fixtures, not public demo media.

## Validation

```powershell
dotnet test .\tests\Jellyfin.SemanticDiscovery.Core.Tests --no-restore
dotnet test .\tests\Jellyfin.SemanticDiscovery.Extraction.Tests --no-restore
dotnet test .\tests\Jellyfin.SemanticDiscovery.IntegrationTests --no-restore
pwsh -NoProfile -File .\scripts\Smoke-Plugin.ps1
pwsh -NoProfile -File .\scripts\Test-EndToEnd.ps1
```

`Test-EndToEnd.ps1` uses the tiny `ci` profile and a separate `.data\e2e`
tree. It does not publish benchmark, accuracy, production, or final-asset
claims.

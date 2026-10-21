# Compatibility and troubleshooting evidence

## Jellyfin 12 plugin loading

This plugin targets `net10.0` and the exact `Jellyfin.Controller`,
`Jellyfin.Model`, and `Jellyfin.Database.Implementations` version `12.0.0`.
Jellyfin 12 discovers exported concrete provider and scheduled-task types, then
constructs them from the root service provider. The plugin therefore keeps those
types public and uses `IPluginServiceRegistrator` only for their singleton
dependencies.

The first container smoke exposed a Jellyfin 12 plugin-loading constraint:
putting private managed dependencies beside the plugin DLL is insufficient.
`PluginLoadContext` is collectible, but its `AssemblyDependencyResolver` is
created from the plugin directory. The dependencies were not resolved and
service registration failed on `Microsoft.ML.OnnxRuntime`.

The verified fix is:

- list every plugin-private managed dependency in `meta.json` `assemblies`;
- do not list or ship host-owned Jellyfin, Microsoft.Extensions host
  infrastructure, System.Text.Json, or Entity Framework assemblies;
- copy `libonnxruntime.so` and `libonnxruntime_providers_shared.so` flat beside
  the plugin DLL for the Linux Jellyfin container (and equivalent Windows DLLs
  for local smoke runs);
- install a `NativeLibrary.SetDllImportResolver` before the first ORT call.

The resolver accepts only the expected ORT import basenames and throws when a
plugin-local native is missing. It does not fall back to an ambient/global
native installation.

`scripts/Smoke-Plugin.ps1` builds the complete graph, checks the pinned Python
reference vector and pairwise similarities, boots the digest-pinned Jellyfin 12
image twice with fresh then persisted configuration, and requires an in-process
Linux ONNX success marker. It also runs a missing-`libonnxruntime.so` negative
control and requires the corresponding failure marker, so a host-only Windows
inference cannot mask a broken plugin deployment.

The pinned tokenizer uses BERT basic tokenization with lower-casing and accent
stripping enabled. Its effective options are part of the tokenizer identity hash,
so changing normalization invalidates old snapshots.

## MEDI document and chunk contracts

PDF, EPUB, subtitle, and transcript ingestion now exchange
`IngestionDocument` and `IngestionChunk<string>` instances from
Microsoft.Extensions.DataIngestion (MEDI). The production index lifecycle still
uses the existing `IContentReader` boundary and `SemanticIndexBuilder`.

The package line is pinned to
`10.9.0-preview.1.26411.16` for DataIngestion Abstractions and Markdig. This
matches the repository's Microsoft.Extensions.AI 10.9 family and avoids turning
the ingestion migration into a broader 10.11 package-family upgrade. The
preview API is isolated behind internal adapters because the released package
uses generic `IngestionChunk<T>` while the upstream API is already evolving
toward a non-generic content model.

MEDI declares Microsoft.ML.Tokenizers `>= 1.0.1`; central package management
continues to resolve the pinned `2.0.0`. Both package versions expose the same
strong-named assembly identity (`Microsoft.ML.Tokenizers`, version `1.0.0.0`,
public key token `cc7b13ffcd2ddd51`). Locked restore, compile, and plugin smoke
remain the runtime binding gates.

The actual plugin publish graph adds only these private managed assemblies:

- `Markdig.Signed.dll`;
- `Microsoft.Extensions.DataIngestion.Abstractions.dll`; and
- `Microsoft.Extensions.DataIngestion.Markdig.dll`.

The full `Microsoft.Extensions.DataIngestion.dll` and
`Microsoft.Extensions.AI.dll` are test-only and are not in the plugin
allowlist. Jellyfin-owned and host-owned Microsoft.Extensions assemblies remain
excluded.

### Exact MiniLM budget adapter

MEDI 10.9's `HeaderChunker` counts with
`Tokenizer.CountTokens(..., considerNormalization: false)`, excludes embedding
special tokens, and throws when header context consumes the configured budget.
The plugin therefore uses source-aware custom MEDI chunkers while retaining
MEDI documents and chunks as the exchange contracts.

Every produced MEDI chunk records the generator's exact normalized MiniLM count
in `jellyfin.model-token-count`. The single domain mapper recalculates that
count, rejects mismatches, and enforces the 256-token model limit including the
two embedding special tokens before computing hashes or deterministic chunk
identities. Heading fallback removes older ancestors first, preserves the
deepest heading when possible, and finally uses empty context instead of
allowing MEDI's context-only exception to escape as an accidental behavior
change. If the deepest heading itself must be truncated, its first candidate is
limited to half the model budget so document chunks retain useful body capacity.

Markdown line endings are canonicalized to LF before MEDI parsing. Chunk
mapping repeats that canonicalization before hashing and identity generation,
so CRLF and LF sources produce identical chunk text, hashes, and IDs.

### Markdig preview behavior and A/B fixtures

MEDI Markdig 10.9 preserves tables, but a list section's public
`GetMarkdown()` joins child content and loses the original list markers. The
contract therefore guarantees ordered semantic list lines, not exact `- `
syntax for flat lists that MEDI maps without semantic loss.

The same preview also classifies non-empty top-level leaf blocks with no
`Inline`/`FirstChild` as empty. That drops fenced code, indented code, and raw
HTML. A thin adapter first runs MEDI's `MarkdownReader`, then merges every such
leaf block back by its original Markdig source span as an
`IngestionDocumentParagraph`. It does not use reflection or a second heading
parser.

The preview reader also rejects valid Advanced Markdown constructs such as
task lists and footnotes, and some successfully mapped nested lists, nested
code, duplicate items, or mixed image paragraphs can omit semantic text. The
adapter therefore compares an ordered, multiplicity-preserving semantic
projection of the original Markdig AST with the public MEDI document. If MEDI
throws `NotSupportedException`, mapped-element alignment fails, or that
recursive coverage check differs, the adapter creates a fallback
`IngestionDocument` from the original bounded top-level Markdown spans.
Headings remain typed MEDI headers, body blocks remain typed MEDI paragraphs,
and the same exact MiniLM chunker and mapper enforce the final model budget.

Image-bearing Markdown uses the same fallback so mixed text and multiple image
alternative texts remain in source order. Markdown image destinations, HTML
`img`/`source` locations, and data-URI payloads are removed before chunking;
only alternative text and surrounding semantic content are embedded. Any
residual unsupported-structure exception is translated to a permanent
per-source ingestion error, so `SemanticIndexBuilder` records that source and
continues with later sources instead of aborting the complete rebuild.

`MediMigrationFixtureComparisonTests` records the pre-migration and MEDI
results:

| Fixture | Pre-migration | MEDI hybrid |
|---|---|---|
| Timed text | Same chunk text, counts, language, and aggregate cue ticks | Exact parity |
| Structured Markdown | Separate whitespace-flattened paragraph chunks | One structure-aware chunk at the fixture budget |
| Lists | Literal markers retained in flattened text | Markers omitted by MEDI preview; ordered item lines retained |
| Fenced code | Content retained but whitespace-flattened | Original fenced Markdown and ordering retained by the lossless merge |
| Unsupported Advanced Markdown | Parsed by the custom text path | Raw-span MEDI document fallback preserves bounded semantic content |
| Nested lists/code and duplicate items | Flattened content retained | Recursive coverage mismatch selects the raw-span fallback without dropping or deduplicating text |
| Mixed or multiple images | Alt text and surrounding text retained | Alt text and surrounding text retained in order; image locations and payloads removed |

Real-model integration tests separately enforce the exact MiniLM limit for
documents, timed text, CJK text, and long unbroken Unicode.

### Full pipeline decision

The released MEDI 10.9 pipeline is not used on the live plugin rebuild path.
`IngestionPipeline<T>` publicly accepts only directories or
`IEnumerable<FileInfo>`, keeps document ingestion private, catches broad
per-file exceptions, and writes each result immediately. That does not fit the
Jellyfin item/source lifecycle or the current cancellation and error contract.
The committed `MediPipelineCompatibilityTests` working spike passes a
success/failure/success file sequence through the real pipeline. Processing
returns successfully and the collecting writer observes two separate committed
write calls around the failed file, concretely demonstrating per-file partial
publication rather than an all-source transaction.

`VectorStoreWriter<T>` also does not preserve the required index guarantees: it
assigns new random GUID keys, invokes embedding on every upsert, discovers
existing records only by document ID, inserts replacements one by one, and
then deletes prior keys. It has no deterministic content-hash reuse, no policy
for retaining prior chunks after a transient conversion failure, and no atomic
prepare/save/publish boundary across metadata and content.

`SemanticIndexBuilder` therefore remains responsible for unchanged-content
embedding reuse, transient-source retention, batched embedding, prepared vector
state, atomic snapshot save, and final publication. MEDI's full pipeline may be
appropriate later for offline import, setup, or fixture tooling where those
live-index guarantees are unnecessary.

The snapshot schema remains version 3 because the persisted shape is
unchanged. `IndexingPipelineVersion` includes the exact MEDI preview contract,
so existing snapshots are rejected and rebuilt after this chunking change.

## API limitations kept explicit

- Internal search results are discarded by Jellyfin when any external search
  provider returns results. Semantic Discovery remains an internal provider so
  it does not suppress built-in SQL results.
- The plugin uses its own deterministic SRT/WebVTT parser because Jellyfin's
  `ISubtitleParser` lives in the non-packaged `MediaBrowser.MediaEncoding`
  assembly.
- MarkItDown publishes an MCP package and source Dockerfile, but no official
  versioned Microsoft container. This repository builds a private pinned image.
- Similar-item support for `Book` is server-native in Jellyfin 12. Stock
  `jellyfin-web` v12 hard-codes its More Like This item-type allowlist without
  `Book` and returns before requesting `/Items/{id}/Similar`. Book is therefore
  API-only in this demo; Movie and Series are the native UI acceptance surface.
- Jellyfin does not apply `ISimilarItemsProvider.CacheDuration` to local
  providers. Movie, Series, and Book recommendations are recomputed against the
  active in-memory snapshot; the plugin does not claim host-side caching.

Primary source pins and links are maintained in the root README manifest.

## Mount and conversion boundaries

Jellyfin 12 opens and rewrites the installed plugin `meta.json` while loading
the plugin. A read-only plugin-directory bind caused a verified startup failure,
so the AppHost grants write access only to that scoped plugin directory. Model
and media binds remain read-only.

MarkItDown is internal plaintext HTTP on the private Aspire network. Redirects
and ambient proxies are disabled, and only bounded PDF/EPUB data URIs are
accepted. Socket settings are idle timeouts, not total elapsed deadlines, and a
synchronous parser invocation cannot be killed by `HttpClient.Timeout`; see the
root README for the complete privacy/resource-limit statement.

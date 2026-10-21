# Fixture assets for Jellyfin semantic discovery

This directory holds the **source of truth** for a tiny, deterministic media
library used to exercise the semantic discovery demo end to end: a movie, a
single-episode series, a PDF book, and an EPUB book, each carrying Jellyfin
12-compatible sidecar metadata and (for the video items) subtitle sidecars.

The tiny canonical outputs are committed under `generated/` so CI and a clean
clone never depend on an ambient FFmpeg build. `manifest.json` describes every
fixture, while `SHA256SUMS` binds every output byte to the reviewed corpus.
Run `scripts/Generate-Fixtures.ps1` from the repository root to verify the
canonical files. Generation and checksum updates are explicit modes and only
run in the recorded generator environment.
`scripts/Test-FixtureReproducibility.ps1` builds that digest-pinned environment,
generates into an empty temporary output tree, and compares every path and
SHA-256 against the committed corpus.

## Contents

| File | Purpose |
| --- | --- |
| `manifest.json` | Committed, machine-readable source of truth: per-item titles/dates, synthetic metadata/subtitle/document text, distinctive phrases + category, and expected structural properties (durations, codecs, page/chapter counts, byte markers). Deliberately contains **no** benchmark, accuracy, or performance numbers -- only content and objectively verifiable structure. |
| `SHA256SUMS` | Exact hashes for every canonical generated library file. |
| `generated/` | Committed Jellyfin-shaped library tree (`Movies/`, `Series/`, `Books/`) plus a stable measured structural manifest. |

## Fixture library

Generated under `assets/fixtures/generated/library/`:

- **Movie** -- `Movies/The Signal Garden (2024)/` -- `movie.nfo` + `The Signal Garden (2024).mp4` + `.srt`/`.vtt`. Carries the **metadata-only** distinctive phrase (embedded only in the NFO `<plot>`) and the **subtitle-only** distinctive phrase (embedded only in one subtitle cue). Each phrase is asserted absent from the other surface by the generator's self-check.
- **Series** -- `Series/Harbor Static (2024)/Season 01/` and
  `Series/Tidal Register (2024)/Season 01/` provide a semantic-only
  recommendation pair while deliberately using different genres and studios.
  Harbor Static remains the **negative/noise** retrieval control.
- **PDF** -- `Books/Field Notes on Static (2024)/` -- `Field Notes on Static (2024).pdf` + `metadata.opf`. Carries the **pdf-content** distinctive phrase in its page text.
- **EPUB** -- `Books/Notes from the Choir Loft (2024)/` -- `Notes from the Choir Loft (2024).epub`
  + `metadata.opf`. Carries the **epub-content** distinctive phrase in one
  chapter and uses a different author from the PDF recommendation peer.

Five distinctive-phrase categories are covered in total: `metadata-only`,
`subtitle-only`, `negative-noise`, `pdf-content`, `epub-content`.

## Generation and validation

See `scripts/Generate-Fixtures.ps1` for full details. In short, the script:

1. Reads `manifest.json` as the single source of truth.
2. Builds MP4s deterministically with `ffmpeg` `lavfi` sources (`testsrc2`/`smptebars` video, `sine` audio tone) -- no timestamps, random seeds, or embedded creation metadata that would make output non-reproducible.
3. Writes cue-aligned `.srt` and `.vtt` sidecars from the same cue list.
4. Hand-builds a minimal valid PDF (raw object/xref bytes) and a minimal valid EPUB (`System.IO.Compression.ZipArchive`, mimetype stored first/uncompressed) using only .NET/PowerShell standard libraries -- no third-party PDF/EPUB packages.
5. Writes Jellyfin 12-style NFO (`movie.nfo`, `tvshow.nfo`, `season.nfo`, per-episode `.nfo`) and OPF (`metadata.opf`) sidecars.
6. Validates everything it produces: `ffprobe`-reported duration/codec/pixel-format for each MP4, subtitle cue counts, the PDF's `%PDF-`/`%%EOF` byte markers, page count, and a structural parse of its `xref` table (each entry's recorded byte offset is checked against the corresponding `N 0 obj` it should point to, plus a `/Root` trailer check) -- confirmed against a real third-party parser (`markitdown`/`pdfminer.six`) during authoring, not just this repository's own checks -- and the EPUB's zip structure (`mimetype` first entry stored uncompressed, `META-INF/container.xml` -> OPF -> spine files all present and readable, chapter text present).
7. Validates every generated file against `SHA256SUMS`; stale files cannot pass
   based only on structure. `-Mode UpdateChecksums` is reserved for a reviewed
   canonical regeneration.
8. Uses fixed ZIP entry timestamps and writes only relative, stable metadata to
   `generated/manifest.actual.json`.
9. Is idempotent: `-Mode Verify` never writes, `-Mode Generate` recreates a
   non-canonical output and requires it to match the lock, and
   `-Mode UpdateChecksums` stages a complete canonical regeneration before
   atomically replacing the corpus and checksum file.

## License and source notes

All text and media under this directory (fixture titles, plots, subtitle
lines, PDF/EPUB body text, and the procedurally generated audio/video itself)
is **original, synthetic content authored for this repository**. It is not
derived from, and does not quote or reference, any third-party copyrighted
work. Video/audio is produced entirely by `ffmpeg`'s built-in `lavfi` test
sources and tone generator (no stock footage, no sampled audio).

This content is licensed under the same **MIT license** as the rest of the
repository (see the repository root `LICENSE`). No separate CC0 grant is made.

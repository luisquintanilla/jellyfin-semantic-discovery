# Third-party notices

This repository's source is MIT licensed. Runtime dependencies retain their own
licenses; package metadata and upstream license files are authoritative.

## all-MiniLM-L6-v2

- Artifact: `sentence-transformers/all-MiniLM-L6-v2`
- Revision: `1110a243fdf4706b3f48f1d95db1a4f5529b4d41`
- Source: <https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/tree/1110a243fdf4706b3f48f1d95db1a4f5529b4d41>
- Declared license: Apache-2.0 in the model-card YAML
- Model ONNX SHA-256: `6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452`
- Vocabulary SHA-256: `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3`

The repository downloads these files directly and does not redistribute them.
The upstream model repository does not contain a standalone `LICENSE` or
`NOTICE` file at this revision; the Apache-2.0 identifier comes from its model
card. The model is a Sentence-Transformers fine-tune of Microsoft's MiniLM
architecture. Model behavior is provided **as is** and may reflect limitations
or biases in its training data.

This implementation explicitly enforces the Sentence-Transformers
`sentence_bert_config.json` limit of 256 tokens. It uses `vocab.txt` with
`Microsoft.ML.Tokenizers.BertTokenizer`; it does not load the pinned
`tokenizer.json`, whose embedded truncation value is 128.

## Runtime and orchestration

| Component | Pin | License | Source |
| --- | --- | --- | --- |
| Jellyfin server source and official image | 12.0.0 / OCI index `sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5`; linux/amd64 child selected by AppHost `sha256:97150310f89f908abbf6fa9640ccc36f9bfa9e5e987775db310b3b70c2d11602` | GPL-2.0-or-later | <https://github.com/jellyfin/jellyfin/tree/v12.0> |
| Jellyfin.Controller, Jellyfin.Database.Implementations, Jellyfin.Model NuGet packages | 12.0.0 | GPL-3.0-only (declared by each `.nuspec`) | <https://www.nuget.org/packages/Jellyfin.Controller/12.0.0> |
| MarkItDown | 0.1.7 | MIT | <https://github.com/microsoft/markitdown> |
| ONNX Runtime | 1.29.0 | MIT | <https://github.com/microsoft/onnxruntime/tree/v1.29.0> |
| Microsoft.ML.Tokenizers | 2.0.0 | MIT | <https://github.com/dotnet/machinelearning> |
| Microsoft.Extensions.AI / VectorData | 10.9.0 | MIT | <https://github.com/dotnet/extensions> |
| CommunityToolkit.VectorData.InMemory | 1.0.1 | MIT | <https://github.com/CommunityToolkit/AI/tree/main/MEVD/src/InMemory> |
| Aspire AppHost | 13.5.3 | MIT | <https://github.com/microsoft/aspire> |
| FFmpeg fixture generator | Ubuntu package `7:6.1.1-3ubuntu5` from snapshot `20250201T000000Z`; exact executable/package/library closure in `toolchain-lock.json` | LGPL-2.1-or-later/GPL-2.0-or-later by enabled build components | <https://ffmpeg.org/legal.html> |

The MarkItDown container's full hash-pinned Python dependency graph is recorded
in `containers/markitdown/requirements.lock.txt`. The built private image also
contains the digest-pinned Python/Debian base and Python packages under multiple
licenses; it is not an MIT-only image. Its
`containers/markitdown/THIRD-PARTY-NOTICES.md` is copied to `/licenses`, Debian
notices remain in `/usr/share/doc`, and Python package metadata remains in
`site-packages/*-dist-info`.

The fixture-generator image is built locally from the digest-pinned PowerShell
Ubuntu base, an immutable Ubuntu package snapshot, and the measured FFmpeg
closure. The repository redistributes only the original synthetic generated
fixtures, not the generator image or FFmpeg binaries. Fixture byte identity is
claimed only for this locked Linux/amd64 generator environment.

The synthetic fixture text, metadata, audio tones, test patterns, PDF, and EPUB
were authored for this repository and are distributed under the repository's
MIT license. They are not CC0 and are not derived from a published media work.

## Opt-in showcase sources

The optional showcase downloads exact source files into ignored local state;
this repository does not redistribute those binaries, extracted book text,
chunks, or vectors.

| Item | Source/rights authority | Scope and attribution |
| --- | --- | --- |
| Sintel | Blender Foundation Durian project | CC-BY-3.0; retain the complete embedded film credits; non-film attribution `© copyright Blender Foundation \| durian.blender.org` |
| Spring | Blender Studio project page | CC-BY-4.0; `© Blender Foundation \| cloud.blender.org/spring`; the page's phrase “released un the…” is preserved verbatim as an apparent upstream typo |
| Coffee Run | Blender Studio licensing page | CC-BY-4.0; `(CC) Blender Foundation \| studio.blender.org` |
| AI for Teachers: an Open Textbook | Pressbooks publisher page/metadata | Mixed-license publisher original; main work CC-BY-4.0 except where otherwise noted; Colin de la Higuera and Jotsna Iyer |
| Introduction to Digital Humanism | Springer/Crossref + book front matter | Mixed-license publisher original; main VOR CC-BY-4.0 with permission-only figure/table exceptions; Springer Nature Switzerland and listed editors |
| The Adventures of Sherlock Holmes, eBook #1661 | Project Gutenberg landing page, exact EPUB `OEBPS/content.opf`, embedded license, and permissions policy | Exact title-bound statement `Public domain in the USA.`; U.S.-only assertion; Project Gutenberg trademark, local-only/no-redistribution, descriptive-link, and non-U.S. jurisdiction caveats apply |

Internet Archive and Wikimedia are byte mirrors for selected films, not the
rights authorities. Logos, trademarks, remote covers, exception-marked
figures/captions/media, and permission-only material are excluded from
redistributed evidence and screenshots. Local MarkItDown processing is not a
per-element rights classifier and does not imply uniform licensing. This
record is not legal advice; operators must verify applicable rights and
jurisdiction. Exact URLs, hashes, sizes, review quotes, exclusions, and
normalized evidence-record hashes are in
`assets/showcase/sources.lock.json`.

Rejected candidates and reasons—including NASA assets carrying explicit music
license restrictions—are recorded in that lock. Fetch-only delivery would not
cure those restrictions.

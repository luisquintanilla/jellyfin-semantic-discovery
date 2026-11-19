# Third-party notices

This repository's source is MIT licensed. Dependencies, container images, and
models retain their own licenses and terms; upstream package/model metadata is
authoritative.

| Component | Pin | Source |
| --- | --- | --- |
| Jellyfin server and packages | 12.0.0 | <https://github.com/jellyfin/jellyfin/tree/v12.0> |
| Jellyfin linux/amd64 image | `sha256:97150310f89f908abbf6fa9640ccc36f9bfa9e5e987775db310b3b70c2d11602` | <https://hub.docker.com/r/jellyfin/jellyfin> |
| Aspire hosting | 17.0.0 | <https://github.com/microsoft/aspire> |
| CommunityToolkit Aspire Ollama integration | 17.0.0-beta.1052 | <https://github.com/CommunityToolkit/Aspire> |
| Microsoft.Extensions.AI / VectorData | 11.0.0 | <https://github.com/dotnet/extensions> |
| Microsoft.Extensions.DataIngestion / DocumentExtraction | 11.0.0-preview.1.26560.3 | <https://github.com/dotnet/extensions> |
| CommunityToolkit DocumentExtraction PdfPig / VisionLanguageModel | 1.0.0-preview | <https://github.com/CommunityToolkit/AI> |
| Microsoft.ML.Tokenizers and O200k data | 2.0.0 | <https://github.com/dotnet/machinelearning> |
| System.Numerics.Tensors | 11.0.0 | <https://github.com/dotnet/runtime> |
| CommunityToolkit.VectorData.InMemory | 1.0.1 | <https://github.com/CommunityToolkit/AI> |
| Poppler `pdftoppm` | Host-installed executable | <https://poppler.freedesktop.org/> |
| FFmpeg fixture generator | Exact closure in `containers/fixture-generator/toolchain-lock.json` | <https://ffmpeg.org/legal.html> |

Aspire-managed Ollama pulls `embeddinggemma` and either `qwen3-vl:8b` or the
explicit `qwen3-vl:4b` lower-memory profile. The repository does not
redistribute model files. Users are responsible for reviewing and accepting
the model licenses and terms published by Ollama and the upstream model
authors before pulling or using them.

The tiny synthetic fixture text, metadata, tones, test patterns, PDF, and EPUB
under `assets/fixtures` were authored for this repository and are distributed
under the repository MIT license. They exist only for deterministic
CI/security tests.

Final public demo media includes concise authorship and license records in
`assets/demo/manifest.json`. Movie and Audio license claims remain pending
while that manifest remains `pending-final-assets`.

The checked-in `Jellyfin Semantic Discovery PDF Corpus` books contain original
writing and deterministic vector artwork, with AI-assisted drafting and
critique. They are licensed CC BY 4.0 with suggested attribution
`"Jellyfin Semantic Discovery PDF Corpus" (2026).` See
`assets/demo/LICENSE-AUTHORSHIP.txt`.

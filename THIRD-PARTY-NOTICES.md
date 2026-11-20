# Third-party notices

This repository's source is MIT licensed. Dependencies, container images, and
models retain their own licenses and terms; upstream package/model metadata is
authoritative.

| Component | Pin | Source |
| --- | --- | --- |
| Jellyfin server and packages | 12.0.0 | <https://github.com/jellyfin/jellyfin/tree/v12.0> |
| Jellyfin linux/amd64 image | `sha256:97150310f89f908abbf6fa9640ccc36f9bfa9e5e987775db310b3b70c2d11602` | <https://hub.docker.com/r/jellyfin/jellyfin> |
| Aspire hosting and Qdrant integrations | 17.0.0 | <https://github.com/microsoft/aspire> |
| Aspire Azure OpenAI client integration | 13.6.5-preview.1.26559.7 | <https://github.com/microsoft/aspire> |
| Qdrant container image (pulled by Aspire) and Qdrant.Client | Aspire default / 1.19.0 | <https://github.com/qdrant/qdrant> |
| CommunityToolkit Aspire Ollama hosting and OllamaSharp client integrations | 17.0.0 | <https://github.com/CommunityToolkit/Aspire> |
| OllamaSharp | 5.5.2 | <https://github.com/awaescher/OllamaSharp> |
| Microsoft.Extensions.AI / VectorData | 11.0.0 | <https://github.com/dotnet/extensions> |
| Microsoft.Extensions.DataIngestion / DocumentExtraction | 11.0.0-preview.1.26560.3 | <https://github.com/dotnet/extensions> |
| CommunityToolkit DocumentExtraction PdfPig / VisionLanguageModel | 1.0.0-preview | <https://github.com/CommunityToolkit/AI> |
| CommunityToolkit.VectorData.Qdrant | 1.0.1 | <https://github.com/CommunityToolkit/AI> |
| PDFtoImage (bundles PDFium and SkiaSharp native binaries) | 5.4.0 | <https://github.com/sungaila/PDFtoImage> |
| Azure.Identity | 1.21.0 | <https://github.com/Azure/azure-sdk-for-net> |
| Microsoft.ML.Tokenizers and O200k data | 2.0.0 | <https://github.com/dotnet/machinelearning> |

Aspire-managed Ollama pulls `embeddinggemma` and, only when
`SEMANTIC_DISCOVERY_LOCAL_VISION=true`, `qwen3-vl:8b-instruct`. The repository
does not redistribute model files. Users are responsible for reviewing and
accepting the model licenses and terms published by Ollama and the upstream
model authors before pulling or using them. The optional Azure OpenAI route
uses a model deployment in the user's own Azure subscription.

Final public demo media includes concise authorship and license records in
`assets/demo/manifest.json`.

The checked-in `Jellyfin Semantic Discovery PDF Corpus` books contain original
writing and deterministic vector artwork, with AI-assisted drafting and
critique. They are licensed CC BY 4.0 with suggested attribution
`"Jellyfin Semantic Discovery PDF Corpus" (2026).` See
`assets/demo/LICENSE-AUTHORSHIP.txt`.

The checked-in Movie and Audio corpus is original work authored for
The .NET AI Stack, copyright 2026 Luis Quintanilla, licensed CC BY 4.0.
Narration was synthesized with Azure Speech. See
`assets/demo/MEDIA-LICENSE-AUTHORSHIP.txt`.

The additive Movie posters/backdrops, Audio covers, and Book covers are also
copyright 2026 Luis Quintanilla and licensed CC BY 4.0. All of this artwork is
original vector illustration: hand-authored SVG written with Claude Opus 5.5
assistance and rendered to JPEG. It is not AI image generation. Book covers
keep each book's real title, kicker, and subtitle text with new illustrations.

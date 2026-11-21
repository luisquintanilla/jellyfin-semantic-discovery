# AppHost

Aspire orchestration for the whole sample. [AppHost.cs](AppHost.cs) is about 100 lines:

| Resource | Integration | Notes |
| --- | --- | --- |
| `qdrant` | `Aspire.Hosting.Qdrant` | Data volume in the `demo` profile only |
| `ollama`, `embedding-model` | `CommunityToolkit.Aspire.Hosting.Ollama` | Pulls `embeddinggemma`; `SEMANTIC_DISCOVERY_OLLAMA_GPU` turns on the GPU |
| `vision-model` | same | Only with `SEMANTIC_DISCOVERY_LOCAL_VISION=true` (pulls `qwen3-vl:8b-instruct`) |
| `page-reader` | `AddConnectionString` | Only if the `ConnectionStrings:page-reader` user secret is set |
| `semantic-service` | `AddDotnetProject` | References everything above |
| `jellyfin` | `AddContainer` | Pinned image; the plugin, library, and config are bind mounts |
| `jellyfin-setup` | `AddDotnetProject` | Runs once after Jellyfin is healthy, then exits |

`WithReference(...)` is how each piece finds the others. Jellyfin gets the service
address as `services__semantic-service__http__0`, which the plugin reads.

Before starting Jellyfin, the AppHost copies the built plugin
(`Plugin/bin/<configuration>/net10.0`) into `.data/plugin`, so build the solution first.

## Adding the Azure OpenAI route

```powershell
dotnet user-secrets --project src/Jellyfin.SemanticDiscovery.AppHost `
  set ConnectionStrings:page-reader "Endpoint=https://<resource>.openai.azure.com/;Deployment=<vision-model>"
```

The service authenticates with `DefaultAzureCredential` (for example, your `az login`).
Your account needs the **Cognitive Services OpenAI User** role on the resource.

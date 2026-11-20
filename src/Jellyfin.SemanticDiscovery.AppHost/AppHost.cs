using Aspire.Hosting.Dotnet;
using CommunityToolkit.Aspire.Hosting.Ollama;
using Microsoft.Extensions.Configuration;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// ci: text layer only, no chat models. demo: also reads the scanned book with a vision model.
string profile = Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_PROFILE")?.ToLowerInvariant() ?? "demo";
string root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
string data = Path.GetFullPath(Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_DATA") ?? Path.Combine(root, ".data"));
string library = Path.Combine(root, "assets", "demo", "library");
string manifest = Path.Combine(root, "assets", "demo", "manifest.json");

// --- AI building blocks -------------------------------------------------------------------

// Vector store: Qdrant in a container.
IResourceBuilder<QdrantServerResource> qdrant = builder.AddQdrant("qdrant");
if (profile == "demo")
{
    qdrant.WithDataVolume("jellyfin-semantic-discovery-qdrant");
}

// Embeddings: Ollama in a container (add SEMANTIC_DISCOVERY_OLLAMA_GPU=nvidia|amd to use a GPU).
IResourceBuilder<OllamaResource> ollama = builder.AddOllama("ollama").WithDataVolume("jellyfin-semantic-discovery-ollama");
switch (Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_OLLAMA_GPU")?.ToLowerInvariant())
{
    case "nvidia": ollama.WithGPUSupport(OllamaGpuVendor.Nvidia); break;
    case "amd": ollama.WithGPUSupport(OllamaGpuVendor.AMD); break;
}

IResourceBuilder<OllamaModelResource> embeddings = ollama.AddModel("embedding-model", "embeddinggemma:latest");

// --- The semantic service -----------------------------------------------------------------

IResourceBuilder<DotnetProjectResource> semanticService = builder
    .AddDotnetProject("semantic-service", "../Jellyfin.SemanticDiscovery.SemanticService/Jellyfin.SemanticDiscovery.SemanticService.csproj")
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health")
    .WithReference(qdrant).WaitFor(qdrant)
    .WithReference(embeddings).WaitFor(embeddings);

if (profile == "demo")
{
    // Chat route 1: an Azure OpenAI vision model you already have, e.g.
    //   dotnet user-secrets set ConnectionStrings:page-reader "Endpoint=https://<resource>.openai.azure.com/;Deployment=<model>"
    // Auth is Microsoft Entra ID (your `az login`), so there's no key.
    if (builder.Configuration.GetConnectionString("page-reader") is { Length: > 0 })
    {
        semanticService.WithReference(builder.AddConnectionString("page-reader"));
    }

    // Chat route 2: a local vision model. Opt in, it's a 6 GB download and slow without a GPU.
    if (Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_LOCAL_VISION") == "true")
    {
        IResourceBuilder<OllamaModelResource> vision = ollama.AddModel("vision-model", "qwen3-vl:8b-instruct");
        semanticService.WithReference(vision).WaitFor(vision);
    }
}

// --- Jellyfin, with the plugin ------------------------------------------------------------

#if DEBUG
const string configuration = "Debug";
#else
const string configuration = "Release";
#endif
string plugin = Path.Combine(data, "plugin", "Semantic Discovery_0.1.0.0");
Directory.CreateDirectory(plugin);
foreach (string file in new[] { "Jellyfin.SemanticDiscovery.Plugin.dll", "meta.json" })
{
    File.Copy(Path.Combine(root, "src", "Jellyfin.SemanticDiscovery.Plugin", "bin", configuration, "net10.0", file), Path.Combine(plugin, file), overwrite: true);
}

IResourceBuilder<ContainerResource> jellyfin = builder
    .AddContainer("jellyfin", "jellyfin/jellyfin", "12.0.20260908-012347")
    .WithImageSHA256("97150310f89f908abbf6fa9640ccc36f9bfa9e5e987775db310b3b70c2d11602")
    .WithHttpEndpoint(targetPort: 8096, name: "http")
    .WithHttpHealthCheck("/System/Ping")
    .WithBindMount(Path.Combine(data, "jellyfin", "config"), "/config")
    .WithBindMount(Path.Combine(data, "jellyfin", "cache"), "/cache")
    .WithBindMount(plugin, "/config/plugins/Semantic Discovery_0.1.0.0")
    .WithBindMount(library, "/media", isReadOnly: true)
    // The plugin calls the service at services__semantic-service__http__0.
    .WithReference(semanticService.GetEndpoint("http"))
    .WaitFor(semanticService);

// --- One-time setup: libraries, scan, and the first sync ----------------------------------

IResourceBuilder<ParameterResource> adminPassword = builder.AddParameter(
    "jellyfin-admin-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);

builder.AddDotnetProject("jellyfin-setup", "../Jellyfin.SemanticDiscovery.Setup/Jellyfin.SemanticDiscovery.Setup.csproj")
    .WithEnvironment("JELLYFIN_URL", jellyfin.GetEndpoint("http"))
    .WithEnvironment("JELLYFIN_ADMIN_PASSWORD", adminPassword)
    .WithEnvironment("JELLYFIN_LIBRARY_MANIFEST", manifest)
    .WithEnvironment("JELLYFIN_CREDENTIALS_PATH", Path.Combine(data, "jellyfin-login.json"))
    .WaitFor(jellyfin)
    .ExcludeFromManifest();

builder.Build().Run();

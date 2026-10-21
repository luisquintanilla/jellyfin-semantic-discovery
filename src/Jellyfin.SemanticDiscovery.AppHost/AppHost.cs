using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using System.Globalization;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

string repositoryRoot = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", ".."));
string dataRoot = Path.GetFullPath(
    Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_DATA")
    ?? Path.Combine(repositoryRoot, ".data"));
Directory.CreateDirectory(dataRoot);
string profile = (
    Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_PROFILE")
    ?? "synthetic").ToLowerInvariant();
if (profile is not ("synthetic" or "showcase"))
{
    throw new InvalidOperationException(
        "SEMANTIC_DISCOVERY_PROFILE must be synthetic or showcase.");
}
bool useShowcase = profile == "showcase";
string modelDirectory = Path.Combine(dataRoot, "models", "all-MiniLM-L6-v2");
string showcaseRoot = Path.GetFullPath(
    Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_SHOWCASE_ROOT")
    ?? Path.Combine(dataRoot, "showcase"));
string fixtureOutput = useShowcase
    ? showcaseRoot
    : Path.Combine(dataRoot, "media");
string mediaDirectory = Path.Combine(fixtureOutput, "library");
string jellyfinConfigDirectory = Path.Combine(dataRoot, "jellyfin", "config");
string jellyfinCacheDirectory = Path.Combine(dataRoot, "jellyfin", "cache");
string buildConfiguration =
    Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_CONFIGURATION")
    ?? "Debug";
if (buildConfiguration is not ("Debug" or "Release"))
{
    throw new InvalidOperationException(
        "SEMANTIC_DISCOVERY_CONFIGURATION must be Debug or Release.");
}

Directory.CreateDirectory(fixtureOutput);
Directory.CreateDirectory(jellyfinConfigDirectory);
Directory.CreateDirectory(jellyfinCacheDirectory);
string pluginOutput = Path.Combine(
    repositoryRoot,
    "src",
    "Jellyfin.SemanticDiscovery.Plugin",
    "bin",
    buildConfiguration,
    "net10.0");
string pluginDirectory = Path.Combine(
    dataRoot,
    "plugin",
    "Semantic Discovery_0.1.0.0");
string credentialsPath = Path.Combine(dataRoot, "jellyfin-login.json");
IResourceBuilder<ParameterResource> adminPassword = builder.AddParameter(
    "jellyfin-admin-password",
    new GenerateParameterDefault
    {
        MinLength = 24,
        Lower = true,
        Upper = true,
        Numeric = true,
        Special = false,
        MinLower = 4,
        MinUpper = 4,
        MinNumeric = 4,
    },
    secret: true,
    persist: true);

IResourceBuilder<ProjectResource> modelSetup = builder
    .AddProject<Projects.Jellyfin_SemanticDiscovery_Setup>("model-setup")
    .WithArgs("model", "--model-dir", modelDirectory)
    .ExcludeFromManifest();

IResourceBuilder<ContainerResource>? fixtureSetup = null;
IResourceBuilder<ProjectResource>? showcaseSetup = null;
if (useShowcase)
{
    showcaseSetup = builder
        .AddProject<Projects.Jellyfin_SemanticDiscovery_Setup>(
            "showcase-setup")
        .WithArgs(
            "showcase",
            "--source-lock",
            Path.Combine(
                repositoryRoot,
                "assets",
                "showcase",
                "sources.lock.json"),
            "--output",
            fixtureOutput)
        .ExcludeFromManifest();
}
else
{
    fixtureSetup = builder
        .AddDockerfile(
            "fixture-setup",
            repositoryRoot,
            Path.Combine("containers", "fixture-generator", "Dockerfile"))
        .WithBindMount(fixtureOutput, "/output")
        .WithArgs(
            "-ManifestPath",
            "/opt/fixture-generator/manifest.json",
            "-OutputRoot",
            "/output",
            "-Mode",
            "Generate")
        .ExcludeFromManifest();
}

IResourceBuilder<ProjectResource> pluginSetup = builder
    .AddProject<Projects.Jellyfin_SemanticDiscovery_Setup>("plugin-setup")
    .WithArgs(
        "plugin",
        "--source",
        pluginOutput,
        "--destination",
        pluginDirectory)
    .ExcludeFromManifest();

IResourceBuilder<ContainerResource> markItDown = builder
    .AddDockerfile(
        "markitdown",
        Path.Combine(repositoryRoot, "containers", "markitdown"))
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpHealthCheck("/health")
    .WithEnvironment(
        "MAX_DOCUMENT_BYTES",
        (useShowcase ? 32 * 1024 * 1024 : 8 * 1024 * 1024)
            .ToString(CultureInfo.InvariantCulture))
    .WithEnvironment(
        "MAX_REQUEST_BYTES",
        (useShowcase ? 48 * 1024 * 1024 : 12 * 1024 * 1024)
            .ToString(CultureInfo.InvariantCulture))
    .WithEnvironment(
        "MAX_CONCURRENT_CONVERSIONS",
        useShowcase ? "1" : "2")
    .WithEnvironment("HTTP_PROXY", string.Empty)
    .WithEnvironment("HTTPS_PROXY", string.Empty)
    .WithEnvironment("ALL_PROXY", string.Empty)
    .WithEnvironment("NO_PROXY", "*")
    .WithEnvironment("OPENAI_API_KEY", string.Empty)
    .WithEnvironment("AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT", string.Empty)
    .WithEnvironment("AZURE_DOCUMENT_INTELLIGENCE_KEY", string.Empty);

IResourceBuilder<ContainerResource> jellyfin = builder
    .AddContainer(
        "jellyfin",
        "jellyfin/jellyfin",
        "12.0.20260908-012347")
    .WithImageSHA256(
        "97150310f89f908abbf6fa9640ccc36f9bfa9e5e987775db310b3b70c2d11602")
    .WithHttpEndpoint(targetPort: 8096, name: "http")
    .WithHttpHealthCheck("/System/Ping")
    .WithBindMount(jellyfinConfigDirectory, "/config")
    .WithBindMount(jellyfinCacheDirectory, "/cache")
    .WithBindMount(
        pluginDirectory,
        "/config/plugins/Semantic Discovery_0.1.0.0",
        isReadOnly: false)
    .WithBindMount(
        Path.GetDirectoryName(modelDirectory)!,
        "/models",
        isReadOnly: true)
    .WithBindMount(mediaDirectory, "/media", isReadOnly: true)
    .WithEnvironment("SEMANTIC_DISCOVERY_STARTUP_SMOKE", "true")
    .WithEnvironment("SEMANTIC_DISCOVERY_MODEL_DIRECTORY", "/models/all-MiniLM-L6-v2")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_MARKITDOWN_ENDPOINT",
        "http://markitdown:8080/mcp")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_MAX_DOCUMENT_BYTES",
        (useShowcase ? 32 * 1024 * 1024 : 8 * 1024 * 1024)
            .ToString(CultureInfo.InvariantCulture))
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_MAX_REQUEST_BYTES",
        (useShowcase ? 48 * 1024 * 1024 : 12 * 1024 * 1024)
            .ToString(CultureInfo.InvariantCulture))
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_MARKITDOWN_TIMEOUT_SECONDS",
        useShowcase ? "600" : "120")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_RECOMMENDATION_FLOOR",
        useShowcase ? "0.38" : "0.5")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_INGEST_BATCH_SIZE",
        useShowcase ? "32" : "8")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_ONNX_INTRA_OP_THREADS",
        useShowcase ? "2" : "1")
    .WithEnvironment("HTTP_PROXY", "http://127.0.0.1:9")
    .WithEnvironment("HTTPS_PROXY", "http://127.0.0.1:9")
    .WithEnvironment("ALL_PROXY", "http://127.0.0.1:9")
    .WithEnvironment("NO_PROXY", "markitdown,localhost,127.0.0.1")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_EGRESS_CANARY_URL",
        "https://example.com/")
    .WaitFor(markItDown)
    .WaitForCompletion(modelSetup)
    .WaitForCompletion(pluginSetup);
if (useShowcase)
{
    jellyfin.WaitForCompletion(showcaseSetup!);
}
else
{
    jellyfin.WaitForCompletion(fixtureSetup!);
}

IResourceBuilder<ProjectResource> bootstrap = builder
    .AddProject<Projects.Jellyfin_SemanticDiscovery_Setup>("jellyfin-bootstrap")
    .WithArgs("jellyfin")
    .WithEnvironment("JELLYFIN_URL", jellyfin.GetEndpoint("http"))
    .WithEnvironment("JELLYFIN_ADMIN_PASSWORD", adminPassword)
    .WithEnvironment("JELLYFIN_CREDENTIALS_PATH", credentialsPath)
    .WithEnvironment(
        "JELLYFIN_READY_TIMEOUT_SECONDS",
        useShowcase ? "1800" : "180")
    .WaitFor(jellyfin)
    .WaitFor(markItDown)
    .ExcludeFromManifest();
if (useShowcase)
{
    bootstrap.WithEnvironment(
        "JELLYFIN_LIBRARY_MANIFEST",
        Path.Combine(fixtureOutput, "jellyfin-library.json"));
}

builder.Build().Run();

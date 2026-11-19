using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using CommunityToolkit.Aspire.Hosting.Ollama;

IDistributedApplicationBuilder builder =
    DistributedApplication.CreateBuilder(args);

string repositoryRoot = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", ".."));
string dataRoot = Path.GetFullPath(
    Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_DATA")
    ?? Path.Combine(repositoryRoot, ".data"));
string profile = (
    Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_PROFILE")
    ?? "demo").ToLowerInvariant();
if (profile is not ("demo" or "ci"))
{
    throw new InvalidOperationException(
        "SEMANTIC_DISCOVERY_PROFILE must be 'demo' or 'ci'.");
}

string hardwareProfile = (
    Environment.GetEnvironmentVariable(
        "SEMANTIC_DISCOVERY_HARDWARE_PROFILE")
    ?? "standard").ToLowerInvariant();
string visionModel = hardwareProfile switch
{
    "standard" => "qwen3-vl:8b",
    "lower-memory" => "qwen3-vl:4b",
    _ => throw new InvalidOperationException(
        "SEMANTIC_DISCOVERY_HARDWARE_PROFILE must be " +
        "'standard' or 'lower-memory'."),
};

string manifestPath = profile == "demo"
    ? Path.Combine(repositoryRoot, "assets", "demo", "manifest.json")
    : Path.Combine(
        repositoryRoot,
        "assets",
        "fixtures",
        "extraction-manifest.json");
string fixtureOutput = Path.Combine(dataRoot, "fixtures");
string mediaDirectory = profile == "demo"
    ? Path.Combine(repositoryRoot, "assets", "demo", "library")
    : Path.Combine(fixtureOutput, "library");
string extractionState = Path.Combine(dataRoot, "extractions");
string extractionPublished = Path.Combine(
    extractionState,
    "published");
string pdfRasterExecutable =
    Environment.GetEnvironmentVariable(
        "SEMANTIC_DISCOVERY_PDF_RASTER_EXECUTABLE")
    ?? "pdftoppm";
string jellyfinConfigDirectory = Path.Combine(
    dataRoot,
    "jellyfin",
    "config");
string jellyfinCacheDirectory = Path.Combine(
    dataRoot,
    "jellyfin",
    "cache");
string pluginDirectory = Path.Combine(
    dataRoot,
    "plugin",
    "Semantic Discovery_0.1.0.0");
string buildConfiguration =
    Environment.GetEnvironmentVariable(
        "SEMANTIC_DISCOVERY_CONFIGURATION")
    ?? "Debug";
if (buildConfiguration is not ("Debug" or "Release"))
{
    throw new InvalidOperationException(
        "SEMANTIC_DISCOVERY_CONFIGURATION must be Debug or Release.");
}

Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(extractionState);
Directory.CreateDirectory(extractionPublished);
Directory.CreateDirectory(jellyfinConfigDirectory);
Directory.CreateDirectory(jellyfinCacheDirectory);
Directory.CreateDirectory(pluginDirectory);

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

IResourceBuilder<OllamaResource> ollama = builder
    .AddOllama("ollama")
    .WithDataVolume();
IResourceBuilder<OllamaModelResource> embeddingModel =
    ollama.AddModel("embedding-model", "embeddinggemma:latest");
IResourceBuilder<OllamaModelResource> extractionModel =
    ollama.AddModel("vision-model", visionModel);

IResourceBuilder<ContainerResource>? fixtureSetup = null;
if (profile == "ci")
{
    fixtureSetup = builder
        .AddDockerfile(
            "fixture-setup",
            repositoryRoot,
            Path.Combine(
                "containers",
                "fixture-generator",
                "Dockerfile"))
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

string pluginOutput = Path.Combine(
    repositoryRoot,
    "src",
    "Jellyfin.SemanticDiscovery.Plugin",
    "bin",
    buildConfiguration,
    "net10.0");
IResourceBuilder<DotnetProjectResource> pluginSetup = builder
    .AddDotnetProject(
        "plugin-setup",
        "../Jellyfin.SemanticDiscovery.Setup/" +
        "Jellyfin.SemanticDiscovery.Setup.csproj")
    .WithArgs(
        "plugin",
        "--source",
        pluginOutput,
        "--destination",
        pluginDirectory)
    .ExcludeFromManifest();

IResourceBuilder<DotnetProjectResource> extraction = builder
    .AddDotnetProject(
        "document-extraction",
        "../Jellyfin.SemanticDiscovery.Extraction/" +
        "Jellyfin.SemanticDiscovery.Extraction.csproj")
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health/ready")
    .WithReference(extractionModel)
    .WithEnvironment("Extraction__ManifestPath", manifestPath)
    .WithEnvironment("Extraction__StateRoot", extractionState)
    .WithEnvironment("Extraction__ArtifactRoot", extractionPublished)
    .WithEnvironment("Extraction__Concurrency", "1")
    .WithEnvironment(
        "Extraction__PdfRasterExecutable",
        pdfRasterExecutable)
    .WithEnvironment("Ollama__Model", visionModel)
    .WaitFor(extractionModel);
if (fixtureSetup is not null)
{
    extraction.WaitForCompletion(fixtureSetup);
}

IResourceBuilder<DotnetProjectResource> extractionSetup = builder
    .AddDotnetProject(
        "extraction-setup",
        "../Jellyfin.SemanticDiscovery.Setup/" +
        "Jellyfin.SemanticDiscovery.Setup.csproj")
    .WithArgs(
        "extract",
        "--manifest",
        manifestPath,
        "--profile",
        profile == "ci" ? "Text" : "Hybrid")
    .WithEnvironment(
        "EXTRACTION_ENDPOINT",
        extraction.GetEndpoint("http"))
    .WaitFor(extraction)
    .ExcludeFromManifest();

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
    .WithBindMount(mediaDirectory, "/media", isReadOnly: true)
    .WithBindMount(
        extractionPublished,
        "/semantic-extractions",
        isReadOnly: true)
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_OLLAMA_ENDPOINT",
        "http://ollama:11434")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_EMBEDDING_MODEL",
        "embeddinggemma:latest")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_EXTRACTION_MODEL",
        visionModel)
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_EXTRACTION_MODE",
        profile == "ci" ? "Text" : "Hybrid")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_EXTRACTION_ARTIFACT_ROOT",
        "/semantic-extractions")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_RECOMMENDATION_FLOOR",
        "0.50")
    .WithEnvironment(
        "SEMANTIC_DISCOVERY_INGEST_BATCH_SIZE",
        "8")
    .WaitFor(embeddingModel)
    .WaitForCompletion(extractionSetup)
    .WaitForCompletion(pluginSetup);
if (fixtureSetup is not null)
{
    jellyfin.WaitForCompletion(fixtureSetup);
}

string credentialsPath = Path.Combine(
    dataRoot,
    "jellyfin-login.json");
IResourceBuilder<DotnetProjectResource> bootstrap = builder
    .AddDotnetProject(
        "jellyfin-bootstrap",
        "../Jellyfin.SemanticDiscovery.Setup/" +
        "Jellyfin.SemanticDiscovery.Setup.csproj")
    .WithArgs("jellyfin")
    .WithEnvironment("JELLYFIN_URL", jellyfin.GetEndpoint("http"))
    .WithEnvironment("JELLYFIN_ADMIN_PASSWORD", adminPassword)
    .WithEnvironment("JELLYFIN_CREDENTIALS_PATH", credentialsPath)
    .WithEnvironment(
        "JELLYFIN_LIBRARY_MANIFEST",
        manifestPath)
    .WithEnvironment(
        "JELLYFIN_READY_TIMEOUT_SECONDS",
        profile == "demo"
            ? 600.ToString(CultureInfo.InvariantCulture)
            : 180.ToString(CultureInfo.InvariantCulture))
    .WaitFor(jellyfin)
    .WaitFor(extraction)
    .ExcludeFromManifest();

builder.Build().Run();

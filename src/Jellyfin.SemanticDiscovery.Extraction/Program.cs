using System.Data.Common;
using System.Text.Json.Serialization;
using Jellyfin.SemanticDiscovery.Extraction.Api;
using Jellyfin.SemanticDiscovery.Extraction.Artifacts;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Extraction;
using Jellyfin.SemanticDiscovery.Extraction.Infrastructure;
using Jellyfin.SemanticDiscovery.Extraction.Jobs;
using Jellyfin.SemanticDiscovery.Extraction.Manifest;
using Jellyfin.SemanticDiscovery.Extraction.Pdf;
using Jellyfin.SemanticDiscovery.Extraction.Persistence;
using Jellyfin.SemanticDiscovery.Extraction.Validation;
using Jellyfin.SemanticDiscovery.Extraction.Worker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<ExtractionOptions>()
    .Bind(builder.Configuration.GetSection(ExtractionOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName))
    .Configure(options =>
    {
        string? connectionString =
            builder.Configuration.GetConnectionString("vision-model");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        DbConnectionStringBuilder connection = new()
        {
            ConnectionString = connectionString,
        };
        if (connection["Endpoint"] is string endpoint)
        {
            options.Endpoint = new Uri(endpoint, UriKind.Absolute);
        }

        if (connection["Model"] is string model)
        {
            options.Model = model;
        }
    })
    .ValidateDataAnnotations()
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Model),
        "Ollama:Model is required; no qwen or other model fallback is used.")
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling =
        JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton(
    serviceProvider =>
        serviceProvider.GetRequiredService<IOptions<ExtractionOptions>>().Value);
builder.Services.AddSingleton(
    serviceProvider =>
        serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value);
builder.Services.AddSingleton<IAtomicFileOperations, AtomicFileOperations>();
builder.Services.AddSingleton<AtomicJsonFile>();
builder.Services.AddSingleton<IExtractionManifestCatalog>(serviceProvider =>
{
    ExtractionOptions options =
        serviceProvider.GetRequiredService<ExtractionOptions>();
    return new JsonExtractionManifestCatalog(options.ManifestPath);
});
builder.Services.AddSingleton<IExtractionJobRepository>(serviceProvider =>
{
    ExtractionOptions options =
        serviceProvider.GetRequiredService<ExtractionOptions>();
    return new FileExtractionJobRepository(
        options.StateRoot,
        serviceProvider.GetRequiredService<AtomicJsonFile>());
});
builder.Services.AddSingleton<IExtractionJobChannel>(serviceProvider =>
{
    ExtractionOptions options =
        serviceProvider.GetRequiredService<ExtractionOptions>();
    return new BoundedExtractionJobChannel(options.QueueCapacity);
});
builder.Services.AddSingleton<IExtractionCancellationRegistry,
    ExtractionCancellationRegistry>();
builder.Services.AddSingleton<PdfPigTextLayerExtractor>();
builder.Services.AddSingleton<IPdfDocumentInspector>(
    serviceProvider =>
        serviceProvider.GetRequiredService<PdfPigTextLayerExtractor>());
builder.Services.AddSingleton<IPdfTextLayerExtractor>(
    serviceProvider =>
        serviceProvider.GetRequiredService<PdfPigTextLayerExtractor>());
builder.Services.AddSingleton<IPdfRasterizer>(serviceProvider =>
{
    ExtractionOptions options =
        serviceProvider.GetRequiredService<ExtractionOptions>();
    return new PopplerPdfRasterizer(options.PdfRasterExecutable);
});
builder.Services.AddHttpClient(
    nameof(OllamaChatClient),
    (serviceProvider, httpClient) =>
    {
        OllamaOptions options =
            serviceProvider.GetRequiredService<OllamaOptions>();
        httpClient.Timeout = TimeSpan.FromMinutes(
            options.RequestTimeoutMinutes);
    });
builder.Services.AddSingleton<IChatClient>(serviceProvider =>
{
    OllamaOptions options =
        serviceProvider.GetRequiredService<OllamaOptions>();
    HttpClient httpClient = serviceProvider
        .GetRequiredService<IHttpClientFactory>()
        .CreateClient(nameof(OllamaChatClient));
    return new OllamaChatClient(
        httpClient,
        options.Endpoint,
        options.Model);
});
builder.Services.AddSingleton<IVisionPageExtractor,
    VisionLanguagePageExtractor>();
builder.Services.AddSingleton<IProvenanceMerger, ProvenanceMerger>();
builder.Services.AddSingleton<IExtractionSourceValidator,
    ExtractionSourceValidator>();
builder.Services.AddSingleton<IDocumentExtractionPipeline,
    DocumentExtractionPipeline>();
builder.Services.AddSingleton<ICompletedArtifactPublisher>(serviceProvider =>
{
    ExtractionOptions options =
        serviceProvider.GetRequiredService<ExtractionOptions>();
    return new FileCompletedArtifactPublisher(
        options.ArtifactRoot,
        serviceProvider.GetRequiredService<AtomicJsonFile>());
});
builder.Services.AddSingleton<IExtractionCoordinator,
    ExtractionCoordinator>();
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton<IReadinessState>(
    serviceProvider => serviceProvider.GetRequiredService<ReadinessState>());
builder.Services.AddHostedService<ExtractionWorker>();

WebApplication app = builder.Build();
app.MapExtractionEndpoints();
await app.RunAsync().ConfigureAwait(false);

public partial class Program;

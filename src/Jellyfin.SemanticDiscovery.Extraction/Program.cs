using System.Text.Json.Serialization;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Extraction.Api;
using Jellyfin.SemanticDiscovery.Extraction.Artifacts;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Extraction;
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
builder.Services.ConfigureHttpClientDefaults(
    httpClient => httpClient.ConfigureHttpClient(
        client => client.Timeout = TimeSpan.FromMinutes(10)));
builder.AddOllamaApiClient("vision-model")
    .AddChatClient()
    .ConfigureOptions(options =>
    {
        options.Temperature ??= 0F;
        options.Seed ??= 42;
        options.MaxOutputTokens ??= 768;
        options.Reasoning ??= new() { Effort = ReasoningEffort.None };
    });
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling =
        JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton(
    serviceProvider =>
        serviceProvider.GetRequiredService<IOptions<ExtractionOptions>>().Value);
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
builder.Services.AddSingleton<IVisionPageExtractor,
    VisionLanguagePageExtractor>();
builder.Services.AddSingleton<IProvenanceMerger, ProvenanceMerger>();
builder.Services.AddSingleton<ApplicationChunkTokenizer>();
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
_ = app.Services.GetRequiredService<IExtractionCoordinator>();
app.MapExtractionEndpoints();
await app.RunAsync().ConfigureAwait(false);

public partial class Program;

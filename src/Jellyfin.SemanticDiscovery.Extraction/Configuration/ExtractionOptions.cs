using System.ComponentModel.DataAnnotations;

namespace Jellyfin.SemanticDiscovery.Extraction.Configuration;

public sealed class ExtractionOptions
{
    public const string SectionName = "Extraction";

    [Required]
    public string ManifestPath { get; set; } = string.Empty;

    [Required]
    public string StateRoot { get; set; } = string.Empty;

    [Required]
    public string ArtifactRoot { get; set; } = string.Empty;

    [Range(1, 1024)]
    public int QueueCapacity { get; set; } = 32;

    [Range(1, 32)]
    public int Concurrency { get; set; } = 1;

    [Range(1, 10_000)]
    public int MaximumPages { get; set; } = 200;

    [Range(1, long.MaxValue)]
    public long MaximumSourceBytes { get; set; } = 100 * 1024 * 1024;

    [Range(72, 600)]
    public int RasterDpi { get; set; } = 144;

    public string PdfRasterExecutable { get; set; } = "pdftoppm";
}

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    [Required]
    public Uri Endpoint { get; set; } = new("http://localhost:11434");

    [Required]
    public string Model { get; set; } = string.Empty;

    [Range(1, 60)]
    public int RequestTimeoutMinutes { get; set; } = 10;
}

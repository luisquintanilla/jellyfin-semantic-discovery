using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace Jellyfin.SemanticDiscovery.SemanticService.Reading;

/// <summary>
/// Middleware that checks the result. If a PDF came back with almost no text, it's
/// probably a scan, so the same bytes go to the vision reader instead.
/// </summary>
internal sealed partial class ScannedPdfFallback(
    DocumentExtractionClient inner,
    DocumentExtractionClient vision,
    int minimumCharacters,
    bool forceVision,
    ILogger<ScannedPdfFallback> logger) : DelegatingDocumentExtractionClient(inner)
{
    public override async Task<ExtractionDocument> ExtractAsync(
        Stream stream, string identifier, string mediaType,
        DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Buffer the input so it can be read twice.
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        ExtractionDocument document = await base.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
        int characters = document.EnumerateContent().Sum(e => e.Content is TextContent t ? t.Text.Trim().Length : 0);
        if (characters > minimumCharacters && !forceVision)
        {
            return document;
        }

        LogFallingBack(identifier, characters);
        Stopwatch timer = Stopwatch.StartNew();
        buffer.Position = 0;
        document = await vision.ExtractAsync(buffer, identifier, mediaType, options, cancellationToken);
        LogVisionRead(identifier, document.Sections.Count, timer.ElapsedMilliseconds);
        return document;
    }

    [LoggerMessage(LogLevel.Information, "Reading {Identifier} with the vision model (text layer: {Characters} characters).")]
    private partial void LogFallingBack(string identifier, int characters);

    [LoggerMessage(LogLevel.Information, "Vision model read {Identifier}: {Sections} sections in {ElapsedMs} ms.")]
    private partial void LogVisionRead(string identifier, int sections, long elapsedMs);
}

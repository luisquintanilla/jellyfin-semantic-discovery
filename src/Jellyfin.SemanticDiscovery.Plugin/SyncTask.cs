using System.Xml.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.SemanticDiscovery.Plugin;

/// <summary>
/// Sends every movie, series, song, and book to the semantic service: item text to
/// <c>/index/media</c>, and each book's PDF to <c>/index/books/{id}</c>.
/// </summary>
public sealed class SyncTask(SemanticServiceClient service, ILibraryManager libraryManager) : IScheduledTask
{
    public string Name => "Sync semantic index";

    public string Key => "SemanticDiscoveryRebuild";

    public string Description => "Sends library metadata, transcripts, and book PDFs to the semantic service.";

    public string Category => "Semantic Discovery";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Audio, BaseItemKind.Book],
            Recursive = true,
            IsVirtualItem = false,
        });

        await service.IndexMediaAsync(items.Select(item => new MediaItem(item.Id, item.Name, Describe(item))).ToArray(), cancellationToken);

        BaseItem[] books = items.Where(item => item.GetBaseItemKind() == BaseItemKind.Book
            && string.Equals(Path.GetExtension(item.Path), ".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        for (int i = 0; i < books.Length; i++)
        {
            await service.IndexBookAsync(books[i].Id, books[i].Path, cancellationToken);
            progress.Report(100.0 * (i + 1) / books.Length);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new() { Type = TaskTriggerInfoType.StartupTrigger },
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks },
    ];

    /// <summary>The text we embed for an item: overview, genres, tags, year, and the transcript if there is one.</summary>
    internal static string Describe(BaseItem item)
    {
        // Music libraries don't read .nfo files, so fall back to the sidecar's <plot>.
        string overview = !string.IsNullOrWhiteSpace(item.Overview) ? item.Overview
            : Sidecar(item.Path, ".nfo") is { } nfo ? XDocument.Load(nfo).Root?.Element("plot")?.Value ?? ""
            : "";
        string? folder = Directory.Exists(item.Path) ? item.Path : Path.GetDirectoryName(item.Path);
        string? vtt = folder is not null && Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.vtt").FirstOrDefault()
            : null;

        return string.Join('\n', new[]
        {
            overview,
            item.Genres.Length > 0 ? $"Genres: {string.Join(", ", item.Genres)}" : "",
            item.Tags.Length > 0 ? $"Tags: {string.Join(", ", item.Tags)}" : "",
            item.ProductionYear is { } year ? $"Year: {year}" : "",
            vtt is null ? "" : Transcript(File.ReadAllLines(vtt)),
        }.Where(line => line.Length > 0));
    }

    /// <summary>WebVTT minus the header, cue numbers, and timestamps.</summary>
    internal static string Transcript(IEnumerable<string> lines) => string.Join(' ', lines
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && line != "WEBVTT" && !line.Contains("-->") && !line.All(char.IsDigit)));

    private static string? Sidecar(string? path, string extension)
    {
        string? sidecar = path is null ? null : Path.ChangeExtension(path, extension);
        return sidecar is not null && File.Exists(sidecar) ? sidecar : null;
    }
}

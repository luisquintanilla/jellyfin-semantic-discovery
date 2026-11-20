using Jellyfin.Data.Enums;
using Jellyfin.SemanticDiscovery.Plugin;
using MediaBrowser.Controller.Library;

namespace Jellyfin.SemanticDiscovery.Tests;

public sealed class PluginTests
{
    private static readonly Guid Movie = Guid.NewGuid();
    private static readonly Guid Book = Guid.NewGuid();
    private static readonly Guid Hidden = Guid.NewGuid();

    private static readonly SearchHit[] Hits =
    [
        new(Movie, 0.62, null, null),
        new(Hidden, 0.58, null, null),
        new(Book, 1.20, 4, "page four"),
    ];

    // Hidden stands in for an item in a library the user has no access to: GetItemById(id, user) is null.
    private static BaseItemKind? KindForUser(Guid id) =>
        id == Movie ? BaseItemKind.Movie : id == Book ? BaseItemKind.Book : null;

    [Fact]
    public void Search_results_drop_items_the_user_cannot_see_and_scale_scores()
    {
        SearchResult[] results = SemanticSearchProvider.Visible(Hits, KindForUser, [], []);

        Assert.Equal([Movie, Book], results.Select(r => r.ItemId));
        Assert.Equal(62f, results[0].Score, 3);
        Assert.Equal(99f, results[1].Score);
    }

    [Fact]
    public void Search_results_respect_include_and_exclude_types()
    {
        Assert.Equal([Book], SemanticSearchProvider.Visible(Hits, KindForUser, [BaseItemKind.Book], []).Select(r => r.ItemId));
        Assert.Equal([Movie], SemanticSearchProvider.Visible(Hits, KindForUser, [], [BaseItemKind.Book]).Select(r => r.ItemId));
    }

    [Fact]
    public void Transcript_keeps_only_the_spoken_text()
    {
        string[] vtt = ["WEBVTT", "", "1", "00:00:00.000 --> 00:00:02.000", "The lighthouse keeper waits.", "", "2", "00:00:02.000 --> 00:00:04.000", " Then the fog lifts. "];

        Assert.Equal("The lighthouse keeper waits. Then the fog lifts.", SyncTask.Transcript(vtt));
    }
}

using Jellyfin.SemanticDiscovery.SemanticService.Ingestion;
using Jellyfin.SemanticDiscovery.SemanticService.Search;
using Microsoft.Extensions.DataIngestion;

namespace Jellyfin.SemanticDiscovery.Tests;

public sealed class SearchTests
{
    private static readonly Guid Book = Guid.NewGuid();
    private static readonly Guid Movie = Guid.NewGuid();

    [Fact]
    public void Merge_keeps_the_best_hit_per_item_and_orders_by_score()
    {
        SearchHit[] hits =
        [
            new(Book, 0.41, null, null),
            new(Movie, 0.52, null, null),
            new(Book, 0.63, 4, "page four"),
            new(Book, 0.47, 2, "page two"),
        ];

        IReadOnlyList<SearchHit> merged = SemanticSearch.Merge(hits, minScore: 0.35, limit: 10);

        Assert.Equal([new(Book, 0.63, 4, "page four"), new(Movie, 0.52, null, null)], merged);
    }

    [Fact]
    public void Merge_drops_weak_hits_and_applies_the_limit()
    {
        SearchHit[] hits = [new(Book, 0.30, 1, null), new(Movie, 0.50, null, null), new(Guid.NewGuid(), 0.40, null, null)];

        IReadOnlyList<SearchHit> merged = SemanticSearch.Merge(hits, minScore: 0.35, limit: 1);

        Assert.Equal(Movie, Assert.Single(merged).Id);
    }

    [Fact]
    public void PageOf_returns_the_page_of_the_first_element_in_the_chunk()
    {
        IngestionDocument document = new("book");
        IngestionDocumentSection section = new();
        section.Elements.Add(new IngestionDocumentParagraph("Page three explains how embeddings turn text into vectors for search.\n") { PageNumber = 3 });
        section.Elements.Add(new IngestionDocumentParagraph("Page four talks about vision models.\n") { PageNumber = 4 });
        document.Sections.Add(section);

        IngestionChunk<string> chunk = new("Page four talks about vision models.", document, "");
        IngestionChunk<string> spanning = new("three explains how embeddings turn text into vectors for search.\nPage four talks about vision models.", document, "");

        Assert.Equal(4, PageNumberProcessor.PageOf(chunk));
        Assert.Equal(3, PageNumberProcessor.PageOf(spanning));
    }
}

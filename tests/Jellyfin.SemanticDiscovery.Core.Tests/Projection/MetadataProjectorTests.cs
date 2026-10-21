using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Projection;

public sealed class MetadataProjectorTests
{
    [Fact]
    public void ProjectOrdersFieldsDeterministicallyWhenAllFieldsArePopulated()
    {
        MetadataRecord record = TestData.Movie(
            title: "The Movie",
            overview: "An overview.",
            genres: ["Action"],
            tags: ["Tag"],
            people: ["Person"],
            studios: ["Studio"]) with
        {
            OriginalTitle = "Original Title",
            ProductionYear = 1999,
        };

        string projection = MetadataProjector.Project(record);

        string[] lines = projection.Split('\n');
        Assert.Equal(
            [
                "title: The Movie",
                "original title: Original Title",
                "type: Movie",
                "media type: Video",
                "year: 1999",
                "overview: An overview.",
                "genres: Action",
                "tags: Tag",
                "people: Person",
                "studios: Studio",
            ],
            lines);
    }

    [Fact]
    public void ProjectSkipsBlankScalarFields()
    {
        MetadataRecord record = TestData.Movie(title: "Only Title") with
        {
            OriginalTitle = "   ",
            Overview = string.Empty,
            ProductionYear = null,
        };

        string projection = MetadataProjector.Project(record);

        Assert.DoesNotContain("original title", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("year:", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("overview", projection, StringComparison.Ordinal);
        Assert.Equal("title: Only Title\ntype: Movie\nmedia type: Video", projection);
    }

    [Fact]
    public void ProjectNormalizesWhitespaceAndUnicodeCompatibility()
    {
        // U+FF41 (fullwidth "a") normalizes under FormKC to U+0061 ("a"), and
        // interior tabs/newlines/repeated spaces collapse to single spaces.
        MetadataRecord record = TestData.Movie(
            title: "Title",
            overview: "line one\t\tline\ntwo   \uFF41nd  three");

        string projection = MetadataProjector.Project(record);

        // All internal whitespace runs (tabs, newlines, repeated spaces)
        // collapse to a single space within the field's value.
        Assert.Contains("overview: line one line two and three", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectSkipsBlankSetEntries()
    {
        MetadataRecord record = TestData.Movie(
            title: "Title",
            genres: ["Action", "   ", string.Empty]);

        string projection = MetadataProjector.Project(record);

        Assert.Contains("genres: Action", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("genres: Action, ", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectDeduplicatesAndSortsSetFieldsCaseInsensitivelyRegardlessOfInputOrder()
    {
        MetadataRecord recordA = TestData.Movie(
            title: "Title",
            genres: ["zeta", "Alpha", "ALPHA", "beta"]);
        MetadataRecord recordB = TestData.Movie(
            title: "Title",
            genres: ["beta", "alpha", "Zeta", "Alpha"]);

        string projectionA = MetadataProjector.Project(recordA);
        string projectionB = MetadataProjector.Project(recordB);

        // Case-insensitive dedup keeps the first-seen casing, then results
        // are ordered case-insensitively, independent of input order.
        Assert.Contains("genres: Alpha, beta, zeta", projectionA, StringComparison.Ordinal);
        Assert.Contains("genres: alpha, beta, Zeta", projectionB, StringComparison.Ordinal);

        // Same case-insensitive value set produces the same normalized order
        // regardless of casing/order permutations in the source lists.
        string[] genreLineA = projectionA.Split('\n')
            .Single(static line => line.StartsWith("genres", StringComparison.Ordinal))
            .Split(", ");
        string[] genreLineB = projectionB.Split('\n')
            .Single(static line => line.StartsWith("genres", StringComparison.Ordinal))
            .Split(", ");
        Assert.Equal(
            genreLineA.Select(static value => value.ToUpperInvariant()),
            genreLineB.Select(static value => value.ToUpperInvariant()));
    }

    [Fact]
    public void ProjectThrowsForNullItem()
    {
        Assert.Throws<ArgumentNullException>(static () => MetadataProjector.Project(null!));
    }
}

using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Retrieval;

public sealed class SearchScopeTests
{
    [Fact]
    public void MatchesStructureTreatsEmptyParentIdAsUnscoped()
    {
        SearchScope scope = new() { ParentId = Guid.Empty };

        Assert.True(scope.MatchesStructure(TestData.Movie()));
    }

    [Fact]
    public void MatchesStructureDefaultsToAllowingEveryKind()
    {
        SearchScope scope = new();

        Assert.True(scope.MatchesStructure(TestData.Movie()));
        Assert.True(scope.MatchesStructure(TestData.Book()));
    }

    [Fact]
    public void MatchesStructureFiltersByKind()
    {
        SearchScope scope = new() { Kinds = new HashSet<MediaKind> { MediaKind.Book } };

        Assert.False(scope.MatchesStructure(TestData.Movie()));
        Assert.True(scope.MatchesStructure(TestData.Book()));
    }

    [Fact]
    public void MatchesStructureIncludeItemTypesIsAnAllowList()
    {
        SearchScope scope = new()
        {
            IncludeItemTypes = new HashSet<string>(StringComparer.Ordinal) { "Movie" },
        };

        Assert.True(scope.MatchesStructure(TestData.Movie(itemType: "Movie")));
        Assert.False(scope.MatchesStructure(TestData.Movie(itemType: "Trailer")));
    }

    [Fact]
    public void MatchesStructureExcludeItemTypesOnlyAppliesWhenIncludeIsEmpty()
    {
        SearchScope scope = new()
        {
            ExcludeItemTypes = new HashSet<string>(StringComparer.Ordinal) { "Trailer" },
        };

        Assert.False(scope.MatchesStructure(TestData.Movie(itemType: "Trailer")));
        Assert.True(scope.MatchesStructure(TestData.Movie(itemType: "Movie")));
    }

    [Fact]
    public void MatchesStructureFiltersByMediaType()
    {
        SearchScope scope = new()
        {
            MediaTypes = new HashSet<string>(StringComparer.Ordinal) { "Video" },
        };

        Assert.True(scope.MatchesStructure(TestData.Movie(mediaType: "Video")));
        Assert.False(scope.MatchesStructure(TestData.Movie(mediaType: "Audio")));
    }

    [Fact]
    public void MatchesStructureFiltersByLibrary()
    {
        Guid libraryId = Guid.NewGuid();
        SearchScope scope = new() { LibraryId = libraryId };

        Assert.True(scope.MatchesStructure(TestData.Movie(libraryId: libraryId)));
        Assert.False(scope.MatchesStructure(TestData.Movie(libraryId: Guid.NewGuid())));
        Assert.False(scope.MatchesStructure(TestData.Movie(libraryId: null)));
    }

    [Fact]
    public void MatchesStructureFiltersByParentOrAncestor()
    {
        Guid parentId = Guid.NewGuid();
        SearchScope scope = new() { ParentId = parentId };

        Assert.True(scope.MatchesStructure(TestData.Movie(parentId: parentId)));
        Assert.True(scope.MatchesStructure(TestData.Movie(ancestorIds: [parentId])));
        Assert.False(scope.MatchesStructure(TestData.Movie(parentId: Guid.NewGuid())));
    }
}

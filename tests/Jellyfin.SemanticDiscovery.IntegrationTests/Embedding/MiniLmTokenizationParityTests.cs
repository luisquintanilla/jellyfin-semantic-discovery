using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

/// <summary>
/// Verifies the WordPiece tokenizer fix (RemoveNonSpacingMarks=true) makes
/// accented text tokenize identically to its ASCII-folded equivalent.
/// </summary>
[Collection(MiniLmModelCollectionDefinition.Name)]
public sealed class MiniLmTokenizationParityTests(MiniLmModelFixture fixture)
{
    private static readonly int[] ExpectedPinnedIds = [101, 7668, 10204, 102];

    [ModelFact]
    public void EncodeTokenIdsAccentedAndAsciiEquivalentsProduceIdenticalPinnedIds()
    {
        MiniLmEmbeddingGenerator generator = fixture.Generator!;

        IReadOnlyList<int> accentedIds = generator.EncodeTokenIds("café Zürich");
        IReadOnlyList<int> asciiIds = generator.EncodeTokenIds("cafe zurich");

        Assert.Equal(ExpectedPinnedIds, accentedIds);
        Assert.Equal(ExpectedPinnedIds, asciiIds);
        Assert.Equal(accentedIds, asciiIds);
        Assert.Equal(MiniLmSpecialTokenIds.Pinned, generator.SpecialTokenIds);
    }
}

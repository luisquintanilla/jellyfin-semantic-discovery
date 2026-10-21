using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.TestSupport;

/// <summary>
/// Deterministic whitespace-word token counter, mirroring the one used in
/// <c>Jellyfin.SemanticDiscovery.Core.Tests</c>, kept local here so this
/// project does not need a cross-test-project reference.
/// </summary>
public sealed class WordCountTokenCounter : ITokenCounter
{
    public int CountTokens(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}

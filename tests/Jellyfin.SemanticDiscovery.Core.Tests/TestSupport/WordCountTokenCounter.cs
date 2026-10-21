using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

/// <summary>
/// Deterministic whitespace-word token counter used across the test suite so
/// that "N tokens" in a test maps directly to "N space-separated words",
/// making boundary tests exact and easy to reason about.
/// </summary>
public sealed class WordCountTokenCounter : ITokenCounter
{
    public int CountTokens(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

public interface ITokenCounter
{
    int CountTokens(string text);
}

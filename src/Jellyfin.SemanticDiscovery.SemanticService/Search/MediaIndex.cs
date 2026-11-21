using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Jellyfin.SemanticDiscovery.SemanticService.Search;

/// <summary>What the Jellyfin plugin sends for each item: title plus overview, genres, and transcript text.</summary>
public sealed record MediaItem(Guid Id, string Title, string Text);

/// <summary>One Qdrant point per Jellyfin item, keyed by the Jellyfin item id.</summary>
public sealed class MediaRecord
{
    [VectorStoreKey]
    public Guid Id { get; set; }

    [VectorStoreData]
    public string Title { get; set; } = "";

    [VectorStoreData]
    public string Text { get; set; } = "";

    [VectorStoreVector(SemanticSearch.Dimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Vector { get; set; }
}

/// <summary>Embeds item text and upserts it into the <c>media</c> collection.</summary>
public sealed class MediaIndex(VectorStore store, IEmbeddingGenerator<string, Embedding<float>> embeddings)
{
    public const string CollectionName = "media";

    public VectorStoreCollection<Guid, MediaRecord> Collection { get; } = store.GetCollection<Guid, MediaRecord>(CollectionName);

    public async Task UpsertAsync(IReadOnlyList<MediaItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        await Collection.EnsureCollectionExistsAsync(cancellationToken);
        GeneratedEmbeddings<Embedding<float>> vectors = await embeddings.GenerateAsync(
            items.Select(item => $"{item.Title}\n{item.Text}"), cancellationToken: cancellationToken);

        await Collection.UpsertAsync(
            items.Select((item, i) => new MediaRecord { Id = item.Id, Title = item.Title, Text = item.Text, Vector = vectors[i].Vector }),
            cancellationToken);
    }
}

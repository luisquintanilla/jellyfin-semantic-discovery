using System.Numerics.Tensors;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;

namespace Jellyfin.SemanticDiscovery.Core.Retrieval;

internal sealed class RecommendationMatrix
{
    private readonly Guid[] _itemIds;
    private readonly float[] _values;

    private RecommendationMatrix(Guid[] itemIds, float[] values)
    {
        _itemIds = itemIds;
        _values = values;
        Values = Tensor.Create(
            values,
            [(nint)itemIds.Length, SemanticConstants.EmbeddingDimensions]);
    }

    public Tensor<float> Values { get; }

    public static RecommendationMatrix Create(
        IReadOnlyList<MetadataRecord> orderedItems,
        IReadOnlyDictionary<Guid, ReadOnlyMemory<float>> vectors)
    {
        ArgumentNullException.ThrowIfNull(orderedItems);
        ArgumentNullException.ThrowIfNull(vectors);
        Guid[] itemIds = orderedItems.Select(static item => item.Id).ToArray();
        float[] values = new float[
            checked(itemIds.Length * SemanticConstants.EmbeddingDimensions)];
        for (int row = 0; row < itemIds.Length; row++)
        {
            if (!vectors.TryGetValue(
                    itemIds[row],
                    out ReadOnlyMemory<float> vector) ||
                vector.Length != SemanticConstants.EmbeddingDimensions)
            {
                throw new InvalidDataException(
                    $"Missing or invalid recommendation vector for '{itemIds[row]}'.");
            }

            vector.Span.CopyTo(
                values.AsSpan(
                    row * SemanticConstants.EmbeddingDimensions,
                    SemanticConstants.EmbeddingDimensions));
        }

        return new RecommendationMatrix(itemIds, values);
    }

    public IReadOnlyList<(Guid ItemId, float Similarity)> Score(
        ReadOnlySpan<float> source)
    {
        if (source.Length != SemanticConstants.EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"Source vectors must contain " +
                $"{SemanticConstants.EmbeddingDimensions} values.",
                nameof(source));
        }

        List<(Guid ItemId, float Similarity)> scores =
            new(_itemIds.Length);
        for (int row = 0; row < _itemIds.Length; row++)
        {
            ReadOnlySpan<float> candidate = _values.AsSpan(
                row * SemanticConstants.EmbeddingDimensions,
                SemanticConstants.EmbeddingDimensions);
            scores.Add((
                _itemIds[row],
                VectorMath.CosineSimilarity(source, candidate)));
        }

        return scores;
    }
}

using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public sealed record MiniLmParityResult(
    int Dimensions,
    float MaximumAbsoluteError,
    float MaximumCosineError,
    float MaximumNormError);

public static class MiniLmParityVerifier
{
    private const float VectorTolerance = 0.0005F;
    private const float CosineTolerance = 0.0002F;
    private const float NormTolerance = 0.00001F;
    private const string ResourceName =
        "Jellyfin.SemanticDiscovery.Core.Assets.minilm-parity.json";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async ValueTask<MiniLmParityResult> VerifyAsync(
        MiniLmEmbeddingGenerator generator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ParityReference reference = await LoadReferenceAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<int> accentedIds = generator.EncodeTokenIds(reference.Texts[0]);
        if (!accentedIds.SequenceEqual(reference.TokenIds[0]) ||
            !generator.EncodeTokenIds(reference.Texts[1]).SequenceEqual(reference.TokenIds[1]) ||
            !accentedIds.SequenceEqual(generator.EncodeTokenIds(reference.Texts[1])))
        {
            throw new InvalidDataException(
                "The WordPiece tokenizer does not match the pinned accented-text reference.");
        }

        GeneratedEmbeddings<Embedding<float>> generated =
            await generator.GenerateAsync(
                reference.Texts,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        if (generated.Count != reference.Texts.Length)
        {
            throw new InvalidDataException("The parity run returned an unexpected vector count.");
        }

        float[] expected = DecodeVector(reference.Vector0Float32Base64);
        ReadOnlySpan<float> actual = generated[0].Vector.Span;
        if (actual.Length != expected.Length)
        {
            throw new InvalidDataException("The parity vector dimensions do not match.");
        }

        float maximumAbsoluteError = 0F;
        for (int index = 0; index < actual.Length; index++)
        {
            maximumAbsoluteError = Math.Max(
                maximumAbsoluteError,
                MathF.Abs(actual[index] - expected[index]));
        }

        if (maximumAbsoluteError > VectorTolerance)
        {
            throw new InvalidDataException(
                $"The reference vector maximum error {maximumAbsoluteError} exceeds {VectorTolerance}.");
        }

        float maximumNormError = generated
            .Select(static embedding =>
                MathF.Abs(TensorPrimitives.Norm(embedding.Vector.Span) - 1F))
            .Max();
        if (maximumNormError > NormTolerance)
        {
            throw new InvalidDataException(
                $"The L2 norm error {maximumNormError} exceeds {NormTolerance}.");
        }

        float maximumCosineError = 0F;
        foreach ((string key, float expectedSimilarity) in reference.PairwiseCosine)
        {
            string[] indices = key.Split('-', 2);
            int left = int.Parse(indices[0], System.Globalization.CultureInfo.InvariantCulture);
            int right = int.Parse(indices[1], System.Globalization.CultureInfo.InvariantCulture);
            float actualSimilarity = VectorMath.CosineSimilarity(
                generated[left].Vector.Span,
                generated[right].Vector.Span);
            maximumCosineError = Math.Max(
                maximumCosineError,
                MathF.Abs(actualSimilarity - expectedSimilarity));
        }

        if (maximumCosineError > CosineTolerance)
        {
            throw new InvalidDataException(
                $"The pairwise cosine error {maximumCosineError} exceeds {CosineTolerance}.");
        }

        return new MiniLmParityResult(
            actual.Length,
            maximumAbsoluteError,
            maximumCosineError,
            maximumNormError);
    }

    private static async ValueTask<ParityReference> LoadReferenceAsync(
        CancellationToken cancellationToken)
    {
        await using Stream stream =
            typeof(MiniLmParityVerifier).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded parity resource '{ResourceName}' was not found.");
        return await JsonSerializer.DeserializeAsync<ParityReference>(
                   stream,
                   SerializerOptions,
                   cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The embedded parity reference is empty.");
    }

    private static float[] DecodeVector(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidDataException("The reference vector byte length is invalid.");
        }

        float[] vector = new float[bytes.Length / sizeof(float)];
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = BinaryPrimitives.ReadSingleLittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)));
        }

        return vector;
    }

    private sealed record ParityReference
    {
        public required string[] Texts { get; init; }

        public required int[][] TokenIds { get; init; }

        public required string Vector0Float32Base64 { get; init; }

        public required Dictionary<string, float> PairwiseCosine { get; init; }
    }
}

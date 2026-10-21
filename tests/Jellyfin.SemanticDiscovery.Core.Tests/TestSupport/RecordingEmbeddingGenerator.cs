using System.Security.Cryptography;
using System.Text;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Embedding;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

/// <summary>
/// A deterministic, in-process embedding generator: the same input text
/// always produces the same 384-dimension unit vector (derived from a SHA-256
/// seeded PRNG), and different text produces (with overwhelming probability)
/// a different vector. Every call is recorded so tests can assert exactly
/// which texts were embedded and how many calls were made — the core
/// mechanism behind the "reuse unchanged vectors with zero extra embedding
/// calls" assertions.
/// </summary>
public sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    public int TotalEmbeddedCount => Calls.Sum(static call => call.Count);

    public static float[] CreateVector(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        ulong seed = BitConverter.ToUInt64(hash, 0);
        float[] vector = new float[SemanticConstants.EmbeddingDimensions];
        for (int index = 0; index < vector.Length; index++)
        {
            // A simple deterministic linear congruential generator; only used
            // to spread values across [-0.5, 0.5) before normalization.
            seed = (seed * 6364136223846793005UL) + 1442695040888963407UL;
            vector[index] = (((seed >> 11) & 0xFFFFFF) / (float)0xFFFFFF) - 0.5F;
        }

        VectorMath.Normalize(vector);
        return vector;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] inputs = values.ToArray();
        Calls.Add(inputs);

        GeneratedEmbeddings<Embedding<float>> generated = new(inputs.Length);
        foreach (string input in inputs)
        {
            generated.Add(new Embedding<float>(CreateVector(input))
            {
                ModelId = "recording-fake",
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }

        return Task.FromResult(generated);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

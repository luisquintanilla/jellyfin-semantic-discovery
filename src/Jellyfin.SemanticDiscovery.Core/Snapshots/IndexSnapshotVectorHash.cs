using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Jellyfin.SemanticDiscovery.Core.Snapshots;

public static class IndexSnapshotVectorHash
{
    public static string Compute(
        IEnumerable<PersistedMetadataVector> metadata,
        IEnumerable<PersistedContentVector> content)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        foreach (PersistedMetadataVector item in metadata.OrderBy(
                     static item => item.Record.Id))
        {
            Append(item.Vector, hash, bytes);
        }
        foreach (PersistedContentVector item in content.OrderBy(
                     static item => item.Chunk.Id,
                     StringComparer.Ordinal))
        {
            Append(item.Vector, hash, bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(
        IEnumerable<float> vector,
        IncrementalHash hash,
        Span<byte> bytes)
    {
        foreach (float value in vector)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }
}

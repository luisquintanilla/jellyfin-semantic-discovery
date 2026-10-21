using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using System.Numerics.Tensors;

namespace Jellyfin.SemanticDiscovery.Core.Snapshots;

public static class IndexSnapshotValidator
{
    public static void Validate(IndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != SemanticConstants.SnapshotSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported snapshot schema {snapshot.SchemaVersion}.");
        }

        if (snapshot.Identity is null ||
            snapshot.Metadata is null ||
            snapshot.Content is null)
        {
            throw new InvalidDataException(
                "Snapshot identity and vector collections are required.");
        }

        ValidateIdentity(snapshot.Identity);
        if (snapshot.CreatedUtc == default)
        {
            throw new InvalidDataException("Snapshot creation time is required.");
        }

        HashSet<Guid> ownerIds = [];
        foreach (PersistedMetadataVector item in snapshot.Metadata)
        {
            if (item is null || item.Record is null)
            {
                throw new InvalidDataException(
                    "Snapshot metadata entries and records are required.");
            }

            if (item.Record.Id == Guid.Empty ||
                !ownerIds.Add(item.Record.Id))
            {
                throw new InvalidDataException(
                    $"Metadata keys must be non-empty and unique: '{item.Record.Id}'.");
            }

            if (!IsSha256(item.ProjectionHash))
            {
                throw new InvalidDataException(
                    $"Metadata '{item.Record.Id}' has a malformed projection hash.");
            }

            ValidateMetadata(item.Record);
            ValidateVector(item.Vector, $"metadata '{item.Record.Id}'");
        }

        HashSet<string> contentIds = new(StringComparer.Ordinal);
        HashSet<(Guid OwnerId, ContentSourceKind Kind, string Path, int Ordinal)>
            contentSlots = [];
        foreach (PersistedContentVector item in snapshot.Content)
        {
            if (item is null || item.Chunk is null)
            {
                throw new InvalidDataException(
                    "Snapshot content entries and chunks are required.");
            }

            ContentChunk chunk = item.Chunk;
            if (string.IsNullOrWhiteSpace(chunk.Id) ||
                !contentIds.Add(chunk.Id))
            {
                throw new InvalidDataException(
                    $"Content keys must be non-empty and unique: '{chunk.Id}'.");
            }
            if (!contentSlots.Add((
                    chunk.OwnerId,
                    chunk.SourceKind,
                    chunk.SourcePath,
                    chunk.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Content '{chunk.Id}' duplicates a source chunk slot.");
            }

            if (!ownerIds.Contains(chunk.OwnerId))
            {
                throw new InvalidDataException(
                    $"Content '{chunk.Id}' references missing owner '{chunk.OwnerId}'.");
            }

            string expectedContentHash = ContentHash.Compute(chunk.Text);
            if (!string.Equals(
                    chunk.ContentHash,
                    expectedContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Content '{chunk.Id}' has a stale or malformed content hash.");
            }

            string expectedId = ContentChunkIdentity.Compute(
                chunk.OwnerId,
                chunk.SourcePath,
                chunk.SourceKind,
                chunk.Ordinal,
                chunk.ContentHash);
            if (!string.Equals(chunk.Id, expectedId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Content '{chunk.Id}' has a stale or malformed deterministic key.");
            }

            ValidateContent(chunk);
            ValidateVector(item.Vector, $"content '{chunk.Id}'");
        }

        if (!IsSha256(snapshot.VectorPayloadHash) ||
            !string.Equals(
                snapshot.VectorPayloadHash,
                IndexSnapshotVectorHash.Compute(
                    snapshot.Metadata,
                    snapshot.Content),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Snapshot vector payload hash is missing or stale.");
        }
    }

    private static void ValidateMetadata(MetadataRecord record)
    {
        if (!Enum.IsDefined(record.Kind) ||
            string.IsNullOrWhiteSpace(record.ItemType) ||
            string.IsNullOrWhiteSpace(record.MediaType) ||
            string.IsNullOrWhiteSpace(record.Title))
        {
            throw new InvalidDataException(
                $"Metadata '{record.Id}' has malformed required text or kind fields.");
        }

        if (record.LibraryId is not { } libraryId ||
            libraryId == Guid.Empty ||
            record.ParentId is not { } parentId ||
            parentId == Guid.Empty)
        {
            throw new InvalidDataException(
                $"Metadata '{record.Id}' has malformed library or parent identifiers.");
        }

        if (!HasValidAncestors(record.AncestorIds))
        {
            throw new InvalidDataException(
                $"Metadata '{record.Id}' has malformed ancestor identifiers.");
        }

        if (
            !HasValidStrings(record.Genres) ||
            !HasValidStrings(record.Tags) ||
            !HasValidStrings(record.People) ||
            !HasValidStrings(record.Studios))
        {
            throw new InvalidDataException(
                $"Metadata '{record.Id}' has malformed required fields.");
        }
    }

    private static void ValidateContent(ContentChunk chunk)
    {
        bool isCueSource = chunk.SourceKind is
            ContentSourceKind.Subtitle or ContentSourceKind.Transcript;
        bool hasAnyTicks = chunk.StartTicks.HasValue || chunk.EndTicks.HasValue;
        bool hasBothTicks = chunk.StartTicks.HasValue && chunk.EndTicks.HasValue;
        if (!Enum.IsDefined(chunk.SourceKind) ||
            chunk.OwnerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(chunk.SourcePath) ||
            string.IsNullOrWhiteSpace(chunk.Text) ||
            chunk.Ordinal < 0 ||
            (isCueSource ? !hasBothTicks : hasAnyTicks) ||
            chunk.StartTicks is < 0 ||
            chunk.EndTicks is < 0 ||
            chunk.StartTicks > chunk.EndTicks)
        {
            throw new InvalidDataException(
                $"Content '{chunk.Id}' has malformed required fields.");
        }
    }

    private static void ValidateIdentity(IndexIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string[] values =
        [
            identity.ModelRevision,
            identity.ModelHash,
            identity.TokenizerRevision,
            identity.TokenizerHash,
            identity.EmbeddingAlgorithmVersion,
            identity.IndexingPipelineVersion,
            identity.ProjectionVersion,
            identity.ProjectionHash,
        ];
        if (values.Any(string.IsNullOrWhiteSpace) ||
            !IsSha256(identity.ModelHash) ||
            !IsSha256(identity.TokenizerHash) ||
            !IsSha256(identity.ProjectionHash))
        {
            throw new InvalidDataException(
                "Snapshot model, tokenizer, and projection identity values are required.");
        }
    }

    private static bool HasValidAncestors(Guid[]? values) =>
        values is not null &&
        values.All(static value => value != Guid.Empty) &&
        values.Distinct().Count() == values.Length;

    private static bool HasValidStrings(string[]? values) =>
        values is not null &&
        values.All(static value => !string.IsNullOrWhiteSpace(value));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateVector(float[]? vector, string description)
    {
        if (vector is null)
        {
            throw new InvalidDataException($"{description} has no vector.");
        }

        if (vector.Length != SemanticConstants.EmbeddingDimensions)
        {
            throw new InvalidDataException(
                $"{description} must contain {SemanticConstants.EmbeddingDimensions} vector values.");
        }

        if (vector.Any(static value => !float.IsFinite(value)))
        {
            throw new InvalidDataException(
                $"{description} contains a non-finite vector value.");
        }

        float norm = TensorPrimitives.Norm(vector);
        if (!float.IsFinite(norm) ||
            norm <= 1e-12F ||
            MathF.Abs(norm - 1F) > 0.005F)
        {
            throw new InvalidDataException(
                $"{description} must contain a unit-normalized non-zero vector.");
        }
    }
}

using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Snapshots;

public sealed class AtomicSnapshotStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"semantic-snapshot-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string SnapshotPath => Path.Combine(_directory, "index.json");

    private static IndexSnapshot CreateSnapshot(
        string identitySuffix = "1",
        int metadataVectorLength = SemanticConstants.EmbeddingDimensions,
        int contentVectorLength = SemanticConstants.EmbeddingDimensions)
    {
        MetadataRecord record = TestData.Movie(title: $"Movie {identitySuffix}");
        ContentChunk chunk = TestData.Chunk(record.Id, text: $"chunk {identitySuffix}");
        return TestData.Seal(new IndexSnapshot
        {
            Identity = TestData.Identity(identitySuffix),
            CreatedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            Metadata =
            [
                new PersistedMetadataVector
                {
                    Record = record,
                    ProjectionHash = ContentHash.Compute("projection"),
                    Vector = UnitVector(metadataVectorLength),
                },
            ],
            Content =
            [
                new PersistedContentVector
                {
                    Chunk = chunk,
                    Vector = UnitVector(contentVectorLength),
                },
            ],
        });
    }

    private static float[] UnitVector(int length)
    {
        float[] vector = new float[length];
        if (length > 0)
        {
            vector[0] = 1F;
        }

        return vector;
    }

    [Fact]
    public async Task LoadAsyncReturnsNullWhenFileDoesNotExist()
    {
        AtomicSnapshotStore store = new(SnapshotPath);

        IndexSnapshot? snapshot = await store.LoadAsync();

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task SaveAsyncThenLoadAsyncRoundTripsSnapshot()
    {
        AtomicSnapshotStore store = new(SnapshotPath);
        IndexSnapshot original = CreateSnapshot();

        await store.SaveAsync(original);
        IndexSnapshot? loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(original.Identity.CompositeHash, loaded!.Identity.CompositeHash);
        Assert.Single(loaded.Metadata);
        Assert.Single(loaded.Content);
        Assert.Equal(original.Metadata[0].Record.Id, loaded.Metadata[0].Record.Id);
        Assert.Equal(original.Content[0].Chunk.Id, loaded.Content[0].Chunk.Id);
    }

    [Fact]
    public async Task SaveAsyncRejectsWhenMetadataVectorLengthIsNot384()
    {
        AtomicSnapshotStore store = new(SnapshotPath);
        IndexSnapshot invalid = CreateSnapshot(metadataVectorLength: 10);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid).AsTask());
        Assert.False(File.Exists(SnapshotPath));
    }

    [Fact]
    public async Task SaveAsyncRejectsWhenContentVectorLengthIsNot384()
    {
        AtomicSnapshotStore store = new(SnapshotPath);
        IndexSnapshot invalid = CreateSnapshot(contentVectorLength: 10);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(invalid).AsTask());
        Assert.False(File.Exists(SnapshotPath));
    }

    [Fact]
    public async Task LoadAsyncRejectsWhenPersistedVectorLengthIsNot384()
    {
        Directory.CreateDirectory(_directory);
        IndexSnapshot corrupted = CreateSnapshot(metadataVectorLength: 3);
        await File.WriteAllTextAsync(
            SnapshotPath,
            JsonSerializer.Serialize(corrupted, RawJsonOptions));
        AtomicSnapshotStore store = new(SnapshotPath);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync().AsTask());

        Assert.Contains("384", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsyncRejectsWhenSchemaVersionMismatches()
    {
        Directory.CreateDirectory(_directory);
        IndexSnapshot mismatched = CreateSnapshot() with { SchemaVersion = 999 };
        await File.WriteAllTextAsync(
            SnapshotPath,
            JsonSerializer.Serialize(mismatched, RawJsonOptions));
        AtomicSnapshotStore store = new(SnapshotPath);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync().AsTask());

        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsyncCancelledLeavesPriorGoodSnapshotIntactAndCleansUpTempFile()
    {
        AtomicSnapshotStore store = new(SnapshotPath);
        IndexSnapshot original = CreateSnapshot(identitySuffix: "original");
        await store.SaveAsync(original);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        IndexSnapshot replacement = CreateSnapshot(identitySuffix: "replacement");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(replacement, cancelled.Token).AsTask());

        IndexSnapshot? loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(original.Identity.CompositeHash, loaded!.Identity.CompositeHash);
        Assert.DoesNotContain(
            Directory.GetFiles(_directory),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsyncFromNewStoreInstanceReturnsPreviouslySavedSnapshotAfterSimulatedRestart()
    {
        IndexSnapshot original = CreateSnapshot();
        AtomicSnapshotStore firstInstance = new(SnapshotPath);
        await firstInstance.SaveAsync(original);

        // A brand new instance pointed at the same path simulates the
        // process restarting and re-opening the snapshot store.
        AtomicSnapshotStore secondInstance = new(SnapshotPath);
        IndexSnapshot? reloaded = await secondInstance.LoadAsync();

        Assert.NotNull(reloaded);
        Assert.Equal(original.Identity.CompositeHash, reloaded!.Identity.CompositeHash);
    }
}

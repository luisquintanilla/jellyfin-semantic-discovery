using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Projection;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using Jellyfin.SemanticDiscovery.Core.Snapshots;
using Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;
using Jellyfin.SemanticDiscovery.Core.VectorData;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Indexing;

/// <summary>
/// Wires a <see cref="SemanticIndexBuilder"/> against fully in-memory/fake
/// dependencies except for the snapshot file, which is written under a
/// unique temp directory (matching <c>AtomicSnapshotStore</c>'s real,
/// file-backed contract) and cleaned up on dispose.
/// </summary>
public sealed class SemanticIndexBuilderHarness : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"semantic-index-builder-{Guid.NewGuid():N}");

    public RecordingEmbeddingGenerator Generator { get; } = new();

    public WordCountTokenCounter TokenCounter { get; } = new();

    public MutableIndexSource Source { get; } = new();

    public FakeContentReader SubtitleReader { get; } =
        new(ContentSourceKind.Subtitle, ContentSourceKind.Transcript);

    public FakeContentReader BookReader { get; } =
        new(ContentSourceKind.Pdf, ContentSourceKind.Epub);

    public InMemorySemanticVectorIndex VectorIndex { get; } = new();

    public string SnapshotPath => Path.Combine(_directory, "index.json");

    public AtomicSnapshotStore CreateSnapshotStore() => new(SnapshotPath);

    public SemanticIndexBuilder CreateBuilder(
        IndexIdentity identity,
        InMemorySemanticVectorIndex? vectorIndex = null,
        IIndexSnapshotStore? snapshotStore = null,
        int embeddingBatchSize = 8) => new(
            Generator,
            TokenCounter,
            Source,
            [SubtitleReader, BookReader],
            snapshotStore ?? CreateSnapshotStore(),
            vectorIndex ?? VectorIndex,
            identity,
            embeddingBatchSize);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public sealed class SemanticIndexBuilderTests
{
    [Fact]
    public async Task RebuildAsyncBuildsAndEmbedsEverythingOnFirstRun()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid item1Id = Guid.NewGuid();
        Guid item2Id = Guid.NewGuid();
        MetadataRecord item1 = TestData.Movie(id: item1Id, title: "Item1");
        MetadataRecord item2 = TestData.Movie(id: item2Id, title: "Item2");
        ContentSourceDescriptor item2Subtitle = new()
        {
            OwnerId = item2Id,
            Kind = ContentSourceKind.Subtitle,
            Path = "item2.srt",
        };
        harness.SubtitleReader.SetChunks(
            item2Subtitle,
            [TestData.Chunk(item2Id, sourcePath: "item2.srt", text: "chunk text")]);
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = item1 },
            new IndexSourceItem { Metadata = item2, ContentSources = [item2Subtitle] },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());

        IndexBuildResult result = await builder.RebuildAsync();

        Assert.Equal(2, result.MetadataCount);
        Assert.Equal(1, result.ContentChunkCount);
        Assert.Equal(2, result.EmbeddedMetadataCount);
        Assert.Equal(1, result.EmbeddedContentChunkCount);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(harness.SnapshotPath));

        // One embedding batch call for metadata (2 texts) and one for content (1 text).
        Assert.Equal(2, harness.Generator.Calls.Count);
    }

    [Fact]
    public async Task RebuildAsyncTruncatesOverBudgetMetadataProjectionTo256Tokens()
    {
        using SemanticIndexBuilderHarness harness = new();
        string longOverview = string.Join(' ', Enumerable.Range(0, 300).Select(i => $"w{i}"));
        MetadataRecord item = TestData.Movie(title: "Long", overview: longOverview);
        harness.Source.Items = [new IndexSourceItem { Metadata = item }];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());

        await builder.RebuildAsync();

        string embeddedText = Assert.Single(harness.Generator.Calls.SelectMany(static call => call));
        int embeddedTokenCount = harness.TokenCounter.CountTokens(embeddedText);
        Assert.Equal(256, embeddedTokenCount);

        string normalizedFullProjection =
            TextNormalization.NormalizeWhitespace(MetadataProjector.Project(item));
        string[] fullWords = normalizedFullProjection.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] embeddedWords = embeddedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(fullWords.Length > 256);
        Assert.Equal(fullWords.Take(256), embeddedWords);
        IndexSnapshot snapshot =
            Assert.IsType<IndexSnapshot>(
                await harness.CreateSnapshotStore().LoadAsync());
        Assert.Equal(
            MetadataProjectionIdentity.ComputeCanonicalSha256(item),
            Assert.Single(snapshot.Metadata).ProjectionHash);
        Assert.NotEqual(
            ContentHash.Compute(embeddedText),
            Assert.Single(snapshot.Metadata).ProjectionHash);
    }

    [Fact]
    public async Task RebuildAsyncReusesUnchangedVectorsWithNoAdditionalEmbeddingCalls()
    {
        using SemanticIndexBuilderHarness harness = new();
        (Guid item1Id, Guid item2Id) = ConfigureTwoItemsWithOneSubtitleChunk(harness);
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        await builder.RebuildAsync();
        int callsAfterFirstRun = harness.Generator.Calls.Count;
        IndexSnapshot? firstSnapshot = await harness.CreateSnapshotStore().LoadAsync();

        IndexBuildResult second = await builder.RebuildAsync();

        Assert.Equal(0, second.EmbeddedMetadataCount);
        Assert.Equal(0, second.EmbeddedContentChunkCount);
        Assert.Equal(2, second.MetadataCount);
        Assert.Equal(1, second.ContentChunkCount);
        Assert.Equal(callsAfterFirstRun, harness.Generator.Calls.Count);

        IndexSnapshot? secondSnapshot = await harness.CreateSnapshotStore().LoadAsync();
        Assert.Equal(
            firstSnapshot!.Metadata.Select(static m => m.Vector),
            secondSnapshot!.Metadata.Select(static m => m.Vector));
        Assert.Contains(secondSnapshot.Metadata, m => m.Record.Id == item1Id);
        Assert.Contains(secondSnapshot.Metadata, m => m.Record.Id == item2Id);
    }

    [Fact]
    public async Task RebuildAsyncReembedsEverythingWhenIndexIdentityChanges()
    {
        using SemanticIndexBuilderHarness harness = new();
        ConfigureTwoItemsWithOneSubtitleChunk(harness);
        SemanticIndexBuilder builder1 = harness.CreateBuilder(TestData.Identity("a"));
        await builder1.RebuildAsync();
        int callsAfterFirstRun = harness.Generator.Calls.Count;

        SemanticIndexBuilder builder2 = harness.CreateBuilder(TestData.Identity("b"));
        IndexBuildResult second = await builder2.RebuildAsync();

        Assert.Equal(2, second.EmbeddedMetadataCount);
        Assert.Equal(1, second.EmbeddedContentChunkCount);
        Assert.True(harness.Generator.Calls.Count > callsAfterFirstRun);
    }

    [Fact]
    public async Task EmbeddingAlgorithmVersionChangeRejectsSnapshotAndPreventsReuse()
    {
        using SemanticIndexBuilderHarness harness = new();
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = TestData.Movie(title: "Item") },
        ];
        IndexIdentity original = TestData.Identity();
        await harness.CreateBuilder(original).RebuildAsync();
        int callsAfterOriginal = harness.Generator.Calls.Count;
        IndexIdentity changed = original with
        {
            EmbeddingAlgorithmVersion = "different-pooling-v2",
        };
        InMemorySemanticVectorIndex freshIndex = new();
        SemanticIndexBuilder changedBuilder = harness.CreateBuilder(
            changed,
            vectorIndex: freshIndex);

        Assert.False(await changedBuilder.TryLoadSnapshotAsync());
        IndexBuildResult result = await changedBuilder.RebuildAsync();

        Assert.Equal(1, result.EmbeddedMetadataCount);
        Assert.True(harness.Generator.Calls.Count > callsAfterOriginal);
    }

    [Fact]
    public async Task RebuildAsyncReembedsOnlyChangedMetadataProjectionOrChangedChunksEachRun()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid item1Id = Guid.NewGuid();
        Guid item2Id = Guid.NewGuid();
        MetadataRecord item1Run1 = TestData.Movie(id: item1Id, title: "Item1", overview: "original overview");
        MetadataRecord item2 = TestData.Movie(id: item2Id, title: "Item2");
        ContentSourceDescriptor item1Descriptor = new()
        {
            OwnerId = item1Id,
            Kind = ContentSourceKind.Subtitle,
            Path = "item1.srt",
        };
        ContentSourceDescriptor item2Descriptor = new()
        {
            OwnerId = item2Id,
            Kind = ContentSourceKind.Subtitle,
            Path = "item2.srt",
        };
        harness.SubtitleReader.SetChunks(
            item1Descriptor,
            [TestData.Chunk(item1Id, sourcePath: "item1.srt", text: "item1 chunk v1")]);
        harness.SubtitleReader.SetChunks(
            item2Descriptor,
            [TestData.Chunk(item2Id, sourcePath: "item2.srt", text: "item2 chunk v1")]);
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = item1Run1, ContentSources = [item1Descriptor] },
            new IndexSourceItem { Metadata = item2, ContentSources = [item2Descriptor] },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        await builder.RebuildAsync();

        // Run 2: only item1's metadata projection changes; both chunks stay the same.
        MetadataRecord item1Run2 = item1Run1 with { Overview = "changed overview" };
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = item1Run2, ContentSources = [item1Descriptor] },
            new IndexSourceItem { Metadata = item2, ContentSources = [item2Descriptor] },
        ];
        IndexBuildResult run2 = await builder.RebuildAsync();

        Assert.Equal(1, run2.EmbeddedMetadataCount);
        Assert.Equal(0, run2.EmbeddedContentChunkCount);

        // Run 3: metadata unchanged from run 2; only item2's chunk text changes.
        harness.SubtitleReader.SetChunks(
            item2Descriptor,
            [TestData.Chunk(item2Id, sourcePath: "item2.srt", text: "item2 chunk v2 changed")]);
        IndexBuildResult run3 = await builder.RebuildAsync();

        Assert.Equal(0, run3.EmbeddedMetadataCount);
        Assert.Equal(1, run3.EmbeddedContentChunkCount);
    }

    [Fact]
    public async Task RebuildAsyncCancelledLeavesPriorSnapshotUntouchedAndNeverReportsFullProgress()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid item1Id = Guid.NewGuid();
        Guid item2Id = Guid.NewGuid();
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = TestData.Movie(id: item1Id, title: "Item1") },
            new IndexSourceItem { Metadata = TestData.Movie(id: item2Id, title: "Item2") },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        await builder.RebuildAsync();
        int callsAfterBaseline = harness.Generator.Calls.Count;

        using CancellationTokenSource cancellationSource = new();
        harness.Source.OnBeforeItem = index =>
        {
            if (index == 1)
            {
                cancellationSource.Cancel();
            }
        };
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = TestData.Movie(id: item1Id, title: "Item1 Changed") },
            new IndexSourceItem { Metadata = TestData.Movie(id: item2Id, title: "Item2 Changed") },
        ];
        List<double> progressValues = [];

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => builder.RebuildAsync(new RecordingProgress(progressValues), cancellationSource.Token).AsTask());

        IndexSnapshot? reloaded = await harness.CreateSnapshotStore().LoadAsync();
        Assert.NotNull(reloaded);
        Assert.Contains(reloaded!.Metadata, m => m.Record.Id == item1Id && m.Record.Title == "Item1");
        Assert.Contains(reloaded.Metadata, m => m.Record.Id == item2Id && m.Record.Title == "Item2");
        Assert.Equal(callsAfterBaseline, harness.Generator.Calls.Count);
        Assert.DoesNotContain(100D, progressValues);
    }

    [Fact]
    public async Task RebuildAsyncOrdersPersistedSnapshotDeterministicallyRegardlessOfSourceOrder()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        Guid ownerLow = ids.Min();
        Guid ownerHigh = ids.Max();
        MetadataRecord recordLow = TestData.Movie(id: ownerLow, title: "Low");
        MetadataRecord recordHigh = TestData.Movie(id: ownerHigh, title: "High");
        ContentSourceDescriptor lowDescriptor = new()
        {
            OwnerId = ownerLow,
            Kind = ContentSourceKind.Subtitle,
            Path = "subs.srt",
        };
        ContentSourceDescriptor highDescriptor = new()
        {
            OwnerId = ownerHigh,
            Kind = ContentSourceKind.Subtitle,
            Path = "subs.srt",
        };
        // The reader deliberately returns chunks out of ordinal order to
        // prove the builder re-sorts by ordinal, not reader emission order.
        harness.SubtitleReader.SetChunks(
            lowDescriptor,
            [
                TestData.Chunk(ownerLow, sourcePath: "subs.srt", ordinal: 1, text: "low chunk one"),
                TestData.Chunk(ownerLow, sourcePath: "subs.srt", ordinal: 0, text: "low chunk zero"),
            ]);
        harness.SubtitleReader.SetChunks(
            highDescriptor,
            [TestData.Chunk(ownerHigh, sourcePath: "subs.srt", ordinal: 0, text: "high chunk zero")]);
        // The "high" owner is listed first to prove metadata is re-sorted by id, not source order.
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = recordHigh, ContentSources = [highDescriptor] },
            new IndexSourceItem { Metadata = recordLow, ContentSources = [lowDescriptor] },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());

        await builder.RebuildAsync();

        IndexSnapshot? snapshot = await harness.CreateSnapshotStore().LoadAsync();
        Assert.NotNull(snapshot);
        Assert.Equal([ownerLow, ownerHigh], snapshot!.Metadata.Select(static m => m.Record.Id));
        Assert.Equal(
            [(ownerLow, 0), (ownerLow, 1), (ownerHigh, 0)],
            snapshot.Content.Select(static c => (c.Chunk.OwnerId, c.Chunk.Ordinal)));
    }

    [Fact]
    public async Task TryLoadSnapshotAsyncLoadsMatchingIdentityAndSkipsMismatchedIdentity()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid itemId = Guid.NewGuid();
        harness.Source.Items = [new IndexSourceItem { Metadata = TestData.Movie(id: itemId, title: "Reload Me") }];
        IndexIdentity identityA = TestData.Identity("a");
        SemanticIndexBuilder builder1 = harness.CreateBuilder(identityA);
        await builder1.RebuildAsync();

        InMemorySemanticVectorIndex freshIndexMatching = new();
        SemanticIndexBuilder reloaderMatching =
            harness.CreateBuilder(identityA, vectorIndex: freshIndexMatching);
        bool loadedMatching = await reloaderMatching.TryLoadSnapshotAsync();

        Assert.True(loadedMatching);
        Assert.NotNull(await freshIndexMatching.OpenReadSession()
            .GetMetadataAsync(itemId, CancellationToken.None));

        InMemorySemanticVectorIndex freshIndexMismatched = new();
        IndexIdentity identityB = TestData.Identity("b");
        SemanticIndexBuilder reloaderMismatched =
            harness.CreateBuilder(identityB, vectorIndex: freshIndexMismatched);
        bool loadedMismatched = await reloaderMismatched.TryLoadSnapshotAsync();

        Assert.False(loadedMismatched);
        Assert.Null(await freshIndexMismatched.OpenReadSession()
            .GetMetadataAsync(itemId, CancellationToken.None));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("overview")]
    [InlineData("genres")]
    [InlineData("tags")]
    public async Task TryLoadSnapshotAsyncRejectsMetadataTamperedAfterProjectionHash(
        string field)
    {
        using SemanticIndexBuilderHarness harness = new();
        MetadataRecord original = TestData.Movie(
            title: "Original",
            overview: "overview",
            genres: ["Drama"],
            tags: ["tag"]);
        harness.Source.Items = [new IndexSourceItem { Metadata = original }];
        IndexIdentity identity = TestData.Identity();
        await harness.CreateBuilder(identity).RebuildAsync();
        IndexSnapshot snapshot =
            Assert.IsType<IndexSnapshot>(await harness.CreateSnapshotStore().LoadAsync());
        MetadataRecord tampered = field switch
        {
            "title" => original with { Title = "Tampered" },
            "overview" => original with { Overview = "tampered overview" },
            "genres" => original with { Genres = ["Comedy"] },
            "tags" => original with { Tags = ["different"] },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        IndexSnapshot corrupted = snapshot with
        {
            Metadata =
            [
                snapshot.Metadata[0] with { Record = tampered },
            ],
        };
        InMemorySemanticVectorIndex freshIndex = new();
        SemanticIndexBuilder loader = harness.CreateBuilder(
            identity,
            vectorIndex: freshIndex,
            snapshotStore: new StaticSnapshotStore(corrupted));

        bool loaded = await loader.TryLoadSnapshotAsync();

        Assert.False(loaded);
        Assert.Contains(
            "InvalidDataException",
            loader.LastSnapshotLoadError,
            StringComparison.Ordinal);
        Assert.Empty(await freshIndex.OpenReadSession()
            .GetMetadataRecordsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RebuildAsyncDegradedBookConversionKeepsMetadataAndSubtitlesAndStalePriorBookChunks()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid movieId = Guid.NewGuid();
        Guid bookId = Guid.NewGuid();
        MetadataRecord movie = TestData.Movie(id: movieId, title: "Movie With Subtitles");
        MetadataRecord book = TestData.Book(id: bookId, title: "Book With PDF");
        ContentSourceDescriptor subtitleDescriptor = new()
        {
            OwnerId = movieId,
            Kind = ContentSourceKind.Subtitle,
            Path = "movie.srt",
        };
        ContentSourceDescriptor pdfDescriptor = new()
        {
            OwnerId = bookId,
            Kind = ContentSourceKind.Pdf,
            Path = "book.pdf",
        };
        harness.SubtitleReader.SetChunks(
            subtitleDescriptor,
            [TestData.Chunk(movieId, sourcePath: "movie.srt", text: "movie subtitle text")]);
        harness.BookReader.SetChunks(
            pdfDescriptor,
            [
                TestData.Chunk(
                    bookId,
                    sourcePath: "book.pdf",
                    sourceKind: ContentSourceKind.Pdf,
                    text: "book chapter one"),
            ]);
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = movie, ContentSources = [subtitleDescriptor] },
            new IndexSourceItem { Metadata = book, ContentSources = [pdfDescriptor] },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        IndexBuildResult baseline = await builder.RebuildAsync();
        Assert.Empty(baseline.Errors);

        // The PDF converter now degrades for the book's document; the
        // subtitle reader is untouched and keeps succeeding.
        harness.BookReader.SetFailure(
            pdfDescriptor,
            new ContentIngestionException("MarkItDown is unavailable.", "book.pdf", isTransient: true));
        IndexBuildResult degraded = await builder.RebuildAsync();

        ContentIngestionError error = Assert.Single(degraded.Errors);
        Assert.Equal("book.pdf", error.SourcePath);
        Assert.True(error.IsTransient);

        IndexSnapshot? snapshot = await harness.CreateSnapshotStore().LoadAsync();
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Metadata, m => m.Record.Id == movieId);
        Assert.Contains(
            snapshot.Content,
            c => c.Chunk.OwnerId == movieId && c.Chunk.Text == "movie subtitle text");
        Assert.Contains(snapshot.Metadata, m => m.Record.Id == bookId);
        Assert.Contains(
            snapshot.Content,
            c => c.Chunk.OwnerId == bookId && c.Chunk.Text == "book chapter one");
    }

    [Theory]
    [InlineData("Malformed EPUB container XML.")]
    [InlineData("EPUB container has no rootfile.")]
    public async Task RebuildAsyncPermanentEpubStructureFailureDoesNotRetainStaleChunks(
        string failure)
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid ownerId = Guid.NewGuid();
        ContentSourceDescriptor descriptor = new()
        {
            OwnerId = ownerId,
            Kind = ContentSourceKind.Epub,
            Path = "book.epub",
        };
        harness.BookReader.SetChunks(
            descriptor,
            [
                TestData.Chunk(
                    ownerId,
                    descriptor.Path,
                    descriptor.Kind,
                    text: "prior document text"),
            ]);
        harness.Source.Items =
        [
            new IndexSourceItem
            {
                Metadata = TestData.Book(ownerId),
                ContentSources = [descriptor],
            },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        await builder.RebuildAsync();
        harness.BookReader.SetFailure(
            descriptor,
            new ContentIngestionException(
                failure,
                descriptor.Path,
                isTransient: false));

        IndexBuildResult result = await builder.RebuildAsync();
        IndexSnapshot snapshot =
            Assert.IsType<IndexSnapshot>(await harness.CreateSnapshotStore().LoadAsync());

        Assert.False(Assert.Single(result.Errors).IsTransient);
        Assert.DoesNotContain(
            snapshot.Content,
            item => item.Chunk.OwnerId == ownerId &&
                item.Chunk.SourcePath == descriptor.Path);
    }

    [Fact]
    public async Task RebuildAsyncEmptySuccessfulContentReadRemovesPriorChunks()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid ownerId = Guid.NewGuid();
        ContentSourceDescriptor descriptor = new()
        {
            OwnerId = ownerId,
            Kind = ContentSourceKind.Epub,
            Path = "book.epub",
        };
        harness.BookReader.SetChunks(
            descriptor,
            [
                TestData.Chunk(
                    ownerId,
                    descriptor.Path,
                    descriptor.Kind,
                    text: "prior chapter"),
            ]);
        harness.Source.Items =
        [
            new IndexSourceItem
            {
                Metadata = TestData.Book(ownerId),
                ContentSources = [descriptor],
            },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        await builder.RebuildAsync();
        harness.BookReader.SetChunks(descriptor, []);

        await builder.RebuildAsync();
        IndexSnapshot snapshot =
            Assert.IsType<IndexSnapshot>(await harness.CreateSnapshotStore().LoadAsync());

        Assert.Empty(snapshot.Content);
    }

    [Fact]
    public async Task RebuildAsyncDeletedContentSourceRemovesPriorChunks()
    {
        using SemanticIndexBuilderHarness harness = new();
        Guid ownerId = Guid.NewGuid();
        MetadataRecord book = TestData.Book(ownerId);
        ContentSourceDescriptor descriptor = new()
        {
            OwnerId = ownerId,
            Kind = ContentSourceKind.Pdf,
            Path = "book.pdf",
        };
        harness.BookReader.SetChunks(
            descriptor,
            [
                TestData.Chunk(
                    ownerId,
                    descriptor.Path,
                    descriptor.Kind,
                    text: "prior chapter"),
            ]);
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = book, ContentSources = [descriptor] },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        await builder.RebuildAsync();
        harness.Source.Items = [new IndexSourceItem { Metadata = book }];

        await builder.RebuildAsync();
        IndexSnapshot snapshot =
            Assert.IsType<IndexSnapshot>(await harness.CreateSnapshotStore().LoadAsync());

        Assert.Empty(snapshot.Content);
    }

    [Fact]
    public async Task RebuildAsyncRejectsDescriptorOwnedByAnotherExistingItem()
    {
        using SemanticIndexBuilderHarness harness = new();
        MetadataRecord first = TestData.Movie(title: "First");
        MetadataRecord second = TestData.Movie(title: "Second");
        ContentSourceDescriptor mismatched = new()
        {
            OwnerId = second.Id,
            Kind = ContentSourceKind.Subtitle,
            Path = "wrong-owner.srt",
        };
        harness.Source.Items =
        [
            new IndexSourceItem
            {
                Metadata = first,
                ContentSources = [mismatched],
            },
            new IndexSourceItem { Metadata = second },
        ];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => builder.RebuildAsync().AsTask());

        Assert.Contains("different metadata owner", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.SnapshotPath));
    }

    [Fact]
    public async Task RebuildAsyncSaveFailureLeavesPublishedGenerationAndProgressIntact()
    {
        using SemanticIndexBuilderHarness harness = new();
        MetadataRecord original = TestData.Movie(title: "Original");
        harness.Source.Items = [new IndexSourceItem { Metadata = original }];
        IndexIdentity identity = TestData.Identity();
        SemanticIndexBuilder baselineBuilder = harness.CreateBuilder(identity);
        await baselineBuilder.RebuildAsync();
        IndexSnapshot baseline =
            Assert.IsType<IndexSnapshot>(await harness.CreateSnapshotStore().LoadAsync());
        MetadataRecord replacement = TestData.Movie(title: "Replacement");
        harness.Source.Items = [new IndexSourceItem { Metadata = replacement }];
        List<double> progress = [];
        SemanticIndexBuilder failingBuilder = harness.CreateBuilder(
            identity,
            snapshotStore: new SaveFailingSnapshotStore(baseline));

        await Assert.ThrowsAsync<IOException>(
            () => failingBuilder.RebuildAsync(
                new RecordingProgress(progress)).AsTask());
        IVectorSearchReadSession session = harness.VectorIndex.OpenReadSession();

        Assert.NotNull(await session.GetMetadataAsync(
            original.Id,
            CancellationToken.None));
        Assert.Null(await session.GetMetadataAsync(
            replacement.Id,
            CancellationToken.None));
        Assert.DoesNotContain(100D, progress);
    }

    [Fact]
    public async Task RebuildAsyncReportsPrecommitProgressWithoutFalliblePostPublicationCallback()
    {
        using SemanticIndexBuilderHarness harness = new();
        harness.Source.Items = [new IndexSourceItem { Metadata = TestData.Movie(title: "Solo") }];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());
        List<double> values = [];

        await builder.RebuildAsync(new RecordingProgress(values));

        Assert.DoesNotContain(100D, values);
        Assert.Equal(99D, values[^1]);
    }

    [Fact]
    public async Task RebuildAsyncNeverInvokesThrowingAt100ProgressAfterPublication()
    {
        using SemanticIndexBuilderHarness harness = new();
        MetadataRecord item = TestData.Movie(title: "Published");
        harness.Source.Items = [new IndexSourceItem { Metadata = item }];
        SemanticIndexBuilder builder = harness.CreateBuilder(TestData.Identity());

        IndexBuildResult result = await builder.RebuildAsync(
            new ThrowAtOneHundredProgress());

        Assert.Equal(1, result.MetadataCount);
        Assert.NotNull(await harness.VectorIndex.OpenReadSession()
            .GetMetadataAsync(item.Id, CancellationToken.None));
        Assert.NotNull(await harness.CreateSnapshotStore().LoadAsync());
    }

    private static (Guid Item1Id, Guid Item2Id) ConfigureTwoItemsWithOneSubtitleChunk(
        SemanticIndexBuilderHarness harness)
    {
        Guid item1Id = Guid.NewGuid();
        Guid item2Id = Guid.NewGuid();
        MetadataRecord item1 = TestData.Movie(id: item1Id, title: "Item1");
        MetadataRecord item2 = TestData.Movie(id: item2Id, title: "Item2");
        ContentSourceDescriptor descriptor = new()
        {
            OwnerId = item2Id,
            Kind = ContentSourceKind.Subtitle,
            Path = "item2.srt",
        };
        harness.SubtitleReader.SetChunks(
            descriptor,
            [TestData.Chunk(item2Id, sourcePath: "item2.srt", text: "chunk")]);
        harness.Source.Items =
        [
            new IndexSourceItem { Metadata = item1 },
            new IndexSourceItem { Metadata = item2, ContentSources = [descriptor] },
        ];
        return (item1Id, item2Id);
    }

    private sealed class RecordingProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private sealed class ThrowAtOneHundredProgress : IProgress<double>
    {
        public void Report(double value)
        {
            if (value == 100D)
            {
                throw new InvalidOperationException(
                    "Post-publication callback must not run.");
            }
        }
    }

    private sealed class SaveFailingSnapshotStore(IndexSnapshot snapshot)
        : IIndexSnapshotStore
    {
        public ValueTask<IndexSnapshot?> LoadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IndexSnapshot?>(snapshot);

        public ValueTask SaveAsync(
            IndexSnapshot value,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("simulated save failure"));
    }

    private sealed class StaticSnapshotStore(IndexSnapshot snapshot)
        : IIndexSnapshotStore
    {
        public ValueTask<IndexSnapshot?> LoadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IndexSnapshot?>(snapshot);

        public ValueTask SaveAsync(
            IndexSnapshot value,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}

using System.Globalization;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.IntegrationTests.TestSupport;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Indexing;

/// <summary>
/// A stream that never reports a usable <see cref="Length"/> (it throws, as
/// a genuinely non-seekable network-style stream would) and keeps producing
/// bytes past whatever an early read might have implied about its total
/// size — i.e. it "grows" across reads. Used to prove
/// <see cref="MarkItDownMcpReader.ReadBoundedDocumentAsync"/> enforces its
/// byte limit purely from the running total of bytes actually read, never
/// from any upfront/reported length.
/// </summary>
public sealed class GrowingStream(long totalBytes) : Stream
{
    private long _produced;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        // Yield to prove this is genuinely asynchronous, multi-step
        // production rather than a single synchronous burst.
        await Task.Yield();
        if (_produced >= totalBytes)
        {
            return 0;
        }

        // Deliberately produce far less than the caller's buffer per read
        // (and less than the remaining total), forcing many reads before
        // the cumulative total ever crosses the caller's limit.
        int toWrite = (int)Math.Min(buffer.Length, Math.Min(4096L, totalBytes - _produced));
        buffer.Span[..toWrite].Fill((byte)'x');
        _produced += toWrite;
        return toWrite;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

public sealed class MarkItDownReadBoundedDocumentAsyncTests
{
    [Fact]
    public async Task FailsOnceActualBytesExceedTheLimitEvenWhenTheStreamGrowsAcrossManyReads()
    {
        const int limit = 10_000;
        GrowingStream stream = new(totalBytes: limit + (64 * 1024));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => MarkItDownMcpReader.ReadBoundedDocumentAsync(
                stream,
                limit,
                CancellationToken.None).AsTask());

        Assert.Contains(
            limit.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SucceedsWhenTheGrowingStreamStaysAtOrUnderTheLimit()
    {
        const int limit = 10_000;
        GrowingStream stream = new(totalBytes: limit);

        byte[] bytes = await MarkItDownMcpReader.ReadBoundedDocumentAsync(
            stream,
            limit,
            CancellationToken.None);

        Assert.Equal(limit, bytes.Length);
    }
}

public sealed class MarkItDownMcpReaderConcurrencyTests : IDisposable
{
    private static readonly Uri Endpoint = new("https://markitdown.local/mcp");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"semantic-markitdown-concurrency-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateFile()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "book.pdf");
        File.WriteAllBytes(path, "pdf-bytes"u8.ToArray());
        return path;
    }

    private static HttpResponseMessage BuildSuccessResponse(CapturedRequest request, string markdown)
    {
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(request.Body);
        long requestId = document.RootElement.GetProperty("id").GetInt64();
        object payload = new
        {
            jsonrpc = "2.0",
            id = requestId,
            result = new
            {
                content = new object[]
                {
                    new { type = "text", text = markdown },
                },
            },
        };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
    }

    [Fact]
    public async Task DisposeDrainsInFlightAndGateWaitingConversionsBeforeDisposingTheGate()
    {
        ScriptedHttpMessageHandler handler = new();
        TaskCompletionSource inFlightEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Handler = async (request, cancellationToken) =>
        {
            inFlightEntered.TrySetResult();
            await releaseInFlight.Task.WaitAsync(cancellationToken);
            return BuildSuccessResponse(request, "# Heading\n\nBody.");
        };
        using HttpClient httpClient = new(handler);
        using MarkItDownMcpReader reader = new(
            httpClient,
            Endpoint,
            new WordCountTokenCounter(),
            maximumConcurrency: 1,
            maximumTokens: 200);
        string path = CreateFile();
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        // Started first: with maximumConcurrency=1 this synchronously
        // acquires the conversion gate and then suspends inside the
        // (gated) HTTP call, holding the gate.
        Task<IReadOnlyList<ContentChunk>> inFlight =
            reader.ReadAsync(source, CancellationToken.None).AsTask();
        await inFlightEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Started second while the gate is held: EnterOperation accepts it
        // (it counts toward drain) but it blocks in the gate's WaitAsync.
        using CancellationTokenSource waitingCancellation = new();
        Task<IReadOnlyList<ContentChunk>> waiting =
            reader.ReadAsync(source, waitingCancellation.Token).AsTask();
        await waitingCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // Dispose races with the still-active in-flight conversion. It must
        // drain it rather than disposing the gate out from under it.
        Task firstDispose = Task.Run(reader.Dispose);
        Task secondDispose = Task.Run(reader.Dispose);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(firstDispose.IsCompleted);

        releaseInFlight.SetResult();
        IReadOnlyList<ContentChunk> completed = await inFlight;
        Assert.NotEmpty(completed);

        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DisposeTimeoutRejectsNewWorkAndPreservesResourcesForALaterSuccessfulRetry()
    {
        ScriptedHttpMessageHandler handler = new();
        TaskCompletionSource inFlightEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Handler = async (request, cancellationToken) =>
        {
            inFlightEntered.TrySetResult();
            await releaseInFlight.Task.WaitAsync(cancellationToken);
            return BuildSuccessResponse(request, "# Heading\n\nBody.");
        };
        using HttpClient httpClient = new(handler);
        MarkItDownMcpReader reader = new(
            httpClient,
            Endpoint,
            new WordCountTokenCounter(),
            maximumConcurrency: 1,
            disposeTimeout: TimeSpan.FromMilliseconds(50),
            maximumTokens: 200);
        string path = CreateFile();
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };
        bool disposed = false;
        try
        {
            Task<IReadOnlyList<ContentChunk>> inFlight =
                reader.ReadAsync(source, CancellationToken.None).AsTask();
            await inFlightEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Throws<TimeoutException>(reader.Dispose);

            // Once a dispose attempt has begun (even if it timed out
            // draining), new work must be rejected rather than racing with
            // eventual cleanup.
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => reader.ReadAsync(source, CancellationToken.None).AsTask());

            releaseInFlight.SetResult();
            IReadOnlyList<ContentChunk> completed = await inFlight;
            Assert.NotEmpty(completed);

            // The gate/HTTP client were left intact by the timed-out
            // dispose, so a retry after the drain now succeeds cleanly.
            reader.Dispose();
            disposed = true;
        }
        finally
        {
            if (!disposed)
            {
                await Task.Run(reader.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}

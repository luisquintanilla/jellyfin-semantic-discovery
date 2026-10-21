using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Ingestion;

namespace Jellyfin.SemanticDiscovery.Core.Indexing;

public sealed class MarkItDownMcpReader : IContentReader, IDisposable
{
    public const int AbsoluteMaximumDocumentBytes = 32 * 1024 * 1024;
    public const int AbsoluteMaximumRequestBytes = 48 * 1024 * 1024;
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly HeadingAwareChunker _chunker;
    private readonly long _maximumDocumentBytes;
    private readonly SemaphoreSlim _conversionGate;
    private readonly TimeSpan _disposeTimeout;
    private readonly long _maximumRequestBytes;
    private readonly Lock _lifetimeLock = new();
    private readonly Lock _memoryLock = new();
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private TaskCompletionSource? _drained;
    private int _activeOperations;
    private bool _disposing;
    private bool _disposed;
    private long _requestId;
    private ClientRequestMemoryObservation? _largestRequestMemoryObservation;

    public MarkItDownMcpReader(
        HttpClient httpClient,
        Uri endpoint,
        HeadingAwareChunker chunker,
        long maximumDocumentBytes = 8 * 1024 * 1024,
        int maximumConcurrency = 2,
        TimeSpan? disposeTimeout = null,
        long maximumRequestBytes = 12 * 1024 * 1024)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ValidateEndpoint(_endpoint);

        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _maximumDocumentBytes =
            maximumDocumentBytes is > 0 and <= AbsoluteMaximumDocumentBytes
            ? maximumDocumentBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
        _disposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(30);
        if (_disposeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(disposeTimeout));
        }
        _maximumRequestBytes =
            maximumRequestBytes is > 0 and <= AbsoluteMaximumRequestBytes
                ? maximumRequestBytes
                : throw new ArgumentOutOfRangeException(
                    nameof(maximumRequestBytes));

        _conversionGate = new SemaphoreSlim(
            maximumConcurrency > 0
                ? maximumConcurrency
                : throw new ArgumentOutOfRangeException(nameof(maximumConcurrency)),
            maximumConcurrency);
    }

    public bool Supports(ContentSourceKind kind) =>
        kind is ContentSourceKind.Pdf or ContentSourceKind.Epub;

    public ClientRequestMemoryObservation? LargestRequestMemoryObservation
    {
        get
        {
            lock (_memoryLock)
            {
                return _largestRequestMemoryObservation;
            }
        }
    }

    public static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        return new HttpClient(CreateHttpHandler(), disposeHandler: true)
        {
            Timeout = timeout,
        };
    }

    public static int MeasureSerializedRequestBytes(
        byte[] document,
        string mediaType,
        long requestId = 1) =>
        CreateRequestBytes(document, mediaType, requestId).Length;

    public static async Task ProbeAsync(
        HttpClient httpClient,
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ValidateEndpoint(endpoint);
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 0,
            method = "tools/list",
            @params = new { },
        });
        using ByteArrayContent content = new(requestBytes);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        using HttpRequestMessage request =
            new(HttpMethod.Post, endpoint)
            {
                Content = content,
            };
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        try
        {
            string json = await ReadBoundedAsync(
                response,
                cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "jsonrpc", out string? protocol) ||
                protocol != "2.0" ||
                !root.TryGetProperty("id", out JsonElement id) ||
                !id.TryGetInt64(out long responseId) ||
                responseId != 0 ||
                !root.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("tools", out JsonElement tools) ||
                tools.ValueKind != JsonValueKind.Array ||
                !tools.EnumerateArray().Any(static tool =>
                    tool.ValueKind == JsonValueKind.Object &&
                    TryGetString(tool, "name", out string? name) &&
                    name == "convert_to_markdown"))
            {
                throw new InvalidDataException(
                    "The configured MarkItDown MCP endpoint did not advertise convert_to_markdown.");
            }
        }
        catch (Exception exception)
            when (exception is JsonException or MarkItDownProtocolException)
        {
            throw new InvalidDataException(
                "The configured MarkItDown MCP readiness response was malformed.",
                exception);
        }
    }

    internal static SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    };

    private static void ValidateEndpoint(Uri? endpoint)
    {
        if (endpoint is null ||
            !endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp &&
             endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The MarkItDown endpoint must be an absolute HTTP(S) URI.",
                nameof(endpoint));
        }
    }

    private static byte[] CreateRequestBytes(
        byte[] document,
        string mediaType,
        long requestId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        string dataUri =
            $"data:{mediaType};base64,{Convert.ToBase64String(document)}";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "tools/call",
            @params = new
            {
                name = "convert_to_markdown",
                arguments = new
                {
                    source = dataUri,
                },
            },
        });
    }

    public async ValueTask<IReadOnlyList<ContentChunk>> ReadAsync(
        ContentSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnterOperation();
        bool requestStarted = false;
        try
        {
            if (!Supports(source.Kind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(source),
                    source.Kind,
                    "MarkItDown accepts PDF and EPUB sources only.");
            }

            await _conversionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using FileStream stream = new(
                    source.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] bytes = await ReadBoundedDocumentAsync(
                    stream,
                    _maximumDocumentBytes,
                    cancellationToken).ConfigureAwait(false);
                SemanticSourceIntegrity.ValidateIfPresent(
                    source.Path,
                    source.Kind,
                    bytes);
                string mediaType = source.Kind == ContentSourceKind.Pdf
                    ? "application/pdf"
                    : "application/epub+zip";
                long requestId = Interlocked.Increment(ref _requestId);
                byte[] requestBytes = CreateRequestBytes(
                    bytes,
                    mediaType,
                    requestId);
                if (requestBytes.LongLength > _maximumRequestBytes)
                {
                    throw new InvalidDataException(
                        $"The serialized document request exceeds the {_maximumRequestBytes}-byte limit.");
                }
                using ByteArrayContent requestContent = new(requestBytes);
                requestContent.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json");
                using HttpRequestMessage httpRequest =
                    new(HttpMethod.Post, _endpoint)
                    {
                        Content = requestContent,
                    };
                long beforeCurrentWorkingSetBytes = Environment.WorkingSet;
                long beforePeakWorkingSetBytes;
                using (Process process = Process.GetCurrentProcess())
                {
                    beforePeakWorkingSetBytes = process.PeakWorkingSet64;
                }
                requestStarted = true;
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string json = await ReadBoundedAsync(response, cancellationToken)
                    .ConfigureAwait(false);
                string markdown = ParseMarkdown(json, requestId, source.Path);
                long afterCurrentWorkingSetBytes = Environment.WorkingSet;
                long afterPeakWorkingSetBytes;
                using (Process process = Process.GetCurrentProcess())
                {
                    afterPeakWorkingSetBytes = process.PeakWorkingSet64;
                }
                RecordRequestMemory(new ClientRequestMemoryObservation(
                    SourceBytes: bytes.LongLength,
                    SerializedRequestBytes: requestBytes.LongLength,
                    BeforeCurrentWorkingSetBytes:
                        beforeCurrentWorkingSetBytes,
                    BeforePeakWorkingSetBytes:
                        beforePeakWorkingSetBytes,
                    AfterCurrentWorkingSetBytes:
                        afterCurrentWorkingSetBytes,
                    AfterPeakWorkingSetBytes:
                        afterPeakWorkingSetBytes));
                return _chunker.Chunk(
                    source.OwnerId,
                    source.Path,
                    source.Kind,
                    markdown);
            }
            catch (HttpRequestException exception)
            {
                throw new ContentIngestionException(
                    "MarkItDown is unavailable.",
                    source.Path,
                    isTransient: true,
                    exception);
            }
            catch (TaskCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new ContentIngestionException(
                    "MarkItDown timed out.",
                    source.Path,
                    isTransient: true,
                    exception);
            }
            catch (JsonException exception)
            {
                throw new ContentIngestionException(
                    "MarkItDown returned malformed JSON.",
                    source.Path,
                    isTransient: true,
                    exception);
            }
            catch (MarkItDownProtocolException exception)
            {
                throw new ContentIngestionException(
                    exception.Message,
                    source.Path,
                    isTransient: true,
                    exception);
            }
            catch (InvalidDataException exception)
            {
                throw new ContentIngestionException(
                    exception.Message,
                    source.Path,
                    isTransient: false,
                    exception);
            }
            catch (FileNotFoundException exception)
            {
                throw new ContentIngestionException(
                    "The document does not exist.",
                    source.Path,
                    isTransient: false,
                    exception);
            }
            catch (DirectoryNotFoundException exception)
            {
                throw new ContentIngestionException(
                    "The document does not exist.",
                    source.Path,
                    isTransient: false,
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new ContentIngestionException(
                    "The document could not be accessed.",
                    source.Path,
                    isTransient: true,
                    exception);
            }
            catch (IOException exception)
            {
                throw new ContentIngestionException(
                    requestStarted
                        ? "The MarkItDown response could not be read."
                        : "The document could not be read.",
                    source.Path,
                    isTransient: true,
                    exception);
            }
            finally
            {
                _conversionGate.Release();
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    private void RecordRequestMemory(
        ClientRequestMemoryObservation observation)
    {
        lock (_memoryLock)
        {
            if (_largestRequestMemoryObservation is null ||
                observation.SerializedRequestBytes >
                    _largestRequestMemoryObservation.SerializedRequestBytes)
            {
                _largestRequestMemoryObservation = observation;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposeGate.Wait(_disposeTimeout))
        {
            throw new TimeoutException(
                "Timed out waiting for another caller to dispose the MarkItDown reader.");
        }

        try
        {
            Task drain;
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposing = true;
                if (_activeOperations == 0)
                {
                    drain = Task.CompletedTask;
                }
                else
                {
                    _drained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drain = _drained.Task;
                }
            }

            if (!WaitWithoutPropagating(drain, _disposeTimeout))
            {
                throw new TimeoutException(
                    "Timed out draining MarkItDown conversions; the HTTP and semaphore state were left intact.");
            }

            drain.GetAwaiter().GetResult();
            _conversionGate.Dispose();
            lock (_lifetimeLock)
            {
                _disposed = true;
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    internal static async ValueTask<byte[]> ReadBoundedDocumentAsync(
        Stream stream,
        long maximumDocumentBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumDocumentBytes is <= 0 or > int.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
        }

        using MemoryStream buffer = new();
        byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                int count = await stream.ReadAsync(rented, cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (buffer.Length + count > maximumDocumentBytes)
                {
                    throw new InvalidDataException(
                        $"The document exceeds the {maximumDocumentBytes}-byte ingestion limit.");
                }

                await buffer.WriteAsync(
                    rented.AsMemory(0, count),
                    cancellationToken).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static string ParseMarkdown(string json, long requestId, string sourcePath)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetString(root, "jsonrpc", out string? protocol) ||
            !string.Equals(protocol, "2.0", StringComparison.Ordinal))
        {
            throw new JsonException("MarkItDown returned an invalid JSON-RPC envelope.");
        }

        if (!root.TryGetProperty("id", out JsonElement id) ||
            id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out long responseId) ||
            responseId != requestId)
        {
            throw new JsonException(
                "MarkItDown returned a mismatched JSON-RPC response identifier.");
        }

        bool hasError = root.TryGetProperty("error", out JsonElement error);
        bool hasResult = root.TryGetProperty("result", out JsonElement result);
        if (hasError == hasResult)
        {
            throw new JsonException(
                "MarkItDown returned neither or both JSON-RPC result and error.");
        }

        if (hasError)
        {
            if (error.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    "MarkItDown returned an invalid JSON-RPC error object.");
            }

            if (!error.TryGetProperty("code", out JsonElement code) ||
                code.ValueKind != JsonValueKind.Number ||
                !code.TryGetInt32(out int codeValue))
            {
                throw new JsonException(
                    "MarkItDown returned an invalid JSON-RPC error code.");
            }

            bool transient = codeValue is not (-32602 or -32002);
            throw new ContentIngestionException(
                $"MarkItDown reported a conversion error (code {codeValue}).",
                sourcePath,
                transient);
        }

        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("MarkItDown returned an invalid JSON-RPC response.");
        }

        if (result.TryGetProperty("isError", out JsonElement isError))
        {
            if (isError.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new JsonException(
                    "MarkItDown returned an invalid tool error flag.");
            }

            if (isError.GetBoolean())
            {
                throw new ContentIngestionException(
                    "MarkItDown reported a tool conversion error.",
                    sourcePath,
                    isTransient: true);
            }
        }

        List<string> textContent = [];
        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetString(item, "type", out string? type))
            {
                throw new JsonException(
                    "MarkItDown returned an invalid content item.");
            }

            if (!string.Equals(type, "text", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryGetString(item, "text", out string? text))
            {
                throw new JsonException(
                    "MarkItDown returned a text item without string content.");
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                textContent.Add(text);
            }
        }

        string markdown = string.Join("\n", textContent);
        return markdown.Length > 0
            ? markdown
            : throw new ContentIngestionException(
                "MarkItDown returned no text content.",
                sourcePath,
                isTransient: false);
    }

    private static bool TryGetString(
        JsonElement value,
        string propertyName,
        out string? result)
    {
        if (value.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString();
            return true;
        }

        result = null;
        return false;
    }

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new MarkItDownProtocolException(
                "MarkItDown response exceeded the configured limit.");
        }

        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] rented = new byte[64 * 1024];
        while (true)
        {
            int count = await stream.ReadAsync(rented, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > MaximumResponseBytes)
            {
                throw new MarkItDownProtocolException(
                    "MarkItDown response exceeded the configured limit.");
            }

            await buffer.WriteAsync(rented.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private void EnterOperation()
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposing || _disposed, this);
            _activeOperations++;
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifetimeLock)
        {
            _activeOperations--;
            if (_disposing && _activeOperations == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    private static bool WaitWithoutPropagating(Task task, TimeSpan timeout) =>
        ReferenceEquals(
            Task.WhenAny(task, Task.Delay(timeout)).GetAwaiter().GetResult(),
            task);

    private sealed class MarkItDownProtocolException(string message)
        : Exception(message);
}

public sealed record ClientRequestMemoryObservation(
    long SourceBytes,
    long SerializedRequestBytes,
    long BeforeCurrentWorkingSetBytes,
    long BeforePeakWorkingSetBytes,
    long AfterCurrentWorkingSetBytes,
    long AfterPeakWorkingSetBytes);

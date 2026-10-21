using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;
using Jellyfin.SemanticDiscovery.Core.Ingestion;
using Jellyfin.SemanticDiscovery.IntegrationTests.TestSupport;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Indexing;

public sealed class MarkItDownMcpReaderTests : IDisposable
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    private static readonly Uri Endpoint = new("https://markitdown.local/mcp");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"semantic-markitdown-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateFile(string name, byte[] content)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private void WriteIntegritySidecar(
        string sourceFileName,
        ContentSourceKind kind,
        byte[] exactBytes)
    {
        File.WriteAllBytes(
            Path.Combine(_directory, "semantic-discovery.metadata.json"),
            JsonSerializer.SerializeToUtf8Bytes(new SemanticMetadataSidecar
            {
                SchemaVersion = 2,
                ProjectionHashContract =
                    Jellyfin.SemanticDiscovery.Core.Projection
                        .MetadataProjectionIdentity.ContractId,
                Kind = MediaKind.Book,
                ItemType = "Book",
                MediaType = "Book",
                Title = "Book",
                ProductionYear = 2024,
                Overview = "Overview",
                Genres = [],
                Tags = [],
                People = [],
                Studios = [],
                ProjectedMetadataSha256 = new string('0', 64),
                ContentSources =
                [
                    new SemanticSourceLock
                    {
                        FileName = sourceFileName,
                        Kind = kind,
                        Size = exactBytes.Length,
                        Sha256 = Convert.ToHexStringLower(
                            SHA256.HashData(exactBytes)),
                    },
                ],
            }));
    }

    private static (MarkItDownMcpReader Reader, ScriptedHttpMessageHandler Handler) CreateReader(
        long maximumDocumentBytes = 8 * 1024 * 1024,
        long maximumRequestBytes = 12 * 1024 * 1024)
    {
        ScriptedHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        MarkItDownMcpReader reader = new(
            httpClient,
            Endpoint,
            new WordCountTokenCounter(),
            maximumDocumentBytes,
            maximumRequestBytes: maximumRequestBytes,
            maximumTokens: 200);
        return (reader, handler);
    }

    private static HttpResponseMessage SuccessJsonRpcResponse(long requestId, string markdown)
    {
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
        return JsonResponse(payload);
    }

    private static HttpResponseMessage JsonResponse(object payload)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        return response;
    }

    [Fact]
    public void RuntimeHttpHandlerDisablesRedirectsThatCouldReplayDocumentBytes()
    {
        using SocketsHttpHandler handler =
            MarkItDownMcpReader.CreateHttpHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task ProbeAsyncUsesTheExactPathPrefixedMcpEndpoint()
    {
        Uri endpoint =
            new("https://converter.example/private/markitdown/mcp");
        ScriptedHttpMessageHandler handler = new();
        handler.Handler = (_, _) => Task.FromResult(JsonResponse(new
        {
            jsonrpc = "2.0",
            id = 0,
            result = new
            {
                tools = new[]
                {
                    new { name = "convert_to_markdown" },
                },
            },
        }));
        using HttpClient client = new(handler);

        await MarkItDownMcpReader.ProbeAsync(
            client,
            endpoint,
            CancellationToken.None);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(endpoint, request.RequestUri);
        Assert.Equal(HttpMethod.Post, request.Method);
        using JsonDocument payload = JsonDocument.Parse(request.Body);
        Assert.Equal(
            "tools/list",
            payload.RootElement.GetProperty("method").GetString());
        Assert.DoesNotContain("/health", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ProbeAsyncNormalizesMalformedJsonToInvalidData()
    {
        ScriptedHttpMessageHandler handler = new();
        handler.Handler = (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{not-json",
                    Encoding.UTF8,
                    "application/json"),
            });
        using HttpClient client = new(handler);

        InvalidDataException exception =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => MarkItDownMcpReader.ProbeAsync(
                    client,
                    new Uri("https://converter.example/prefix/mcp"),
                    CancellationToken.None));

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task ProbeAsyncNormalizesOversizedResponseToInvalidData()
    {
        ByteArrayContent content = new("x"u8.ToArray());
        content.Headers.ContentLength = 16 * 1024 * 1024 + 1;
        ScriptedHttpMessageHandler handler = new();
        handler.Handler = (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        using HttpClient client = new(handler);

        InvalidDataException exception =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => MarkItDownMcpReader.ProbeAsync(
                    client,
                    new Uri("https://converter.example/prefix/mcp"),
                    CancellationToken.None));

        Assert.Contains(
            "readiness response was malformed",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncTreatsRedirectAsTransientWithoutIssuingAnotherRequest()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers =
                {
                    Location = new Uri("http://169.254.169.254/latest/meta-data"),
                },
            });
        string path = CreateFile("book.pdf", "private bytes"u8.ToArray());

        ContentIngestionException exception =
            await Assert.ThrowsAsync<ContentIngestionException>(
                () => reader.ReadAsync(
                    new ContentSourceDescriptor
                    {
                        OwnerId = Guid.NewGuid(),
                        Kind = ContentSourceKind.Pdf,
                        Path = path,
                    },
                    CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RuntimeHttpClientDoesNotReplayRedirectedDocumentToTarget()
    {
        int sourcePort = GetFreePort();
        int targetPort = GetFreePort();
        using HttpListener sourceListener = new();
        using HttpListener targetListener = new();
        sourceListener.Prefixes.Add($"http://127.0.0.1:{sourcePort}/");
        targetListener.Prefixes.Add($"http://127.0.0.1:{targetPort}/");
        sourceListener.Start();
        targetListener.Start();
        Task<int> sourceRequest = Task.Run(async () =>
        {
            HttpListenerContext context =
                await sourceListener.GetContextAsync();
            using MemoryStream body = new();
            await context.Request.InputStream.CopyToAsync(body);
            context.Response.StatusCode =
                (int)HttpStatusCode.TemporaryRedirect;
            context.Response.RedirectLocation =
                $"http://127.0.0.1:{targetPort}/capture";
            context.Response.ContentLength64 = 0;
            context.Response.Close();
            return checked((int)body.Length);
        });
        Task<HttpListenerContext> targetRequest =
            targetListener.GetContextAsync();
        using HttpClient client =
            MarkItDownMcpReader.CreateHttpClient(TimeSpan.FromSeconds(5));
        using MarkItDownMcpReader reader = new(
            client,
            new Uri($"http://127.0.0.1:{sourcePort}/mcp"),
            new WordCountTokenCounter());
        string path = CreateFile("redirect.pdf", "private document bytes"u8.ToArray());

        ContentIngestionException exception =
            await Assert.ThrowsAsync<ContentIngestionException>(
                () => reader.ReadAsync(
                    new ContentSourceDescriptor
                    {
                        OwnerId = Guid.NewGuid(),
                        Kind = ContentSourceKind.Pdf,
                        Path = path,
                    },
                    CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.True(await sourceRequest > 0);
        await Assert.ThrowsAsync<TimeoutException>(
            () => targetRequest.WaitAsync(TimeSpan.FromMilliseconds(500)));
    }

    private static long GetRequestId(CapturedRequest request)
    {
        using JsonDocument document = JsonDocument.Parse(request.Body);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ReadAsyncConvertsPdfDocumentThroughTheHeadingAwareChunker()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (request, _) =>
            Task.FromResult(SuccessJsonRpcResponse(
                GetRequestId(request),
                "# Heading\n\nBody text here."));
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        IReadOnlyList<ContentChunk> chunks = await reader.ReadAsync(source, CancellationToken.None);

        ContentChunk chunk = Assert.Single(chunks);
        Assert.Equal("Heading", chunk.Heading);
        Assert.Contains("Body text here.", chunk.Text, StringComparison.Ordinal);
        Assert.Equal(ContentSourceKind.Pdf, chunk.SourceKind);
        ClientRequestMemoryObservation memory =
            Assert.IsType<ClientRequestMemoryObservation>(
                reader.LargestRequestMemoryObservation);
        Assert.Equal("pdf-bytes"u8.Length, memory.SourceBytes);
        Assert.Equal(
            MarkItDownMcpReader.MeasureSerializedRequestBytes(
                "pdf-bytes"u8.ToArray(),
                "application/pdf"),
            memory.SerializedRequestBytes);
        Assert.True(memory.BeforeCurrentWorkingSetBytes > 0);
        Assert.True(memory.BeforePeakWorkingSetBytes > 0);
        Assert.True(memory.AfterPeakWorkingSetBytes > 0);
    }

    [Fact]
    public async Task ReadAsyncConvertsEpubDocumentThroughTheHeadingAwareChunker()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (request, _) =>
            Task.FromResult(SuccessJsonRpcResponse(GetRequestId(request), "# Chapter\n\nEpub body."));
        string path = CreateFile("book.epub", "epub-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Epub,
            Path = path,
        };

        IReadOnlyList<ContentChunk> chunks = await reader.ReadAsync(source, CancellationToken.None);

        ContentChunk chunk = Assert.Single(chunks);
        Assert.Equal(ContentSourceKind.Epub, chunk.SourceKind);
        Assert.Contains("Epub body.", chunk.Text, StringComparison.Ordinal);

        CapturedRequest request = Assert.Single(handler.Requests);
        using JsonDocument document = JsonDocument.Parse(request.Body);
        string dataUri = document.RootElement
            .GetProperty("params").GetProperty("arguments").GetProperty("source").GetString()!;
        Assert.StartsWith("data:application/epub+zip;base64,", dataUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncRejectsBytesThatDoNotMatchSidecarBeforeSendingHttp()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        byte[] expected = "expected-pdf"u8.ToArray();
        string path = CreateFile("book.pdf", "mutated-pdf"u8.ToArray());
        WriteIntegritySidecar("book.pdf", ContentSourceKind.Pdf, expected);

        ContentIngestionException exception =
            await Assert.ThrowsAsync<ContentIngestionException>(
                () => reader.ReadAsync(
                    new ContentSourceDescriptor
                    {
                        OwnerId = Guid.NewGuid(),
                        Kind = ContentSourceKind.Pdf,
                        Path = path,
                    },
                    CancellationToken.None).AsTask());

        Assert.False(exception.IsTransient);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadAsyncPostsToTheConfiguredEndpoint()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (request, _) =>
            Task.FromResult(SuccessJsonRpcResponse(GetRequestId(request), "# H\n\nBody."));
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        await reader.ReadAsync(source, CancellationToken.None);

        CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Endpoint, request.RequestUri);
    }

    [Fact]
    public async Task ReadAsyncRequestBodyIsABoundedJsonRpcToolsCallWithDataUriSourceAndNoPlaintextPath()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (request, _) =>
            Task.FromResult(SuccessJsonRpcResponse(GetRequestId(request), "# H\n\nBody."));
        byte[] fileBytes = "pdf-bytes"u8.ToArray();
        string path = CreateFile("secret-book.pdf", fileBytes);
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        await reader.ReadAsync(source, CancellationToken.None);

        CapturedRequest request = Assert.Single(handler.Requests);
        using JsonDocument document = JsonDocument.Parse(request.Body);
        JsonElement root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("tools/call", root.GetProperty("method").GetString());
        JsonElement parameters = root.GetProperty("params");
        Assert.Equal("convert_to_markdown", parameters.GetProperty("name").GetString());
        string dataUri = parameters.GetProperty("arguments").GetProperty("source").GetString()!;
        string expectedDataUri = $"data:application/pdf;base64,{Convert.ToBase64String(fileBytes)}";
        Assert.Equal(expectedDataUri, dataUri);

        // The local file path never appears anywhere in the request payload.
        Assert.DoesNotContain(path, request.BodyAsUtf8Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-book.pdf", request.BodyAsUtf8Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncRejectsSerializedRequestOneByteAboveConfiguredCap()
    {
        byte[] bytes = "pdf-bytes-with-plus-sensitive-base64-shape"u8.ToArray();
        int requestBytes =
            MarkItDownMcpReader.MeasureSerializedRequestBytes(
                bytes,
                "application/pdf");
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) =
            CreateReader(maximumRequestBytes: requestBytes - 1);
        string path = CreateFile("request-cap.pdf", bytes);

        ContentIngestionException exception =
            await Assert.ThrowsAsync<ContentIngestionException>(
                () => reader.ReadAsync(
                    new ContentSourceDescriptor
                    {
                        OwnerId = Guid.NewGuid(),
                        Kind = ContentSourceKind.Pdf,
                        Path = path,
                    },
                    CancellationToken.None).AsTask());

        Assert.False(exception.IsTransient);
        Assert.Contains(
            "serialized document request",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadAsyncRejectsDocumentExceedingConfiguredByteLimitWithoutSendingAnyRequest()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader(maximumDocumentBytes: 4);
        string path = CreateFile("too-big.pdf", "this document is definitely bigger than 4 bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.False(exception.IsTransient);
        Assert.Contains("ingestion limit", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadAsyncThrowsContentIngestionExceptionWhenFileIsMissing()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = Path.Combine(_directory, "does-not-exist.pdf"),
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.False(exception.IsTransient);
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadAsyncRejectsResponseExceedingConfiguredByteLimitViaContentLengthHeader()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeclaredLengthContent(MaximumResponseBytes + 1),
        });
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Contains("exceeded the configured limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncRejectsResponseExceedingConfiguredByteLimitViaStreamedLength()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UndeclaredLengthContent(MaximumResponseBytes + (64 * 1024)),
        });
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Contains("exceeded the configured limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncTreatsResponseStreamIoFailureAsTransient()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingResponseContent(),
        });
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(
                new ContentSourceDescriptor
                {
                    OwnerId = Guid.NewGuid(),
                    Kind = ContentSourceKind.Pdf,
                    Path = path,
                },
                CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Contains("response could not be read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncTreatsMalformedJsonRpcErrorAsTransientAndRedactsRemoteMessage()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(JsonResponse(new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new { message = "boom" },
        }));
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.DoesNotContain("boom", exception.Message, StringComparison.Ordinal);
        Assert.Contains("malformed JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncWrapsMalformedJsonResponseAsATransientContentIngestionException()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ this is not valid json", Encoding.UTF8, "application/json"),
        });
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Contains("malformed JSON", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-32602, false)]
    [InlineData(-32002, false)]
    [InlineData(-32001, true)]
    [InlineData(-32603, true)]
    [InlineData(-32123, true)]
    public async Task ReadAsyncClassifiesJsonRpcCodesWithoutPersistingRemoteText(
        int code,
        bool expectedTransient)
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(JsonResponse(new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new
            {
                code,
                message = "SECRET_REMOTE_DOCUMENT_TEXT",
            },
        }));
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(
                new ContentSourceDescriptor
                {
                    OwnerId = Guid.NewGuid(),
                    Kind = ContentSourceKind.Pdf,
                    Path = path,
                },
                CancellationToken.None).AsTask());

        Assert.Equal(expectedTransient, exception.IsTransient);
        Assert.Contains(
            code.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SECRET_REMOTE_DOCUMENT_TEXT",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncTreatsMismatchedResponseIdAsTransient()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(
            SuccessJsonRpcResponse(999, "# Heading\n\nBody."));
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(
                new ContentSourceDescriptor
                {
                    OwnerId = Guid.NewGuid(),
                    Kind = ContentSourceKind.Pdf,
                    Path = path,
                },
                CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Contains("malformed JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncTreatsToolErrorFlagAsTransient()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) => Task.FromResult(JsonResponse(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                isError = true,
                content = Array.Empty<object>(),
            },
        }));
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(
                new ContentSourceDescriptor
                {
                    OwnerId = Guid.NewGuid(),
                    Kind = ContentSourceKind.Pdf,
                    Path = path,
                },
                CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task ReadAsyncWrapsHttpFailureAsATransientContentIngestionException()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Contains("unavailable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncWrapsNonUserTimeoutAsATransientContentIngestionException()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        handler.Handler = (_, _) =>
            throw new TaskCanceledException("Simulated timeout, unrelated to the caller's token.");
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };

        ContentIngestionException exception = await Assert.ThrowsAsync<ContentIngestionException>(
            () => reader.ReadAsync(source, CancellationToken.None).AsTask());

        Assert.True(exception.IsTransient);
        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsyncPropagatesUserCancellationUnwrappedAndNeverSendsARequest()
    {
        (MarkItDownMcpReader reader, ScriptedHttpMessageHandler handler) = CreateReader();
        string path = CreateFile("book.pdf", "pdf-bytes"u8.ToArray());
        ContentSourceDescriptor source = new()
        {
            OwnerId = Guid.NewGuid(),
            Kind = ContentSourceKind.Pdf,
            Path = path,
        };
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(source, cancelled.Token).AsTask());

        Assert.IsNotType<ContentIngestionException>(exception);
        Assert.Empty(handler.Requests);
    }

    private static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

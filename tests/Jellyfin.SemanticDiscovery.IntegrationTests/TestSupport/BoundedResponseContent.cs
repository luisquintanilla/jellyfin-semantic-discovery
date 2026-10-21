using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.TestSupport;

/// <summary>
/// Content whose declared <c>Content-Length</c> is a fixed value that need
/// not match the (small) real payload actually written — used to exercise
/// <c>MarkItDownMcpReader</c>'s header-based bounded-response check without
/// materializing a huge real body.
/// </summary>
public sealed class DeclaredLengthContent : HttpContent
{
    private readonly long _declaredLength;
    private readonly byte[] _actualBody;

    public DeclaredLengthContent(long declaredLength, string actualBody = "{}")
    {
        _declaredLength = declaredLength;
        _actualBody = Encoding.UTF8.GetBytes(actualBody);
        Headers.ContentType = new MediaTypeHeaderValue("application/json");
        Headers.ContentLength = declaredLength;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(_actualBody).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = _declaredLength;
        return true;
    }
}

/// <summary>
/// Content that streams a real, exact number of bytes but deliberately
/// reports no computable/declared length (so <c>Content-Length</c> is
/// absent), forcing <c>MarkItDownMcpReader</c> to discover an over-limit
/// response only while streaming it.
/// </summary>
public sealed class UndeclaredLengthContent(long totalBytes) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        byte[] chunk = new byte[64 * 1024];
        Array.Fill(chunk, (byte)' ');
        long written = 0;
        while (written < totalBytes)
        {
            int toWrite = (int)Math.Min(chunk.Length, totalBytes - written);
            await stream.WriteAsync(chunk.AsMemory(0, toWrite)).ConfigureAwait(false);
            written += toWrite;
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>
/// Content whose response stream always fails while being read, simulating
/// a connection dropped mid-response after headers were already received.
/// </summary>
public sealed class ThrowingResponseContent : HttpContent
{
    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context) =>
        Task.FromException(new IOException("simulated response stream failure"));

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new ThrowingReadStream());

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated response stream failure");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new IOException("simulated response stream failure"));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;

namespace Jellyfin.SemanticDiscovery.Extraction.Jobs;

public interface IExtractionJobChannel
{
    bool IsDurable { get; }

    ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken);

    ValueTask<string> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class BoundedExtractionJobChannel : IExtractionJobChannel
{
    private readonly Channel<string> _channel;

    public BoundedExtractionJobChannel(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    public bool IsDurable => false;

    public ValueTask EnqueueAsync(
        string jobId,
        CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(jobId, cancellationToken);

    public ValueTask<string> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}

public interface IExtractionCancellationRegistry
{
    CancellationToken Register(string jobId, CancellationToken stoppingToken);

    bool Cancel(string jobId);

    void Complete(string jobId);
}

public sealed class ExtractionCancellationRegistry :
    IExtractionCancellationRegistry,
    IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource>
        _sources = new(StringComparer.Ordinal);

    public CancellationToken Register(
        string jobId,
        CancellationToken stoppingToken)
    {
        CancellationTokenSource source =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_sources.TryAdd(jobId, source))
        {
            source.Dispose();
            throw new InvalidOperationException(
                $"Extraction job '{jobId}' is already running.");
        }

        return source.Token;
    }

    public bool Cancel(string jobId)
    {
        if (!_sources.TryGetValue(jobId, out CancellationTokenSource? source))
        {
            return false;
        }

        source.Cancel();
        return true;
    }

    public void Complete(string jobId)
    {
        if (_sources.TryRemove(jobId, out CancellationTokenSource? source))
        {
            source.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource source in _sources.Values)
        {
            source.Dispose();
        }

        _sources.Clear();
    }
}

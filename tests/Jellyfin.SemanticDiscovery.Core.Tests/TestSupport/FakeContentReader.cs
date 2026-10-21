using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Indexing;

namespace Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

/// <summary>
/// A scripted <see cref="IContentReader"/> that returns pre-configured chunks
/// (or throws a pre-configured <see cref="ContentIngestionException"/>) for a
/// given descriptor, and records every descriptor it was asked to read.
/// </summary>
public sealed class FakeContentReader(params ContentSourceKind[] supportedKinds) : IContentReader
{
    private readonly HashSet<ContentSourceKind> _supportedKinds = [.. supportedKinds];

    private readonly Dictionary<Key, IReadOnlyList<ContentChunk>> _chunks = [];

    private readonly Dictionary<Key, ContentIngestionException> _failures = [];

    public List<ContentSourceDescriptor> ReadCalls { get; } = [];

    public bool Supports(ContentSourceKind kind) => _supportedKinds.Contains(kind);

    public void SetChunks(ContentSourceDescriptor descriptor, IReadOnlyList<ContentChunk> chunks) =>
        _chunks[ToKey(descriptor)] = chunks;

    public void SetFailure(ContentSourceDescriptor descriptor, ContentIngestionException exception) =>
        _failures[ToKey(descriptor)] = exception;

    public ValueTask<IReadOnlyList<ContentChunk>> ReadAsync(
        ContentSourceDescriptor source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ReadCalls.Add(source);
        Key key = ToKey(source);
        if (_failures.TryGetValue(key, out ContentIngestionException? failure))
        {
            throw failure;
        }

        IReadOnlyList<ContentChunk> chunks = _chunks.TryGetValue(key, out IReadOnlyList<ContentChunk>? value)
            ? value
            : [];
        return ValueTask.FromResult(chunks);
    }

    private static Key ToKey(ContentSourceDescriptor descriptor) =>
        new(descriptor.OwnerId, descriptor.Kind, descriptor.Path);

    private readonly record struct Key(Guid OwnerId, ContentSourceKind Kind, string Path);
}

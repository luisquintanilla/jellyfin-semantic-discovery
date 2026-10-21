using System.Runtime.CompilerServices;
using Jellyfin.SemanticDiscovery.Core.Indexing;

namespace Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

/// <summary>
/// An <see cref="IIndexSource"/> whose item list can be replaced between
/// calls (simulating a changing library across successive
/// <c>RebuildAsync</c> runs), with an optional callback fired before each
/// item is yielded so tests can trigger cancellation mid-enumeration.
/// </summary>
public sealed class MutableIndexSource : IIndexSource
{
    public IReadOnlyList<IndexSourceItem> Items { get; set; } = [];

    public Action<int>? OnBeforeItem { get; set; }

    public async IAsyncEnumerable<IndexSourceItem> GetItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<IndexSourceItem> snapshot = Items;
        for (int index = 0; index < snapshot.Count; index++)
        {
            OnBeforeItem?.Invoke(index);
            cancellationToken.ThrowIfCancellationRequested();
            yield return snapshot[index];
            await Task.Yield();
        }
    }
}

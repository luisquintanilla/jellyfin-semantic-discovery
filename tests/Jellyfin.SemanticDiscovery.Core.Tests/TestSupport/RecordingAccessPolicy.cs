using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;

namespace Jellyfin.SemanticDiscovery.Core.Tests.TestSupport;

/// <summary>
/// A scripted, recording <see cref="IMediaAccessPolicy"/>. Defaults to
/// allowing every candidate so tests only need to override
/// <see cref="Resolve"/> when they specifically want to deny access.
/// Records every call's candidate set and scope so tests can assert the
/// service made exactly one batch call with the expected structurally
/// eligible set.
/// </summary>
public sealed class RecordingAccessPolicy : IMediaAccessPolicy
{
    public List<IReadOnlyList<MetadataRecord>> CandidateCalls { get; } = [];

    public List<SearchScope> ScopeCalls { get; } = [];

    public Func<IReadOnlyList<MetadataRecord>, IReadOnlySet<Guid>> Resolve { get; set; } =
        static candidates => candidates.Select(static candidate => candidate.Id).ToHashSet();

    public ValueTask<IReadOnlySet<Guid>> GetAccessibleItemIdsAsync(
        IReadOnlyList<MetadataRecord> candidates,
        SearchScope scope,
        CancellationToken cancellationToken)
    {
        CandidateCalls.Add(candidates);
        ScopeCalls.Add(scope);
        return ValueTask.FromResult(Resolve(candidates));
    }
}

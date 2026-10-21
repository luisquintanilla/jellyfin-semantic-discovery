using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Retrieval;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed class JellyfinMediaAccessPolicy(
    IDbContextFactory<JellyfinDbContext> dbContextFactory,
    IItemTypeLookup itemTypeLookup,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IItemQueryHelpers queryHelpers)
    : IMediaAccessPolicy
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));

    private readonly IItemTypeLookup _itemTypeLookup =
        itemTypeLookup ?? throw new ArgumentNullException(nameof(itemTypeLookup));

    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));

    private readonly IUserManager _userManager =
        userManager ?? throw new ArgumentNullException(nameof(userManager));

    private readonly IItemQueryHelpers _queryHelpers =
        queryHelpers ?? throw new ArgumentNullException(nameof(queryHelpers));

    public async ValueTask<IReadOnlySet<Guid>> GetAccessibleItemIdsAsync(
        IReadOnlyList<MetadataRecord> candidates,
        SearchScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(scope);
        if (candidates.Count == 0 || scope.UserId == Guid.Empty)
        {
            return new HashSet<Guid>();
        }

        Guid[] candidateIds = candidates
            .Select(static item => item.Id)
            .Distinct()
            .ToArray();
        User? user = _userManager.GetUserById(scope.UserId);
        if (user is null)
        {
            return new HashSet<Guid>();
        }

        await using JellyfinDbContext dbContext =
            await _dbContextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
        IQueryable<BaseItemEntity> query = dbContext.BaseItems
            .AsNoTracking()
            .Where(item => candidateIds.Contains(item.Id));

        BaseItemKind[] includedKinds = ParseKinds(scope.IncludeItemTypes);
        BaseItemKind[] excludedKinds = ParseKinds(scope.ExcludeItemTypes);
        InternalItemsQuery accessFilter =
            CreateUserFilter(scope, includedKinds, excludedKinds, user);
        query = _queryHelpers.ApplyAccessFiltering(
            dbContext,
            query,
            accessFilter);

        if (includedKinds.Length > 0)
        {
            string[] names = MapKindNames(includedKinds);
            query = query.Where(item => names.Contains(item.Type));
        }
        if (excludedKinds.Length > 0)
        {
            string[] names = MapKindNames(excludedKinds);
            query = query.Where(item => !names.Contains(item.Type));
        }

        if (scope.MediaTypes.Count > 0)
        {
            string[] mediaTypes = scope.MediaTypes.ToArray();
            query = query.Where(item =>
                item.MediaType != null && mediaTypes.Contains(item.MediaType));
        }

        if (scope.ParentId is { } parentId && parentId != Guid.Empty)
        {
            query = query.Where(item =>
                item.ParentId == parentId ||
                item.Parents!.Any(parent => parent.ParentItemId == parentId));
        }

        return (await query
                .Select(static item => item.Id)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();
    }

    private InternalItemsQuery CreateUserFilter(
        SearchScope scope,
        BaseItemKind[] includedKinds,
        BaseItemKind[] excludedKinds,
        User user)
    {
        InternalItemsQuery filter = new(user)
        {
            IncludeItemTypes = includedKinds,
            ExcludeItemTypes = excludedKinds,
            IncludeItemsByName = !scope.ParentId.HasValue || scope.ParentId == Guid.Empty,
        };
        _libraryManager.ConfigureUserAccess(filter, user);
        return filter;
    }

    private static BaseItemKind[] ParseKinds(IEnumerable<string> values) =>
        values
            .Select(static value =>
                Enum.TryParse(value, ignoreCase: false, out BaseItemKind kind)
                    ? (BaseItemKind?)kind
                    : null)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();

    private string[] MapKindNames(IEnumerable<BaseItemKind> kinds) =>
        kinds
            .Select(kind => _itemTypeLookup.BaseItemKindNames.TryGetValue(
                    kind,
                    out string? name)
                ? name
                : null)
            .Where(static name => name is not null)
            .Cast<string>()
            .ToArray();
}

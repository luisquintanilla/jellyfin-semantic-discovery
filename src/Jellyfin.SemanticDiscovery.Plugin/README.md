# Plugin

The Jellyfin plugin. It targets `net10.0` because that's Jellyfin 12's runtime, and it
has no AI code: it forwards to the semantic service over HTTP and applies Jellyfin's
permissions.

| File | Jellyfin extension point | What it does |
| --- | --- | --- |
| [SemanticSearchProvider.cs](SemanticSearchProvider.cs) | `IInternalSearchProvider` | Adds semantic hits to the search box, minus items the user can't see |
| [SimilarItemsProviders.cs](SimilarItemsProviders.cs) | `ILocalSimilarItemsProvider<T>` | "More Like This" for movies, series, songs, and books |
| [SyncTask.cs](SyncTask.cs) | `IScheduledTask` | Sends item text and book PDFs to the service (at startup, then daily) |
| [SemanticServiceClient.cs](SemanticServiceClient.cs) | | The four HTTP calls |
| [ServiceRegistrator.cs](ServiceRegistrator.cs) | `IPluginServiceRegistrator` | DI registrations |
| [Plugin.cs](Plugin.cs) | `BasePlugin` | Plugin identity |

**Permissions:** `libraryManager.GetItemById(id, user)` returns `null` for items the user
can't access, so the plugin drops those hits. The service never sees user data.

**Service address:** `services__semantic-service__http__0` (set by Aspire), else
`SEMANTIC_SERVICE_URL`, else `http://localhost:5180`.

The plugin has no settings page. To use it outside Aspire, copy
`Jellyfin.SemanticDiscovery.Plugin.dll` and `meta.json` to
`<jellyfin config>/plugins/Semantic Discovery_0.1.0.0/` and set `SEMANTIC_SERVICE_URL`.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Setup.Showcase;

namespace Jellyfin.SemanticDiscovery.Setup;

public sealed record JellyfinBootstrapOptions
{
    public required Uri BaseAddress { get; init; }

    public string AdminUserName { get; init; } = "semantic-admin";

    public required string AdminPassword { get; init; }

    public string? CredentialsPath { get; init; }

    public string? LibraryManifestPath { get; init; }

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

public sealed class JellyfinBootstrapper(
    HttpClient httpClient,
    JellyfinBootstrapOptions options)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string Authorization =
        "MediaBrowser Client=\"Semantic Discovery Setup\", " +
        "Device=\"Aspire\", DeviceId=\"semantic-discovery-bootstrap\", Version=\"0.1.0\"";
    private static readonly string[] SemanticProviderOrder = ["Semantic Discovery"];
    private static readonly string[] NoRemoteProviders =
        ["Semantic Discovery: local sidecars only"];
    private static readonly string[] DisabledEpubReaders =
        ["EPUB Metadata"];
    private static readonly string[] DisabledSubtitleFetchers =
        ["Open Subtitles", "OpenSubtitles"];
    private static readonly LibrarySpec[] SyntheticLibraries =
    [
        new(
            "Semantic Movies",
            "movies",
            "Movie",
            "/media/Movies",
            [
                new(
                    "The Signal Garden",
                    "Movie",
                    "/media/Movies/The Signal Garden (2024)/The Signal Garden (2024).mp4",
                    "At a remote listening post, an aurora cartographer maps the silence between orbits, cataloguing faint signals that no one else has thought to name.",
                    ["Science Fiction"],
                    ["restricted-semantic"],
                    ["Silver Engine Fixtures"],
                    "R"),
                new(
                    "Orbit Almanac",
                    "Movie",
                    "/media/Movies/Orbit Almanac (2024)/Orbit Almanac (2024).mp4",
                    "A polar radio keeper charts aurora transmissions between silent orbital stations and preserves each signal in a hand-drawn almanac.",
                    ["Documentary"],
                    [],
                    ["Northern Almanac Workshop"],
                    "G"),
            ]),
        new(
            "Semantic Series",
            "tvshows",
            "Series",
            "/media/Series",
            [
                new(
                    "Harbor Static",
                    "Series",
                    "/media/Series/Harbor Static (2024)",
                    "A quiet coastal town goes about its ordinary business near the harbor.",
                    ["Drama"],
                    [],
                    ["Silver Engine Fixtures"]),
                new(
                    "Low Tide Static",
                    "Episode",
                    "/media/Series/Harbor Static (2024)/Season 01/Harbor Static (2024) S01E01.mp4",
                    "The tide comes in, the tide goes out, and nothing unusual happens today."),
                new(
                    "Tidal Register",
                    "Series",
                    "/media/Series/Tidal Register (2024)",
                    "A dockworker records ordinary tide schedules and quiet morning arrivals in a harbor ledger.",
                    ["Documentary"],
                    [],
                    ["Tidal Register Workshop"]),
                new(
                    "Morning Entries",
                    "Episode",
                    "/media/Series/Tidal Register (2024)/Season 01/Tidal Register (2024) S01E01.mp4",
                    "The morning tide arrives and each boat is entered in the ledger."),
            ]),
        new(
            "Semantic Books",
            "books",
            "Book",
            "/media/Books",
            [
                new(
                    "Field Notes on Static",
                    "Book",
                    "/media/Books/Field Notes on Static (2024)/Field Notes on Static (2024).pdf",
                    "An original synthetic field log used only as a repository fixture for retrieval demos."),
                new(
                    "Notes from the Choir Loft",
                    "Book",
                    "/media/Books/Notes from the Choir Loft (2024)/Notes from the Choir Loft (2024).epub",
                    "An original synthetic short story used only as a repository fixture for retrieval demos."),
            ]),
    ];

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly JellyfinBootstrapOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly LibrarySpec[] _libraries =
        LoadLibraries(options?.LibraryManifestPath);

    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        _httpClient.BaseAddress = _options.BaseAddress;
        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);

        using JsonDocument publicInfo =
            await GetJsonAsync("System/Info/Public", cancellationToken)
                .ConfigureAwait(false);
        bool startupComplete = publicInfo.RootElement.TryGetProperty(
            "StartupWizardCompleted",
            out JsonElement startupProperty) &&
            startupProperty.GetBoolean();

        string? accessToken = null;
        if (!startupComplete)
        {
            using JsonDocument startupUser =
                await GetJsonAsync("Startup/User", cancellationToken)
                    .ConfigureAwait(false);
            accessToken = await TryAuthenticateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (accessToken is null)
            {
                using HttpResponseMessage setUser =
                    await _httpClient.PostAsJsonAsync(
                        "Startup/User",
                        new
                        {
                            Name = _options.AdminUserName,
                            Password = _options.AdminPassword,
                        },
                        cancellationToken).ConfigureAwait(false);
                if (setUser.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException(
                        "The startup administrator already has a different password; refusing to overwrite it.");
                }

                setUser.EnsureSuccessStatusCode();
                accessToken = await AuthenticateAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            SetAuthorizationToken(accessToken);
            await PostJsonAsync(
                "Startup/Configuration",
                new
                {
                    UICulture = "en-US",
                    MetadataCountryCode = "US",
                    PreferredMetadataLanguage = "en",
                },
                allowForbidden: false,
                cancellationToken).ConfigureAwait(false);
            await PostJsonAsync(
                "Startup/RemoteAccess",
                new
                {
                    EnableRemoteAccess = false,
                    EnableAutomaticPortMapping = false,
                },
                allowForbidden: false,
                cancellationToken).ConfigureAwait(false);
            await PostJsonAsync(
                "Startup/Complete",
                new { },
                allowForbidden: false,
                cancellationToken).ConfigureAwait(false);
        }

        accessToken ??= await AuthenticateAsync(cancellationToken)
            .ConfigureAwait(false);
        SetAuthorizationToken(accessToken);

        IReadOnlyDictionary<string, Guid> libraryIds =
            await EnsureLibrariesAsync(cancellationToken).ConfigureAwait(false);

        await RunLibraryScanAsync(cancellationToken).ConfigureAwait(false);
        await WaitForItemsAsync(libraryIds, cancellationToken).ConfigureAwait(false);
        long completedGeneration =
            await RunSemanticIndexTaskAsync(cancellationToken).ConfigureAwait(false);
        await VerifySemanticIndexStatusAsync(
            completedGeneration,
            cancellationToken).ConfigureAwait(false);
        await WriteCredentialsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ReadyTimeout;
        HttpRequestException? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response =
                    await _httpClient.GetAsync("System/Info/Public", cancellationToken)
                        .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Jellyfin did not become ready before the bootstrap timeout.",
            lastException);
    }

    private async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        return await TryAuthenticateAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                "The configured Jellyfin administrator credentials were rejected.");
    }

    private async Task<string?> TryAuthenticateAsync(
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation("Authorization", Authorization);
        request.Content = JsonContent.Create(new
        {
            Username = _options.AdminUserName,
            Pw = _options.AdminPassword,
        });
        using HttpResponseMessage response =
            await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using JsonDocument json =
            await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("AccessToken").GetString()
            ?? throw new InvalidDataException("Jellyfin returned an empty access token.");
    }

    private void SetAuthorizationToken(string accessToken)
    {
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"{Authorization}, Token=\"{accessToken}\"");
    }

    private async Task<IReadOnlyDictionary<string, Guid>> EnsureLibrariesAsync(
        CancellationToken cancellationToken)
    {
        foreach (LibrarySpec library in _libraries)
        {
            using JsonDocument folders =
                await GetJsonAsync("Library/VirtualFolders", cancellationToken)
                    .ConfigureAwait(false);
            JsonElement? existing = folders.RootElement
                .EnumerateArray()
                .Cast<JsonElement?>()
                .FirstOrDefault(folder =>
                    folder.HasValue &&
                    TryGetPropertyIgnoreCase(
                        folder.Value,
                        "Name",
                        out JsonElement name) &&
                    string.Equals(
                        name.GetString(),
                        library.Name,
                        StringComparison.Ordinal));
            if (existing.HasValue && LibraryMatches(existing.Value, library))
            {
                continue;
            }

            if (existing.HasValue)
            {
                using HttpResponseMessage remove = await _httpClient.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(library.Name)}" +
                    "&refreshLibrary=false",
                    cancellationToken).ConfigureAwait(false);
                remove.EnsureSuccessStatusCode();
            }

            string uri =
                $"Library/VirtualFolders?name={Uri.EscapeDataString(library.Name)}" +
                $"&collectionType={Uri.EscapeDataString(library.CollectionType)}" +
                $"&paths={Uri.EscapeDataString(library.Path)}&refreshLibrary=false";
            using HttpResponseMessage add = await _httpClient.PostAsJsonAsync(
                uri,
                new
                {
                    LibraryOptions = new
                    {
                        Enabled = true,
                        EnableInternetProviders = false,
                        EnableRealtimeMonitor = false,
                        EnablePhotos = false,
                        EnableLUFSScan = false,
                        EnableChapterImageExtraction = false,
                        ExtractChapterImagesDuringLibraryScan = false,
                        EnableTrickplayImageExtraction = false,
                        ExtractTrickplayImagesDuringLibraryScan = false,
                        EnableEmbeddedTitles = false,
                        EnableEmbeddedExtrasTitles = false,
                        EnableEmbeddedEpisodeInfos = false,
                        AutomaticRefreshIntervalDays = 0,
                        SaveLocalMetadata = false,
                        SaveSubtitlesWithMedia = false,
                        SubtitleDownloadLanguages = Array.Empty<string>(),
                        SubtitleFetcherOrder = Array.Empty<string>(),
                        DisabledSubtitleFetchers,
                        SaveTrickplayWithMedia = false,
                        MetadataSavers = Array.Empty<string>(),
                        DisabledLocalMetadataReaders =
                            _options.LibraryManifestPath is not null &&
                            library.ItemType == "Book"
                                ? DisabledEpubReaders
                                : Array.Empty<string>(),
                        TypeOptions = new[]
                        {
                            new
                            {
                                Type = library.ItemType,
                                MetadataFetcherOrder = Array.Empty<string>(),
                                MetadataFetchers = NoRemoteProviders,
                                ImageFetcherOrder = Array.Empty<string>(),
                                ImageFetchers = NoRemoteProviders,
                                ImageOptions = Array.Empty<object>(),
                                SimilarItemProviderOrder = SemanticProviderOrder,
                                SimilarItemProviders = Array.Empty<string>(),
                            },
                        },
                    },
                },
                cancellationToken).ConfigureAwait(false);
            add.EnsureSuccessStatusCode();
        }

        using JsonDocument reconciled =
            await GetJsonAsync("Library/VirtualFolders", cancellationToken)
                .ConfigureAwait(false);
        string[] actualLibraryNames = reconciled.RootElement
            .EnumerateArray()
            .Select(folder =>
                TryGetPropertyIgnoreCase(folder, "Name", out JsonElement name)
                    ? name.GetString()
                    : null)
            .Where(static name => name is not null)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedLibraryNames = _libraries
            .Select(static library => library.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualLibraryNames.SequenceEqual(
                expectedLibraryNames,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The demo data root must contain exactly the controlled profile libraries.");
        }

        Dictionary<string, Guid> ids = new(StringComparer.Ordinal);
        foreach (LibrarySpec library in _libraries)
        {
            JsonElement folder = reconciled.RootElement
                .EnumerateArray()
                .Single(candidate =>
                    TryGetPropertyIgnoreCase(
                        candidate,
                        "Name",
                        out JsonElement name) &&
                    string.Equals(
                        name.GetString(),
                        library.Name,
                        StringComparison.Ordinal));
            if (!LibraryMatches(folder, library) ||
                !TryGetPropertyIgnoreCase(folder, "ItemId", out JsonElement id) ||
                id.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(id.GetString(), out Guid itemId))
            {
                throw new InvalidDataException(
                    $"Library '{library.Name}' did not converge to its required type, path, and provider order.");
            }

            ids.Add(library.Name, itemId);
        }

        return ids;
    }

    private async Task WaitForItemsAsync(
        IReadOnlyDictionary<string, Guid> libraryIds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ReadyTimeout;
        List<string> missing = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            missing.Clear();
            foreach (LibrarySpec library in _libraries)
            {
                Guid parentId = libraryIds[library.Name];
                using JsonDocument items = await GetJsonAsync(
                    $"Items?ParentId={parentId:N}&Recursive=true" +
                    $"&IncludeItemTypes={Uri.EscapeDataString(string.Join(',', library.Items.Select(static item => item.Type).Distinct(StringComparer.Ordinal)))}" +
                    "&Fields=Path,Overview,Genres,Tags,Studios,OfficialRating&Limit=100",
                    cancellationToken).ConfigureAwait(false);
                JsonElement[] actualItems = items.RootElement.GetProperty("Items")
                    .EnumerateArray()
                    .ToArray();
                bool exact = actualItems.Length == library.Items.Length &&
                    library.Items.All(expected =>
                        actualItems.Count(item => ItemMatches(item, expected)) == 1) &&
                    actualItems.All(item =>
                        library.Items.Count(expected => ItemMatches(item, expected)) == 1);
                if (!exact)
                {
                    missing.Add(
                        $"{library.Name}: expected exactly {library.Items.Length} controlled leaves");
                }
            }

            if (missing.Count == 0)
            {
                Console.WriteLine("Jellyfin scan found every expected fixture item.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Jellyfin did not index the expected fixture items: {string.Join(", ", missing)}.");
    }

    private async Task RunLibraryScanAsync(CancellationToken cancellationToken)
    {
        string taskId = await GetScheduledTaskIdAsync(
            "RefreshLibrary",
            cancellationToken).ConfigureAwait(false);
        await WaitForTaskToStopRunningAsync(taskId, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument baseline = await GetJsonAsync(
            $"ScheduledTasks/{Uri.EscapeDataString(taskId)}",
            cancellationToken).ConfigureAwait(false);
        string baselineResult = GetExecutionResultMarker(baseline.RootElement);
        using HttpResponseMessage refresh =
            await _httpClient.PostAsync(
                "Library/Refresh",
                content: null,
                cancellationToken).ConfigureAwait(false);
        refresh.EnsureSuccessStatusCode();
        await WaitForNewCompletedTaskResultAsync(
            taskId,
            baselineResult,
            "Jellyfin library scan",
            cancellationToken).ConfigureAwait(false);
    }



    private async Task<long> RunSemanticIndexTaskAsync(
        CancellationToken cancellationToken)
    {
        string taskId = await GetScheduledTaskIdAsync(
            "SemanticDiscoveryRebuild",
            cancellationToken).ConfigureAwait(false);
        await WaitForTaskToStopRunningAsync(taskId, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument baselineTask = await GetJsonAsync(
            $"ScheduledTasks/{Uri.EscapeDataString(taskId)}",
            cancellationToken).ConfigureAwait(false);
        string baselineResult = GetExecutionResultMarker(baselineTask.RootElement);
        long baselineGeneration =
            await GetIndexGenerationAsync(cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage start = await _httpClient.PostAsync(
            $"ScheduledTasks/Running/{Uri.EscapeDataString(taskId)}",
            content: null,
            cancellationToken).ConfigureAwait(false);
        start.EnsureSuccessStatusCode();

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using JsonDocument task = await GetJsonAsync(
                $"ScheduledTasks/{Uri.EscapeDataString(taskId)}",
                cancellationToken).ConfigureAwait(false);
            string? state = TryGetPropertyIgnoreCase(
                task.RootElement,
                "State",
                out JsonElement stateProperty)
                ? stateProperty.GetString()
                : null;
            string currentResult = GetExecutionResultMarker(task.RootElement);
            string? resultStatus = GetExecutionResultStatus(task.RootElement);
            long currentGeneration =
                await GetIndexGenerationAsync(cancellationToken).ConfigureAwait(false);
            bool newResult = !string.Equals(
                currentResult,
                baselineResult,
                StringComparison.Ordinal);
            if (newResult &&
                resultStatus is "Failed" or "Cancelled" or "Canceled" or "Aborted")
            {
                throw new InvalidOperationException(
                    $"The Semantic Discovery index task ended with status '{resultStatus}'.");
            }

            if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase) &&
                newResult &&
                string.Equals(resultStatus, "Completed", StringComparison.Ordinal) &&
                currentGeneration > baselineGeneration)
            {
                Console.WriteLine(
                    $"Semantic Discovery index task completed generation {currentGeneration}.");
                return currentGeneration;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The Semantic Discovery index task did not finish.");
    }

    private async Task<string> GetScheduledTaskIdAsync(
        string keyValue,
        CancellationToken cancellationToken)
    {
        using JsonDocument tasks =
            await GetJsonAsync("ScheduledTasks", cancellationToken)
                .ConfigureAwait(false);
        JsonElement? task = tasks.RootElement
            .EnumerateArray()
            .Cast<JsonElement?>()
            .SingleOrDefault(candidate =>
                candidate.HasValue &&
                TryGetPropertyIgnoreCase(
                    candidate.Value,
                    "Key",
                    out JsonElement key) &&
                string.Equals(
                    key.GetString(),
                    keyValue,
                    StringComparison.Ordinal));
        if (!task.HasValue ||
            !TryGetPropertyIgnoreCase(
                task.Value,
                "Id",
                out JsonElement idProperty) ||
            string.IsNullOrWhiteSpace(idProperty.GetString()))
        {
            throw new InvalidDataException(
                $"Scheduled task '{keyValue}' was not registered.");
        }

        return idProperty.GetString()!;
    }

    private async Task WaitForNewCompletedTaskResultAsync(
        string taskId,
        string baselineResult,
        string description,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using JsonDocument task = await GetJsonAsync(
                $"ScheduledTasks/{Uri.EscapeDataString(taskId)}",
                cancellationToken).ConfigureAwait(false);
            string currentResult = GetExecutionResultMarker(task.RootElement);
            string? resultStatus = GetExecutionResultStatus(task.RootElement);
            string? state = TryGetPropertyIgnoreCase(
                task.RootElement,
                "State",
                out JsonElement stateProperty)
                ? stateProperty.GetString()
                : null;
            bool newResult = !string.Equals(
                currentResult,
                baselineResult,
                StringComparison.Ordinal);
            if (newResult &&
                resultStatus is "Failed" or "Cancelled" or "Canceled" or "Aborted")
            {
                throw new InvalidOperationException(
                    $"{description} ended with status '{resultStatus}'.");
            }

            bool active = state is not null &&
                (state.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                 state.Equals("Cancelling", StringComparison.OrdinalIgnoreCase));
            if (!active &&
                newResult &&
                string.Equals(
                    resultStatus,
                    "Completed",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException($"{description} did not complete.");
    }

    private async Task WaitForTaskToStopRunningAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.ReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using JsonDocument task = await GetJsonAsync(
                $"ScheduledTasks/{Uri.EscapeDataString(taskId)}",
                cancellationToken).ConfigureAwait(false);
            string? state = TryGetPropertyIgnoreCase(
                task.RootElement,
                "State",
                out JsonElement stateProperty)
                ? stateProperty.GetString()
                : null;
            if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(state, "Cancelling", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "A prior Semantic Discovery index task did not finish.");
    }

    private async Task VerifySemanticIndexStatusAsync(
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        using JsonDocument configuration = await GetJsonAsync(
            "Plugins/de965148-5789-4e8c-9774-6d1409ef3478/Configuration",
            cancellationToken).ConfigureAwait(false);
        if (!TryGetPropertyIgnoreCase(
                configuration.RootElement,
                "LastStatus",
                out JsonElement statusElement) ||
            statusElement.GetString() is not { } status ||
            !status.StartsWith("Ready:", StringComparison.Ordinal) ||
            !TryGetPropertyIgnoreCase(
                configuration.RootElement,
                "IndexGeneration",
                out JsonElement generation) ||
            !generation.TryGetInt64(out long actualGeneration) ||
            actualGeneration != expectedGeneration)
        {
            string error = TryGetPropertyIgnoreCase(
                configuration.RootElement,
                "LastError",
                out JsonElement errorElement)
                ? errorElement.GetString() ?? "unknown error"
                : "missing plugin status";
            throw new InvalidOperationException(
                $"Semantic Discovery index did not become ready: {statusElement}. {error}");
        }

        Console.WriteLine(status);
    }

    private async Task<long> GetIndexGenerationAsync(
        CancellationToken cancellationToken)
    {
        using JsonDocument configuration = await GetJsonAsync(
            "Plugins/de965148-5789-4e8c-9774-6d1409ef3478/Configuration",
            cancellationToken).ConfigureAwait(false);
        return TryGetPropertyIgnoreCase(
                configuration.RootElement,
                "IndexGeneration",
                out JsonElement generation) &&
            generation.TryGetInt64(out long value)
                ? value
                : 0;
    }

    private bool LibraryMatches(JsonElement folder, LibrarySpec expected)
    {
        if (!TryGetPropertyIgnoreCase(
                folder,
                "CollectionType",
                out JsonElement collectionType) ||
            !string.Equals(
                collectionType.GetString(),
                expected.CollectionType,
                StringComparison.OrdinalIgnoreCase) ||
            !TryGetPropertyIgnoreCase(folder, "Locations", out JsonElement locations) ||
            locations.ValueKind != JsonValueKind.Array ||
            locations.GetArrayLength() != 1 ||
            !string.Equals(
                locations.EnumerateArray().Single().GetString(),
                expected.Path,
                StringComparison.Ordinal) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "Enabled", expected: true) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableInternetProviders", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableRealtimeMonitor", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnablePhotos", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableLUFSScan", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableChapterImageExtraction", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "ExtractChapterImagesDuringLibraryScan", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableTrickplayImageExtraction", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "ExtractTrickplayImagesDuringLibraryScan", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableEmbeddedTitles", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableEmbeddedExtrasTitles", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "EnableEmbeddedEpisodeInfos", expected: false) ||
            !TryGetBoolean(options: folder, "LibraryOptions", "SaveLocalMetadata", expected: false))
        {
            return false;
        }

        if (!TryGetPropertyIgnoreCase(
                folder,
                "LibraryOptions",
                out JsonElement options) ||
            !TryGetPropertyIgnoreCase(options, "TypeOptions", out JsonElement typeOptions) ||
            typeOptions.ValueKind != JsonValueKind.Array ||
            typeOptions.GetArrayLength() != 1)
        {
            return false;
        }

        JsonElement[] matchingTypeOptions = typeOptions
            .EnumerateArray()
            .Where(typeOption =>
            TryGetPropertyIgnoreCase(typeOption, "Type", out JsonElement type) &&
            string.Equals(type.GetString(), expected.ItemType, StringComparison.Ordinal))
            .ToArray();
        if (matchingTypeOptions.Length != 1)
        {
            return false;
        }

        JsonElement matching = matchingTypeOptions[0];
        return HasEmptyArray(matching, "MetadataFetcherOrder") &&
            HasExactArray(
                matching,
                "MetadataFetchers",
                NoRemoteProviders) &&
            HasEmptyArray(matching, "ImageFetcherOrder") &&
            HasExactArray(
                matching,
                "ImageFetchers",
                NoRemoteProviders) &&
            HasEmptyArray(matching, "ImageOptions") &&
            TryGetPropertyIgnoreCase(
                matching,
                "SimilarItemProviderOrder",
                out JsonElement order) &&
            IsExactStringArray(order, SemanticProviderOrder) &&
            TryGetPropertyIgnoreCase(
                matching,
                "SimilarItemProviders",
                out JsonElement disabledProviders) &&
            IsExactStringArray(disabledProviders, []) &&
            TryGetPropertyIgnoreCase(options, "MetadataSavers", out JsonElement savers) &&
            IsExactStringArray(savers, []) &&
            TryGetPropertyIgnoreCase(
                options,
                "DisabledLocalMetadataReaders",
                out JsonElement disabledLocalReaders) &&
            IsExactStringArray(
                disabledLocalReaders,
                _options.LibraryManifestPath is not null &&
                expected.ItemType == "Book"
                    ? DisabledEpubReaders
                    : []) &&
            HasEmptyArray(options, "SubtitleDownloadLanguages") &&
            HasEmptyArray(options, "SubtitleFetcherOrder") &&
            HasExactArray(
                options,
                "DisabledSubtitleFetchers",
                DisabledSubtitleFetchers);
    }

    private static bool HasEmptyArray(
        JsonElement value,
        string property) =>
        TryGetPropertyIgnoreCase(value, property, out JsonElement array) &&
        array.ValueKind == JsonValueKind.Array &&
        array.GetArrayLength() == 0;

    private static bool HasExactArray(
        JsonElement value,
        string property,
        IReadOnlyList<string> expected) =>
        TryGetPropertyIgnoreCase(value, property, out JsonElement array) &&
        IsExactStringArray(array, expected);

    private static bool TryGetBoolean(
        JsonElement options,
        string optionsProperty,
        string property,
        bool expected)
    {
        return TryGetPropertyIgnoreCase(
                options,
                optionsProperty,
                out JsonElement nested) &&
            nested.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(nested, property, out JsonElement value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetBoolean() == expected;
    }

    private static bool IsExactStringArray(
        JsonElement value,
        IReadOnlyList<string> expected) =>
        value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() == expected.Count &&
        value.EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .SequenceEqual(expected, StringComparer.Ordinal);

    private static bool ItemMatches(JsonElement item, ExpectedItem expected) =>
        TryGetPropertyIgnoreCase(item, "Name", out JsonElement name) &&
        string.Equals(name.GetString(), expected.Title, StringComparison.Ordinal) &&
        TryGetPropertyIgnoreCase(item, "Type", out JsonElement type) &&
        string.Equals(type.GetString(), expected.Type, StringComparison.Ordinal) &&
        TryGetPropertyIgnoreCase(item, "Path", out JsonElement path) &&
        string.Equals(path.GetString(), expected.Path, StringComparison.Ordinal) &&
        MatchesOptionalString(item, "Overview", expected.Overview) &&
        MatchesOptionalArray(item, "Genres", expected.Genres) &&
        MatchesOptionalArray(item, "Tags", expected.Tags) &&
        MatchesOptionalArray(item, "Studios", expected.Studios) &&
        MatchesOptionalString(
            item,
            "OfficialRating",
            expected.OfficialRating);

    private static bool MatchesOptionalString(
        JsonElement item,
        string property,
        string? expected) =>
        expected is null ||
        (TryGetPropertyIgnoreCase(item, property, out JsonElement value) &&
         string.Equals(value.GetString(), expected, StringComparison.Ordinal));

    private static bool MatchesOptionalArray(
        JsonElement item,
        string property,
        string[]? expected) =>
        expected is null ||
        (TryGetPropertyIgnoreCase(item, property, out JsonElement value) &&
         value.ValueKind == JsonValueKind.Array &&
         value.EnumerateArray()
             .Select(static entry =>
                 entry.ValueKind == JsonValueKind.String
                     ? entry.GetString() ?? string.Empty
                     : entry.ValueKind == JsonValueKind.Object &&
                       TryGetPropertyIgnoreCase(
                           entry,
                           "Name",
                           out JsonElement name)
                         ? name.GetString() ?? string.Empty
                         : string.Empty)
             .Order(StringComparer.Ordinal)
             .SequenceEqual(
                 expected.Order(StringComparer.Ordinal),
                 StringComparer.Ordinal));

    private static string GetExecutionResultMarker(JsonElement task)
    {
        return TryGetPropertyIgnoreCase(
                task,
                "LastExecutionResult",
                out JsonElement result) &&
            result.ValueKind == JsonValueKind.Object
                ? result.GetRawText()
                : string.Empty;
    }

    private static string? GetExecutionResultStatus(JsonElement task)
    {
        if (TryGetPropertyIgnoreCase(
                task,
                "LastExecutionResult",
                out JsonElement result) &&
            result.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(result, "Status", out JsonElement status) &&
            status.ValueKind == JsonValueKind.String)
        {
            return status.GetString();
        }

        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostJsonAsync(
        string uri,
        object value,
        bool allowForbidden,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await _httpClient.PostAsJsonAsync(uri, value, cancellationToken)
                .ConfigureAwait(false);
        if (allowForbidden && response.StatusCode == HttpStatusCode.Forbidden)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream stream =
            await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static LibrarySpec[] LoadLibraries(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return SyntheticLibraries;
        }

        string path = Path.GetFullPath(manifestPath);
        ShowcaseLibraryManifest manifest =
            JsonSerializer.Deserialize<ShowcaseLibraryManifest>(
                File.ReadAllBytes(path),
                ManifestJsonOptions)
            ?? throw new InvalidDataException(
                "The showcase Jellyfin library manifest is empty.");
        if (manifest.ProfileId != "showcase-v1" ||
            manifest.Libraries is null ||
            manifest.Libraries.Length != 2)
        {
            throw new InvalidDataException(
                "The showcase Jellyfin library manifest is invalid.");
        }

        return manifest.Libraries.Select(static library =>
        {
            if (string.IsNullOrWhiteSpace(library.Name) ||
                library.CollectionType is not ("movies" or "books") ||
                library.ItemType is not ("Movie" or "Book") ||
                !library.Path.StartsWith("/media/", StringComparison.Ordinal) ||
                library.Items is null ||
                library.Items.Length != 3)
            {
                throw new InvalidDataException(
                    "A showcase Jellyfin library entry is invalid.");
            }

            return new LibrarySpec(
                library.Name,
                library.CollectionType,
                library.ItemType,
                library.Path,
                library.Items.Select(static item => new ExpectedItem(
                    item.Title,
                    item.Type,
                    item.Path,
                    item.Overview,
                    item.Genres,
                    item.Tags,
                    item.Studios,
                    item.OfficialRating)).ToArray());
        }).ToArray();
    }

    private async Task WriteCredentialsAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.CredentialsPath))
        {
            return;
        }

        string path = Path.GetFullPath(_options.CredentialsPath);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Url = _options.BaseAddress.AbsoluteUri,
            UserName = _options.AdminUserName,
            Password = _options.AdminPassword,
        });
        await WriteCredentialsFileAsync(path, content, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Jellyfin login written to {path}.");
    }

    internal static async Task WriteCredentialsFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new InvalidOperationException(
                "The credentials path must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            FileStreamOptions options = new()
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                BufferSize = 4096,
                Options =
                    FileOptions.Asynchronous |
                    FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode =
                    UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (FileStream stream = new(temporaryPath, options))
            {
                await stream.WriteAsync(content, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record LibrarySpec(
        string Name,
        string CollectionType,
        string ItemType,
        string Path,
        ExpectedItem[] Items);

    private sealed record ExpectedItem(
        string Title,
        string Type,
        string Path,
        string? Overview = null,
        string[]? Genres = null,
        string[]? Tags = null,
        string[]? Studios = null,
        string? OfficialRating = null);
}

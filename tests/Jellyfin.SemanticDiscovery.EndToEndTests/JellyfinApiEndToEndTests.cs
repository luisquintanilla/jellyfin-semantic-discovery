using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.SemanticDiscovery.EndToEndTests;

public sealed class JellyfinApiEndToEndTests
{
    private const string PluginConfigurationUri =
        "Plugins/de965148-5789-4e8c-9774-6d1409ef3478/Configuration";

    [EndToEndFact]
    public async Task NativeSearchFindsMetadataSubtitlesPdfEpubAndRejectsNoise()
    {
        await using DemoContext context = await DemoContext.CreateAsync();

        Assert.Contains(
            "The Signal Garden",
            await SearchNamesAsync(
                context.Admin,
                "an aurora cartographer maps silence between orbits"));
        Assert.Contains(
            "The Signal Garden",
            await SearchNamesAsync(
                context.Admin,
                "quiet cartouche hums twice before dawn"));
        Assert.Contains(
            "Field Notes on Static",
            await SearchNamesAsync(
                context.Admin,
                "marigold ledger returns before frost"));
        Assert.Contains(
            "Notes from the Choir Loft",
            await SearchNamesAsync(
                context.Admin,
                "seventeen lanterns gather at the loft door"));
        Assert.Contains(
            "Harbor Static",
            await SearchNamesAsync(context.Admin, "head back before dark"));
        Assert.Empty(await SearchNamesAsync(
            context.Admin,
            "quantum banana tractor"));

        string[] exactTitle =
            await SearchNamesAsync(context.Admin, "The Signal Garden");
        Assert.NotEmpty(exactTitle);
        Assert.Equal("The Signal Garden", exactTitle[0]);
    }

    [EndToEndFact]
    public async Task NativeSearchHonorsCombinedTypeMediaAndParentScopes()
    {
        await using DemoContext context = await DemoContext.CreateAsync();
        string movieLibraryId = context.LibraryIds["Semantic Movies"];
        string bookLibraryId = context.LibraryIds["Semantic Books"];

        Assert.Contains(
            "The Signal Garden",
            await SearchNamesAsync(
                context.Admin,
                "quiet cartouche hums twice before dawn",
                includeItemTypes: "Movie",
                mediaTypes: "Video",
                parentId: movieLibraryId));
        Assert.Empty(await SearchNamesAsync(
            context.Admin,
            "quiet cartouche hums twice before dawn",
            includeItemTypes: "Book"));
        Assert.Empty(await SearchNamesAsync(
            context.Admin,
            "quiet cartouche hums twice before dawn",
            includeItemTypes: "Movie",
            mediaTypes: "Book"));
        Assert.Empty(await SearchNamesAsync(
            context.Admin,
            "quiet cartouche hums twice before dawn",
            includeItemTypes: "Movie",
            mediaTypes: "Video",
            parentId: bookLibraryId));
        Assert.Empty(await SearchNamesAsync(
            context.Admin,
            "quiet cartouche hums twice before dawn",
            includeItemTypes: "Movie",
            excludeItemTypes: "Movie"));
    }

    [EndToEndFact]
    public async Task NativeSearchAndRecommendationsFailClosedForRestrictedUsers()
    {
        await using DemoContext context = await DemoContext.CreateAsync();
        List<string> createdUserIds = [];
        List<Exception> cleanupFailures = [];
        Exception? primaryFailure = null;
        try
        {
            CreatedUser hiddenLibraryUser = await CreateUserAsync(
                context,
                createdUserIds,
                "semantic-hidden-library",
                policy =>
                {
                    policy["EnableAllFolders"] = false;
                    policy["EnabledFolders"] =
                        new JsonArray(context.LibraryIds["Semantic Books"]);
                });
            using HttpClient hiddenLibraryClient = hiddenLibraryUser.Client;
            string[] hiddenImplicit = await SearchNamesAsync(
                hiddenLibraryUser.Client,
                "quiet cartouche hums twice before dawn");
            string[] hiddenExplicit = await SearchNamesAsync(
                hiddenLibraryUser.Client,
                "quiet cartouche hums twice before dawn",
                userId: hiddenLibraryUser.Id);
            Assert.Equal(hiddenImplicit, hiddenExplicit);
            Assert.DoesNotContain("The Signal Garden", hiddenImplicit);
            Assert.Contains(
                "Field Notes on Static",
                await SearchNamesAsync(
                    hiddenLibraryUser.Client,
                    "marigold ledger returns before frost"));
            using HttpResponseMessage overrideAttempt =
                await hiddenLibraryUser.Client.GetAsync(
                    $"Search/Hints?SearchTerm=test&UserId={context.AdminUserId}");
            Assert.Equal(HttpStatusCode.Forbidden, overrideAttempt.StatusCode);

            CreatedUser blockedTagUser = await CreateUserAsync(
                context,
                createdUserIds,
                "semantic-blocked-tag",
                policy =>
                {
                    policy["EnableAllFolders"] = true;
                    policy["EnabledFolders"] = new JsonArray();
                    policy["BlockedTags"] =
                        new JsonArray("restricted-semantic");
                });
            using HttpClient blockedTagClient = blockedTagUser.Client;
            Assert.Equal(
                await SearchNamesAsync(
                    blockedTagUser.Client,
                    "The Signal Garden"),
                await SearchNamesAsync(
                    blockedTagUser.Client,
                    "The Signal Garden",
                    userId: blockedTagUser.Id));
            Assert.DoesNotContain(
                "The Signal Garden",
                await SearchNamesAsync(
                    blockedTagUser.Client,
                    "The Signal Garden"));
            string signalId = GetRequiredString(
                context.Items["The Signal Garden"],
                "Id");
            string orbitId = GetRequiredString(
                context.Items["Orbit Almanac"],
                "Id");
            string[] blockedSimilar = await GetSimilarIdsAsync(
                blockedTagUser.Client,
                $"Items/{orbitId}/Similar?Limit=10",
                "Movie");
            Assert.DoesNotContain(
                signalId,
                blockedSimilar,
                StringComparer.OrdinalIgnoreCase);

            CreatedUser parentalUser = await CreateUserAsync(
                context,
                createdUserIds,
                "semantic-parental",
                policy =>
                {
                    policy["EnableAllFolders"] = true;
                    policy["EnabledFolders"] = new JsonArray();
                    policy["MaxParentalRating"] = 1;
                });
            using HttpClient parentalClient = parentalUser.Client;
            Assert.Equal(
                await SearchNamesAsync(
                    parentalUser.Client,
                    "The Signal Garden"),
                await SearchNamesAsync(
                    parentalUser.Client,
                    "The Signal Garden",
                    userId: parentalUser.Id));
            Assert.DoesNotContain(
                "The Signal Garden",
                await SearchNamesAsync(
                    parentalUser.Client,
                    "The Signal Garden"));

            CreatedUser allowedTagUser = await CreateUserAsync(
                context,
                createdUserIds,
                "semantic-allowed-tag",
                policy =>
                {
                    policy["EnableAllFolders"] = true;
                    policy["EnabledFolders"] = new JsonArray();
                    policy["AllowedTags"] = new JsonArray("some-other-tag");
                    policy["BlockedTags"] = new JsonArray();
                });
            using HttpClient allowedTagClient = allowedTagUser.Client;
            Assert.Equal(
                await SearchNamesAsync(
                    allowedTagUser.Client,
                    "The Signal Garden"),
                await SearchNamesAsync(
                    allowedTagUser.Client,
                    "The Signal Garden",
                    userId: allowedTagUser.Id));
            Assert.DoesNotContain(
                "The Signal Garden",
                await SearchNamesAsync(
                    allowedTagUser.Client,
                    "The Signal Garden"));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            foreach (string userId in createdUserIds.AsEnumerable().Reverse())
            {
                try
                {
                    using HttpResponseMessage delete =
                        await context.Admin.DeleteAsync($"Users/{userId}");
                    delete.EnsureSuccessStatusCode();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                if (primaryFailure is not null)
                {
                    primaryFailure.Data["TemporaryUserCleanupFailures"] =
                        cleanupFailures.Count;
                }
            }
        }

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "One or more temporary Jellyfin users could not be deleted.",
                cleanupFailures);
        }
    }

    [EndToEndFact]
    public async Task SemanticProviderFillsLimitOneBeforeBuiltInsForMovieSeriesAndBook()
    {
        await using DemoContext context = await DemoContext.CreateAsync();
        JsonObject before = await GetObjectAsync(
            context.Admin,
            PluginConfigurationUri);
        long initialCount = GetRequiredInt64(before, "RecommendationQueryCount");
        long initialResultCount =
            GetRequiredInt64(before, "RecommendationResultCount");
        (string Source, string Expected, string Type)[] pairs =
        [
            ("The Signal Garden", "Orbit Almanac", "Movie"),
            ("Harbor Static", "Tidal Register", "Series"),
            ("Field Notes on Static", "Notes from the Choir Loft", "Book"),
        ];

        foreach ((string sourceName, string expectedName, string type) in pairs)
        {
            string sourceId =
                GetRequiredString(context.Items[sourceName], "Id");
            string expectedId =
                GetRequiredString(context.Items[expectedName], "Id");
            string[] implicitUser = await GetSimilarIdsAsync(
                context.Admin,
                $"Items/{sourceId}/Similar?Limit=1",
                type);
            string[] explicitUser = await GetSimilarIdsAsync(
                context.Admin,
                $"Items/{sourceId}/Similar?UserId={context.AdminUserId}&Limit=1",
                type);
            Assert.Equal([expectedId], implicitUser);
            Assert.Equal(implicitUser, explicitUser);
            Assert.DoesNotContain(
                sourceId,
                implicitUser,
                StringComparer.OrdinalIgnoreCase);
        }

        JsonObject after = await GetObjectAsync(
            context.Admin,
            PluginConfigurationUri);
        Assert.True(
            GetRequiredInt64(after, "RecommendationQueryCount") >=
            initialCount + (pairs.Length * 2));
        Assert.True(
            GetRequiredInt64(after, "RecommendationResultCount") >=
            initialResultCount + (pairs.Length * 2));

        long countBeforeDisabled =
            GetRequiredInt64(after, "RecommendationQueryCount");
        long resultsBeforeDisabled =
            GetRequiredInt64(after, "RecommendationResultCount");
        try
        {
            await SetPluginEnabledAsync(context.Admin, after, enabled: false);
            foreach ((string sourceName, string expectedName, string type) in pairs)
            {
                string sourceId =
                    GetRequiredString(context.Items[sourceName], "Id");
                string expectedId =
                    GetRequiredString(context.Items[expectedName], "Id");
                Assert.DoesNotContain(
                    expectedId,
                    await GetSimilarIdsAsync(
                        context.Admin,
                        $"Items/{sourceId}/Similar?Limit=1",
                        type),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await SetPluginEnabledAsync(context.Admin, after, enabled: true);
        }
        JsonObject afterDisabled = await GetObjectAsync(
            context.Admin,
            PluginConfigurationUri);
        Assert.Equal(
            countBeforeDisabled,
            GetRequiredInt64(afterDisabled, "RecommendationQueryCount"));
        Assert.Equal(
            resultsBeforeDisabled,
            GetRequiredInt64(afterDisabled, "RecommendationResultCount"));
    }

    [EndToEndFact]
    public async Task RuntimeEvidenceIsPersistedForRestartComparison()
    {
        string evidencePath =
            Environment.GetEnvironmentVariable("JELLYFIN_E2E_EVIDENCE_PATH")
            ?? throw new InvalidOperationException(
                "JELLYFIN_E2E_EVIDENCE_PATH is required.");
        await using DemoContext context = await DemoContext.CreateAsync();
        JsonObject configuration = await GetObjectAsync(
            context.Admin,
            PluginConfigurationUri);
        JsonObject evidence = new()
        {
            ["indexGeneration"] =
                GetRequiredInt64(configuration, "IndexGeneration"),
            ["metadataRecordCount"] =
                GetRequiredInt64(configuration, "MetadataRecordCount"),
            ["contentChunkCount"] =
                GetRequiredInt64(configuration, "ContentChunkCount"),
            ["lastEmbeddedMetadataCount"] =
                GetRequiredInt64(configuration, "LastEmbeddedMetadataCount"),
            ["lastEmbeddedContentChunkCount"] =
                GetRequiredInt64(configuration, "LastEmbeddedContentChunkCount"),
            ["recommendationQueryCount"] =
                GetRequiredInt64(configuration, "RecommendationQueryCount"),
            ["recommendationResultCount"] =
                GetRequiredInt64(configuration, "RecommendationResultCount"),
            ["ingestEmbeddingsTotal"] =
                GetRequiredInt64(configuration, "IngestEmbeddingsTotal"),
            ["queryEmbeddingsTotal"] =
                GetRequiredInt64(configuration, "QueryEmbeddingsTotal"),
            ["indexIdentity"] =
                GetRequiredString(configuration, "IndexIdentity"),
        };
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
        await File.WriteAllTextAsync(
            evidencePath,
            evidence.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
    }

    private static async Task SetPluginEnabledAsync(
        HttpClient client,
        JsonObject configuration,
        bool enabled)
    {
        configuration["Enabled"] = enabled;
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            PluginConfigurationUri,
            configuration);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<CreatedUser> CreateUserAsync(
        DemoContext context,
        List<string> createdUserIds,
        string prefix,
        Action<JsonObject> configurePolicy)
    {
        string name = $"{prefix}-{Guid.NewGuid():N}";
        string password = $"access-{Guid.NewGuid():N}";
        using HttpResponseMessage create = await context.Admin.PostAsJsonAsync(
            "Users/New",
            new { Name = name, Password = password });
        create.EnsureSuccessStatusCode();
        JsonObject user = (await JsonNode.ParseAsync(
            await create.Content.ReadAsStreamAsync()))!.AsObject();
        string userId = GetRequiredString(user, "Id");
        createdUserIds.Add(userId);

        JsonObject policy = user["Policy"]!.AsObject();
        policy["IsAdministrator"] = false;
        policy["IsHidden"] = false;
        policy["IsDisabled"] = false;
        policy["BlockedTags"] ??= new JsonArray();
        policy["AllowedTags"] ??= new JsonArray();
        configurePolicy(policy);
        using HttpResponseMessage update = await context.Admin.PostAsJsonAsync(
            $"Users/{userId}/Policy",
            policy);
        update.EnsureSuccessStatusCode();

        return new CreatedUser(
            await AuthenticateAsync(context.BaseAddress, name, password),
            userId);
    }

    private static async Task<string[]> GetSimilarIdsAsync(
        HttpClient client,
        string uri,
        string expectedType)
    {
        JsonObject result = await GetObjectAsync(client, uri);
        return result["Items"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Where(item => string.Equals(
                GetRequiredString(item, "Type"),
                expectedType,
                StringComparison.Ordinal))
            .Select(item => GetRequiredString(item, "Id"))
            .ToArray();
    }

    private static async Task<HttpClient> AuthenticateAsync(
        Uri baseAddress,
        string userName,
        string password)
    {
        using HttpClient anonymous = new() { BaseAddress = baseAddress };
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "MediaBrowser Client=\"Semantic Discovery E2E\", " +
            "Device=\"CI\", DeviceId=\"semantic-e2e\", Version=\"0.1.0\"");
        request.Content = JsonContent.Create(new
        {
            Username = userName,
            Pw = password,
        });
        using HttpResponseMessage response =
            await anonymous.SendAsync(request);
        response.EnsureSuccessStatusCode();
        JsonObject payload = (await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync()))!.AsObject();
        string token = GetRequiredString(payload, "AccessToken");

        HttpClient authenticated = new() { BaseAddress = baseAddress };
        authenticated.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            "MediaBrowser Client=\"Semantic Discovery E2E\", " +
            "Device=\"CI\", DeviceId=\"semantic-e2e\", Version=\"0.1.0\", " +
            $"Token=\"{token}\"");
        return authenticated;
    }

    private static async Task<Dictionary<string, JsonObject>> GetIndexedItemsAsync(
        HttpClient client)
    {
        JsonObject response = await GetObjectAsync(
            client,
            "Items?Recursive=true&IncludeItemTypes=Movie,Series,Book" +
            "&Fields=Path,Overview,Genres,Tags,Studios,OfficialRating&Limit=100");
        return response["Items"]!.AsArray()
            .Select(static item => item!.AsObject())
            .ToDictionary(
                item => GetRequiredString(item, "Name"),
                StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, string>> GetLibraryIdsAsync(
        HttpClient client)
    {
        JsonArray folders = await GetArrayAsync(
            client,
            "Library/VirtualFolders");
        return folders
            .Select(static folder => folder!.AsObject())
            .ToDictionary(
                folder => GetRequiredString(folder, "Name"),
                folder => GetRequiredString(folder, "ItemId"),
                StringComparer.Ordinal);
    }

    private static async Task<string[]> SearchNamesAsync(
        HttpClient client,
        string term,
        string? includeItemTypes = null,
        string? excludeItemTypes = null,
        string? mediaTypes = null,
        string? parentId = null,
        string? userId = null)
    {
        List<string> query =
        [
            $"SearchTerm={Uri.EscapeDataString(term)}",
            "Limit=20",
        ];
        AddQuery(query, "IncludeItemTypes", includeItemTypes);
        AddQuery(query, "ExcludeItemTypes", excludeItemTypes);
        AddQuery(query, "MediaTypes", mediaTypes);
        AddQuery(query, "ParentId", parentId);
        AddQuery(query, "UserId", userId);

        JsonObject response = await GetObjectAsync(
            client,
            $"Search/Hints?{string.Join('&', query)}");
        return response["SearchHints"]!.AsArray()
            .Select(static hint => GetRequiredString(
                hint!.AsObject(),
                "Name"))
            .ToArray();
    }

    private static void AddQuery(
        List<string> query,
        string name,
        string? value)
    {
        if (value is not null)
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static async Task<JsonObject> GetObjectAsync(
        HttpClient client,
        string uri)
    {
        using HttpResponseMessage response = await client.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync()))!.AsObject();
    }

    private static async Task<JsonArray> GetArrayAsync(
        HttpClient client,
        string uri)
    {
        using HttpResponseMessage response = await client.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync()))!.AsArray();
    }

    private static string GetRequiredString(
        JsonObject value,
        string property) =>
        value[property]?.GetValue<string>()
        ?? throw new InvalidDataException($"Response omitted '{property}'.");

    private static long GetRequiredInt64(
        JsonObject value,
        string property) =>
        value[property]?.GetValue<long>()
        ?? throw new InvalidDataException($"Response omitted '{property}'.");

    private sealed record CreatedUser(HttpClient Client, string Id);

    private sealed class DemoContext : IAsyncDisposable
    {
        private DemoContext(
            Uri baseAddress,
            HttpClient admin,
            string adminUserId,
            Dictionary<string, JsonObject> items,
            Dictionary<string, string> libraryIds)
        {
            BaseAddress = baseAddress;
            Admin = admin;
            AdminUserId = adminUserId;
            Items = items;
            LibraryIds = libraryIds;
        }

        public Uri BaseAddress { get; }

        public HttpClient Admin { get; }

        public string AdminUserId { get; }

        public Dictionary<string, JsonObject> Items { get; }

        public Dictionary<string, string> LibraryIds { get; }

        public static async Task<DemoContext> CreateAsync()
        {
            string path =
                Environment.GetEnvironmentVariable(
                    "JELLYFIN_E2E_CREDENTIALS_PATH")
                ?? throw new InvalidOperationException(
                    "JELLYFIN_E2E_CREDENTIALS_PATH is required.");
            await using FileStream stream = File.OpenRead(path);
            JsonObject login = (await JsonNode.ParseAsync(stream))!.AsObject();
            Uri baseAddress = new(GetRequiredString(login, "Url"));
            HttpClient admin = await AuthenticateAsync(
                baseAddress,
                GetRequiredString(login, "UserName"),
                GetRequiredString(login, "Password"));
            JsonObject adminUser = await GetObjectAsync(admin, "Users/Me");
            return new DemoContext(
                baseAddress,
                admin,
                GetRequiredString(adminUser, "Id"),
                await GetIndexedItemsAsync(admin),
                await GetLibraryIdsAsync(admin));
        }

        public ValueTask DisposeAsync()
        {
            Admin.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class EndToEndFactAttribute : FactAttribute
{
    public EndToEndFactAttribute()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("JELLYFIN_E2E_PROFILE"),
                "showcase",
                StringComparison.Ordinal))
        {
            Skip = "Synthetic baseline tests do not run against showcase data.";
        }
        else if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "JELLYFIN_E2E_CREDENTIALS_PATH")))
        {
            Skip =
                "JELLYFIN_E2E_CREDENTIALS_PATH is set by scripts/Test-EndToEnd.ps1.";
        }
    }
}

using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Jellyfin.SemanticDiscovery.EndToEndTests;

public sealed class ShowcaseEndToEndTests
{
    [ShowcaseEndToEndFact]
    public async Task ExactSixItemsUseOnlyLocalLockedMetadataAndNoArtwork()
    {
        await using ShowcaseContext context = await ShowcaseContext.CreateAsync();
        JsonObject response = await GetObjectAsync(
            context.Admin,
            "Items?Recursive=true&IncludeItemTypes=Movie,Book" +
            "&Fields=Path,Overview,Genres,Studios,ImageTags&Limit=20");
        JsonObject[] items = response["Items"]!.AsArray()
            .Select(static value => value!.AsObject())
            .ToArray();

        Assert.Equal(6, items.Length);
        Assert.Equal(
            [
                "AI for Teachers: an Open Textbook",
                "Coffee Run",
                "Introduction to Digital Humanism: A Textbook",
                "Sintel",
                "Spring",
                "The Adventures of Sherlock Holmes",
            ],
            items.Select(static item => Required(item, "Name"))
                .Order(StringComparer.Ordinal));
        Assert.Contains(
            items,
            item => Required(item, "Path").EndsWith(
                ".mp4",
                StringComparison.Ordinal));
        Assert.Contains(
            items,
            item => Required(item, "Path").EndsWith(
                ".webm",
                StringComparison.Ordinal));
        Assert.Contains(
            items,
            item => Required(item, "Path").EndsWith(
                ".epub",
                StringComparison.Ordinal));
        Assert.Equal(
            2,
            items.Count(item => Required(item, "Path").EndsWith(
                ".epub",
                StringComparison.Ordinal)));
        Assert.Contains(
            items,
            item => Required(item, "Path").EndsWith(
                ".pdf",
                StringComparison.Ordinal));
        Assert.All(
            items,
            item =>
            {
                Assert.Empty(item["ImageTags"]?.AsObject() ?? []);
                Assert.Empty(item["BackdropImageTags"]?.AsArray() ?? []);
            });
    }

    [ShowcaseEndToEndFact]
    public async Task LibrariesDisableRemoteMetadataArtworkAndLocalImageGeneration()
    {
        await using ShowcaseContext context = await ShowcaseContext.CreateAsync();
        using HttpResponseMessage response =
            await context.Admin.GetAsync("Library/VirtualFolders");
        response.EnsureSuccessStatusCode();
        JsonArray folders = (await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync()))!.AsArray();
        Assert.Equal(2, folders.Count);
        foreach (JsonObject folder in folders.Select(
                     static value => value!.AsObject()))
        {
            JsonObject options = folder["LibraryOptions"]!.AsObject();
            Assert.False(options["EnableRealtimeMonitor"]!.GetValue<bool>());
            Assert.False(options["EnablePhotos"]!.GetValue<bool>());
            Assert.False(options["EnableChapterImageExtraction"]!.GetValue<bool>());
            Assert.False(options["ExtractChapterImagesDuringLibraryScan"]!.GetValue<bool>());
            Assert.False(options["EnableTrickplayImageExtraction"]!.GetValue<bool>());
            Assert.False(options["ExtractTrickplayImagesDuringLibraryScan"]!.GetValue<bool>());
            Assert.Empty(options["SubtitleDownloadLanguages"]!.AsArray());
            Assert.Empty(options["SubtitleFetcherOrder"]!.AsArray());
            Assert.Equal(
                ["Open Subtitles", "OpenSubtitles"],
                options["DisabledSubtitleFetchers"]!.AsArray()
                    .Select(static value => value!.GetValue<string>()));
            JsonObject type = Assert.Single(
                options["TypeOptions"]!.AsArray())!.AsObject();
            Assert.Equal(
                ["Semantic Discovery: local sidecars only"],
                type["MetadataFetchers"]!.AsArray()
                    .Select(static value => value!.GetValue<string>()));
            Assert.Equal(
                ["Semantic Discovery: local sidecars only"],
                type["ImageFetchers"]!.AsArray()
                    .Select(static value => value!.GetValue<string>()));
            Assert.Empty(type["ImageOptions"]!.AsArray());
        }
    }

    [ShowcaseEndToEndFact]
    public async Task SourceDerivedQueriesRankIntendedOwnersFirstAndRejectNoise()
    {
        await using ShowcaseContext context = await ShowcaseContext.CreateAsync();
        (string Query, string Title, string Type)[] contract =
        [
            ("a dangerous journey to find a dragon", "Sintel", "Movie"),
            (
                "a shepherd girl and her dog confront ancient spirits",
                "Spring",
                "Movie"),
            (
                "caffeine and bittersweet memories of a past relationship",
                "Coffee Run",
                "Movie"),
            (
                "How do search engines understand meaning rather than exact words?",
                "AI for Teachers: an Open Textbook",
                "Book"),
            (
                "How should people shape our collective digital future?",
                "Introduction to Digital Humanism: A Textbook",
                "Book"),
            (
                "the detective distinguishes seeing from observing",
                "The Adventures of Sherlock Holmes",
                "Book"),
        ];
        JsonArray evidence = [];
        foreach ((string query, string title, string type) in contract)
        {
            JsonObject[] first = await SearchAsync(context.Admin, query);
            JsonObject[] repeated = await SearchAsync(context.Admin, query);

            JsonObject top = Assert.IsType<JsonObject>(
                Assert.Single(first.Take(1)));
            Assert.Equal(title, Required(top, "Name"));
            Assert.Equal(type, Required(top, "Type"));
            Assert.Equal(
                first.Select(static item => Required(item, "Id")),
                repeated.Select(static item => Required(item, "Id")));
            evidence.Add(new JsonObject
            {
                ["query"] = query,
                ["intendedTitle"] = title,
                ["intendedType"] = type,
                ["results"] = SanitizeItems(first),
            });
        }

        const string negativeQuery =
            "quantum banana volcano choreography";
        JsonObject[] negative =
            await SearchAsync(context.Admin, negativeQuery);
        Assert.Empty(negative);
        await WriteEvidenceAsync(
            "SHOWCASE_E2E_NATIVE_SEARCH_EVIDENCE",
            new JsonObject
            {
                ["schemaVersion"] = 2,
                ["generatorId"] =
                    "showcase-native-search-evidence-v3",
                ["evidenceKind"] = "native-jellyfin-search",
                ["profileId"] = "showcase-v1",
                ["source"] = "jellyfin-native-search-hints-api",
                ["accessControlBoundary"] =
                    "Showcase uses its generated administrator only; synthetic E2E remains the access-control oracle.",
                ["queries"] = evidence,
                ["negativeControl"] = new JsonObject
                {
                    ["query"] = negativeQuery,
                    ["resultCount"] = negative.Length,
                },
            });
    }

    [ShowcaseEndToEndFact]
    public async Task AllSixRecommendationDirectionsRecordSemanticFloorAndNativeFallbacks()
    {
        await using ShowcaseContext context = await ShowcaseContext.CreateAsync();
        Dictionary<string, JsonObject> items =
            await GetItemsByNameAsync(context.Admin);
        (
            string Source,
            string? SemanticExpected,
            string? NativeExpected,
            string Boundary)[] contract =
        [
            ("Spring", "Coffee Run", "Coffee Run", "semantic"),
            ("Coffee Run", "Spring", "Spring", "semantic"),
            (
                "AI for Teachers: an Open Textbook",
                "Introduction to Digital Humanism: A Textbook",
                "Introduction to Digital Humanism: A Textbook",
                "semantic"),
            (
                "Introduction to Digital Humanism: A Textbook",
                "AI for Teachers: an Open Textbook",
                "AI for Teachers: an Open Textbook",
                "semantic"),
            (
                "Sintel",
                null,
                "Spring",
                "Jellyfin 12 always invokes the built-in Local Genre/Tag provider after an empty local semantic provider result."),
            (
                "The Adventures of Sherlock Holmes",
                null,
                null,
                "semantic-floor-zero"),
        ];
        JsonArray evidence = [];
        foreach ((
                     string sourceName,
                     string? semanticExpectedName,
                     string? nativeExpectedName,
                     string boundary) in contract)
        {
            JsonObject source = items[sourceName];
            JsonObject[] first = await SimilarAsync(
                context.Admin,
                Required(source, "Id"));
            JsonObject[] repeated = await SimilarAsync(
                context.Admin,
                Required(source, "Id"));
            Assert.Equal(
                first.Select(static item => Required(item, "Id")),
                repeated.Select(static item => Required(item, "Id")));
            if (nativeExpectedName is null)
            {
                Assert.True(
                    first.Length == 0,
                    $"{sourceName} unexpectedly returned: " +
                    string.Join(
                        ", ",
                        first.Select(static item => Required(item, "Name"))));
            }
            else
            {
                JsonObject result = Assert.Single(first);
                Assert.Equal(
                    Required(items[nativeExpectedName], "Id"),
                    Required(result, "Id"));
                Assert.NotEqual(
                    Required(source, "Id"),
                    Required(result, "Id"));
            }
            JsonObject configuration = await GetObjectAsync(
                context.Admin,
                "Plugins/de965148-5789-4e8c-9774-6d1409ef3478/Configuration");
            Assert.Equal(
                Required(source, "Id").Replace("-", string.Empty),
                Required(
                    configuration,
                    "LastRecommendationSourceItemId"));
            JsonArray semanticResults = JsonNode.Parse(
                Required(
                    configuration,
                    "LastRecommendationResultsJson"))!.AsArray();
            float effectiveFloor = configuration[
                "LastRecommendationEffectiveFloor"]!.GetValue<float>();
            if (semanticExpectedName is null)
            {
                Assert.Empty(semanticResults);
            }
            else
            {
                JsonObject semanticResult =
                    Assert.Single(semanticResults)!.AsObject();
                Assert.Equal(
                    Required(items[semanticExpectedName], "Id")
                        .Replace("-", string.Empty),
                    Required(semanticResult, "itemId"));
                Assert.True(
                    semanticResult["score"]!.GetValue<float>() >
                    effectiveFloor);
            }

            evidence.Add(new JsonObject
            {
                ["sourceTitle"] = sourceName,
                ["sourceId"] = Required(source, "Id"),
                ["semanticExpectedTitle"] = semanticExpectedName,
                ["nativeExpectedTitle"] = nativeExpectedName,
                ["providerBoundary"] = boundary,
                ["semanticRuntimeEffectiveFloor"] =
                    effectiveFloor,
                ["semanticRuntimeResults"] =
                    semanticResults.DeepClone(),
                ["results"] = SanitizeItems(first),
            });
        }

        await WriteEvidenceAsync(
            "SHOWCASE_E2E_NATIVE_SIMILAR_EVIDENCE",
            new JsonObject
            {
                ["schemaVersion"] = 2,
                ["generatorId"] =
                    "showcase-native-similar-evidence-v3",
                ["evidenceKind"] = "native-jellyfin-similar",
                ["profileId"] = "showcase-v1",
                ["source"] = "jellyfin-native-similar-items-api",
                ["strictFloorComparison"] = ">",
                ["providerAttributionExposedByApi"] = false,
                ["knownJellyfinBoundary"] =
                    "Jellyfin 12 invokes every local similar-items provider; an empty Semantic Discovery Movie result can fall through to Local Genre/Tag.",
                ["directions"] = evidence,
            });
    }

    [ShowcaseEndToEndFact]
    public async Task RuntimeCountersAreWrittenAsSanitizedEvidence()
    {
        string path =
            Environment.GetEnvironmentVariable(
                "SHOWCASE_E2E_RUNTIME_EVIDENCE")
            ?? throw new InvalidOperationException(
                "SHOWCASE_E2E_RUNTIME_EVIDENCE is required.");
        await using ShowcaseContext context = await ShowcaseContext.CreateAsync();
        JsonObject configuration = await GetObjectAsync(
            context.Admin,
            "Plugins/de965148-5789-4e8c-9774-6d1409ef3478/Configuration");
        float expectedFloor = float.Parse(
            Environment.GetEnvironmentVariable(
                "SHOWCASE_E2E_EXPECTED_RECOMMENDATION_FLOOR")
            ?? throw new InvalidOperationException(
                "SHOWCASE_E2E_EXPECTED_RECOMMENDATION_FLOOR is required."),
            CultureInfo.InvariantCulture);
        float effectiveFloor = configuration[
            "EffectiveRecommendationFloor"]?.GetValue<float>()
            ?? throw new InvalidDataException(
                "Response omitted 'EffectiveRecommendationFloor'.");
        Assert.Equal(expectedFloor, effectiveFloor, precision: 6);
        JsonObject evidence = new()
        {
            ["schemaVersion"] = 3,
            ["generatorId"] = "showcase-runtime-evidence-v3",
            ["evidenceKind"] = "showcase-runtime-status",
            ["indexGeneration"] =
                RequiredInt64(configuration, "IndexGeneration"),
            ["metadataRecordCount"] =
                RequiredInt64(configuration, "MetadataRecordCount"),
            ["contentChunkCount"] =
                RequiredInt64(configuration, "ContentChunkCount"),
            ["lastEmbeddedMetadataCount"] =
                RequiredInt64(configuration, "LastEmbeddedMetadataCount"),
            ["lastEmbeddedContentChunkCount"] =
                RequiredInt64(configuration, "LastEmbeddedContentChunkCount"),
            ["ingestEmbeddingsTotal"] =
                RequiredInt64(configuration, "IngestEmbeddingsTotal"),
            ["queryEmbeddingsTotal"] =
                RequiredInt64(configuration, "QueryEmbeddingsTotal"),
            ["indexIdentity"] = Required(configuration, "IndexIdentity"),
            ["effectiveRecommendationFloor"] = effectiveFloor,
            ["appHostExpectedRecommendationFloor"] = expectedFloor,
            ["clientMemory"] = new JsonObject
            {
                ["process"] = "jellyfin-dotnet",
                ["units"] = "bytes",
                ["metricKind"] = "process-lifetime-high-water",
                ["currentWorkingSetBytes"] =
                    RequiredInt64(
                        configuration,
                        "ClientProcessCurrentWorkingSetBytes"),
                ["peakWorkingSetBytes"] =
                    RequiredInt64(
                        configuration,
                        "ClientProcessPeakWorkingSetBytes"),
                ["largestConversionRequest"] = new JsonObject
                {
                    ["sourceBytes"] = RequiredInt64(
                        configuration,
                        "LargestConversionSourceBytes"),
                    ["serializedRequestBytes"] = RequiredInt64(
                        configuration,
                        "LargestSerializedRequestBytes"),
                    ["beforeCurrentWorkingSetBytes"] = RequiredInt64(
                        configuration,
                        "LargestRequestBeforeCurrentWorkingSetBytes"),
                    ["beforePeakWorkingSetBytes"] = RequiredInt64(
                        configuration,
                        "LargestRequestBeforePeakWorkingSetBytes"),
                    ["afterCurrentWorkingSetBytes"] = RequiredInt64(
                        configuration,
                        "LargestRequestAfterCurrentWorkingSetBytes"),
                    ["afterPeakWorkingSetBytes"] = RequiredInt64(
                        configuration,
                        "LargestRequestAfterPeakWorkingSetBytes"),
                },
            },
        };
        Assert.True(
            evidence["clientMemory"]!["currentWorkingSetBytes"]!
                .GetValue<long>() > 0);
        Assert.True(
            evidence["clientMemory"]!["peakWorkingSetBytes"]!
                .GetValue<long>() >=
            evidence["clientMemory"]!["currentWorkingSetBytes"]!
                .GetValue<long>());
        JsonNode largestRequest =
            evidence["clientMemory"]!["largestConversionRequest"]!;
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    "SHOWCASE_E2E_REQUIRE_CLIENT_MEMORY"),
                "true",
                StringComparison.Ordinal))
        {
            Assert.Equal(
                25_808_724,
                largestRequest["sourceBytes"]!.GetValue<long>());
            Assert.Equal(
                37_396_552,
                largestRequest["serializedRequestBytes"]!.GetValue<long>());
            Assert.True(
                largestRequest["beforePeakWorkingSetBytes"]!.GetValue<long>() >
                0);
            Assert.True(
                largestRequest["afterPeakWorkingSetBytes"]!.GetValue<long>() >
                0);
        }
        Assert.True(configuration[
            "ExternalEgressCanaryBlocked"]!.GetValue<bool>());
        Assert.True(configuration[
            "MarkItDownReachable"]!.GetValue<bool>());
        evidence["networkBoundary"] = new JsonObject
        {
            ["externalHttpCanaryBlocked"] = true,
            ["markItDownReachable"] = true,
            ["markItDownReachabilityProbe"] = Required(
                configuration,
                "MarkItDownReachabilityProbe"),
            ["enforcement"] = Required(
                configuration,
                "EgressEnforcement"),
            ["residualLimitation"] = Required(
                configuration,
                "EgressResidualLimitation"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, evidence.ToJsonString());
    }

    private static async Task<JsonObject[]> SimilarAsync(
        HttpClient client,
        string sourceId)
    {
        JsonObject response = await GetObjectAsync(
            client,
            $"Items/{sourceId}/Similar?Limit=1");
        return response["Items"]!.AsArray()
            .Select(static item => item!.AsObject())
            .ToArray();
    }

    private static async Task<JsonObject[]> SearchAsync(
        HttpClient client,
        string query)
    {
        JsonObject response = await GetObjectAsync(
            client,
            $"Search/Hints?SearchTerm={Uri.EscapeDataString(query)}&Limit=10");
        return response["SearchHints"]!.AsArray()
            .Select(static item => item!.AsObject())
            .ToArray();
    }

    private static JsonArray SanitizeItems(IEnumerable<JsonObject> items) =>
        new(items.Select(static item => (JsonNode)new JsonObject
        {
            ["id"] = Required(item, "Id"),
            ["name"] = Required(item, "Name"),
            ["type"] = Required(item, "Type"),
        }).ToArray());

    private static async Task WriteEvidenceAsync(
        string environmentVariable,
        JsonObject evidence)
    {
        string path = Environment.GetEnvironmentVariable(environmentVariable)
            ?? throw new InvalidOperationException(
                $"{environmentVariable} is required.");
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(
            path,
            evidence.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    NewLine = "\n",
                }));
    }

    private static async Task<Dictionary<string, JsonObject>> GetItemsByNameAsync(
        HttpClient client)
    {
        JsonObject response = await GetObjectAsync(
            client,
            "Items?Recursive=true&IncludeItemTypes=Movie,Book&Limit=20");
        return response["Items"]!.AsArray()
            .Select(static item => item!.AsObject())
            .ToDictionary(
                item => Required(item, "Name"),
                StringComparer.Ordinal);
    }

    private static async Task<JsonObject> GetObjectAsync(
        HttpClient client,
        string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync()))!.AsObject();
    }

    private static string Required(JsonObject value, string property) =>
        value[property]?.GetValue<string>()
        ?? throw new InvalidDataException($"Response omitted '{property}'.");

    private static long RequiredInt64(JsonObject value, string property) =>
        value[property]?.GetValue<long>()
        ?? throw new InvalidDataException($"Response omitted '{property}'.");

    private sealed class ShowcaseContext(HttpClient admin) : IAsyncDisposable
    {
        public HttpClient Admin { get; } = admin;

        public static async Task<ShowcaseContext> CreateAsync()
        {
            string credentialsPath =
                Environment.GetEnvironmentVariable(
                    "JELLYFIN_E2E_CREDENTIALS_PATH")
                ?? throw new InvalidOperationException(
                    "JELLYFIN_E2E_CREDENTIALS_PATH is required.");
            await using FileStream stream = File.OpenRead(credentialsPath);
            JsonObject login = (await JsonNode.ParseAsync(stream))!.AsObject();
            Uri baseAddress = new(Required(login, "Url"));
            using HttpClient anonymous = new() { BaseAddress = baseAddress };
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                "Users/AuthenticateByName");
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "MediaBrowser Client=\"Showcase E2E\", Device=\"CI\", " +
                "DeviceId=\"showcase-e2e\", Version=\"1\"");
            request.Content = JsonContent.Create(new
            {
                Username = Required(login, "UserName"),
                Pw = Required(login, "Password"),
            });
            using HttpResponseMessage response =
                await anonymous.SendAsync(request);
            response.EnsureSuccessStatusCode();
            JsonObject payload = (await JsonNode.ParseAsync(
                await response.Content.ReadAsStreamAsync()))!.AsObject();
            HttpClient authenticated = new() { BaseAddress = baseAddress };
            authenticated.DefaultRequestHeaders.TryAddWithoutValidation(
                "Authorization",
                "MediaBrowser Client=\"Showcase E2E\", Device=\"CI\", " +
                "DeviceId=\"showcase-e2e\", Version=\"1\", " +
                $"Token=\"{Required(payload, "AccessToken")}\"");
            return new ShowcaseContext(authenticated);
        }

        public ValueTask DisposeAsync()
        {
            Admin.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class ShowcaseEndToEndFactAttribute : FactAttribute
{
    public ShowcaseEndToEndFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("JELLYFIN_E2E_PROFILE"),
                "showcase",
                StringComparison.Ordinal))
        {
            Skip = "The showcase profile is an explicit manual opt-in.";
        }
    }
}

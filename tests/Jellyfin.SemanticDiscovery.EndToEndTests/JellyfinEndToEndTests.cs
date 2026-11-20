using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.SemanticDiscovery.EndToEndTests;

/// <summary>
/// Runs against a live AppHost started by scripts/Test-EndToEnd.ps1. Every call goes through
/// Jellyfin's own API, so these tests see exactly what a Jellyfin client sees.
/// </summary>
public sealed class JellyfinEndToEndTests
{
    [EndToEndTheory]
    [InlineData("official Jellyfin 12 experience remains intact", "Jellyfin, with a New Capability")]
    [InlineData("keyed dependency injection selects AI providers", "One Contract, Many Models")]
    [InlineData("attention masks mark real tokens and padding with zero", "Tokens to Vectors")]
    [InlineData("confidence threshold rejects uncertain results", "Routing and Decisions")]
    [InlineData("humans approve consequential work after validation", "Tools, Agents, and Human Control")]
    [InlineData("how do token offsets align tokens with the original source text?", "Tokenizers Field Guide")]
    [InlineData("which tensor API is best for vectorized embedding hot loops?", "Tensors for Embeddings")]
    [InlineData("who decides correctness in a verifiable AI workflow?", "Verifiable AI Systems")]
    public async Task SearchFindsItemsByMeaning(string query, string expected)
    {
        using HttpClient admin = await Jellyfin.LoginAsAdminAsync();
        Assert.Equal(expected, (await Jellyfin.SearchAsync(admin, query)).FirstOrDefault());
    }

    [EndToEndFact]
    public async Task SearchReturnsNothingForNoise()
    {
        using HttpClient admin = await Jellyfin.LoginAsAdminAsync();
        Assert.Empty(await Jellyfin.SearchAsync(admin, "quantum banana tractor"));
    }

    [EndToEndFact]
    public async Task SearchHonorsTheItemTypeFilter()
    {
        using HttpClient admin = await Jellyfin.LoginAsAdminAsync();
        string query = "keyed dependency injection selects AI providers";
        Assert.Contains("One Contract, Many Models", await Jellyfin.SearchAsync(admin, query, "Movie"));
        Assert.DoesNotContain("One Contract, Many Models", await Jellyfin.SearchAsync(admin, query, "Book"));
    }

    [EndToEndFact]
    public async Task SearchOnlyReturnsItemsTheUserCanSee()
    {
        using HttpClient admin = await Jellyfin.LoginAsAdminAsync();
        JsonArray folders = (await admin.GetFromJsonAsync<JsonArray>("Library/VirtualFolders"))!;
        string books = folders.Single(f => (string?)f!["Name"] == "Semantic Books")!["ItemId"]!.GetValue<string>();

        (string id, HttpClient reader) = await Jellyfin.CreateUserAsync(admin, enabledFolders: [books]);
        try
        {
            using (reader)
            {
                Assert.DoesNotContain("One Contract, Many Models",
                    await Jellyfin.SearchAsync(reader, "keyed dependency injection selects AI providers"));
                Assert.Contains("Verifiable AI Systems",
                    await Jellyfin.SearchAsync(reader, "who decides correctness in a verifiable AI workflow?"));
            }
        }
        finally
        {
            (await admin.DeleteAsync($"Users/{id}")).EnsureSuccessStatusCode();
        }
    }

    [EndToEndFact]
    public async Task MoreLikeThisComesFromTheSemanticIndex()
    {
        using HttpClient admin = await Jellyfin.LoginAsAdminAsync();
        Dictionary<string, string> ids = await Jellyfin.ItemIdsAsync(admin);

        JsonObject similar = (await admin.GetFromJsonAsync<JsonObject>($"Items/{ids["Tokens to Vectors"]}/Similar?Limit=5"))!;
        string[] names = similar["Items"]!.AsArray().Select(item => (string)item!["Name"]!).ToArray();

        Assert.NotEmpty(names);
        Assert.DoesNotContain("Tokens to Vectors", names);
    }

    [EndToEndFact]
    public async Task EveryDemoItemIsInTheLibraryWithArtwork()
    {
        using HttpClient admin = await Jellyfin.LoginAsAdminAsync();
        JsonObject response = (await admin.GetFromJsonAsync<JsonObject>(
            "Items?Recursive=true&IncludeItemTypes=Movie,Audio,Book&Fields=ImageTags&Limit=100"))!;
        JsonObject[] items = response["Items"]!.AsArray().Select(item => item!.AsObject()).ToArray();

        Assert.Equal(10, items.Length);
        // Movies and books use poster.jpg / <name>.jpg beside the file; songs carry the cover in the FLAC.
        Assert.All(items, item => Assert.False(
            string.IsNullOrEmpty((string?)item["ImageTags"]?["Primary"]),
            $"{item["Name"]} has no Primary image."));
    }

    [DemoFact]
    public async Task ScannedBookIsReadByTheVisionModel()
    {
        using HttpClient admin = await Jellyfin.LoginAsAdminAsync();
        // Retrieval Field Notes has no text layer: only the page-reading chat client can index it.
        Assert.Equal("Retrieval Field Notes",
            (await Jellyfin.SearchAsync(admin, "reading scanned pages with a vision model", "Book")).FirstOrDefault());
    }
}

internal static class Jellyfin
{
    private const string Client = "MediaBrowser Client=\"Semantic Discovery E2E\", Device=\"CI\", DeviceId=\"semantic-e2e\", Version=\"0.1.0\"";

    public static async Task<HttpClient> LoginAsAdminAsync()
    {
        JsonObject login = JsonNode.Parse(await File.ReadAllTextAsync(EndToEnd.CredentialsPath!))!.AsObject();
        return await LoginAsync(new Uri((string)login["Url"]!), (string)login["UserName"]!, (string)login["Password"]!);
    }

    public static async Task<string[]> SearchAsync(HttpClient client, string term, string? includeItemTypes = null)
    {
        string uri = $"Search/Hints?SearchTerm={Uri.EscapeDataString(term)}&Limit=20"
            + (includeItemTypes is null ? "" : $"&IncludeItemTypes={includeItemTypes}");
        JsonObject response = (await client.GetFromJsonAsync<JsonObject>(uri))!;
        return response["SearchHints"]!.AsArray().Select(hint => (string)hint!["Name"]!).ToArray();
    }

    public static async Task<Dictionary<string, string>> ItemIdsAsync(HttpClient client)
    {
        JsonObject response = (await client.GetFromJsonAsync<JsonObject>(
            "Items?Recursive=true&IncludeItemTypes=Movie,Audio,Book&Limit=100"))!;
        return response["Items"]!.AsArray().ToDictionary(item => (string)item!["Name"]!, item => (string)item!["Id"]!);
    }

    public static async Task<(string Id, HttpClient Client)> CreateUserAsync(HttpClient admin, string[] enabledFolders)
    {
        string name = $"semantic-e2e-{Guid.NewGuid():N}";
        string password = Guid.NewGuid().ToString("N");
        using HttpResponseMessage created = await admin.PostAsJsonAsync("Users/New", new { Name = name, Password = password });
        JsonObject user = (await created.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>())!;
        string id = (string)user["Id"]!;

        JsonObject policy = user["Policy"]!.AsObject();
        policy["EnableAllFolders"] = false;
        policy["EnabledFolders"] = new JsonArray(enabledFolders.Select(folder => (JsonNode?)folder).ToArray());
        (await admin.PostAsJsonAsync($"Users/{id}/Policy", policy)).EnsureSuccessStatusCode();

        return (id, await LoginAsync(admin.BaseAddress!, name, password));
    }

    private static async Task<HttpClient> LoginAsync(Uri baseAddress, string userName, string password)
    {
        HttpClient client = new() { BaseAddress = baseAddress };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Client);
        using HttpResponseMessage response = await client.PostAsJsonAsync("Users/AuthenticateByName", new { Username = userName, Pw = password });
        string token = (string)(await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>())!["AccessToken"]!;

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"{Client}, Token=\"{token}\"");
        return client;
    }
}

internal static class EndToEnd
{
    public static string? CredentialsPath => Environment.GetEnvironmentVariable("JELLYFIN_E2E_CREDENTIALS_PATH") is { Length: > 0 } path ? path : null;

    public static bool IsDemo => Environment.GetEnvironmentVariable("JELLYFIN_E2E_PROFILE") == "demo";

    public const string SkipReason = "Run scripts/Test-EndToEnd.ps1 to start the AppHost and set JELLYFIN_E2E_CREDENTIALS_PATH.";
}

public sealed class EndToEndFactAttribute : FactAttribute
{
    public EndToEndFactAttribute()
    {
        if (EndToEnd.CredentialsPath is null)
        {
            Skip = EndToEnd.SkipReason;
        }
    }
}

public sealed class EndToEndTheoryAttribute : TheoryAttribute
{
    public EndToEndTheoryAttribute()
    {
        if (EndToEnd.CredentialsPath is null)
        {
            Skip = EndToEnd.SkipReason;
        }
    }
}

/// <summary>Needs the demo profile, where a vision-capable chat client reads the scanned book.</summary>
public sealed class DemoFactAttribute : FactAttribute
{
    public DemoFactAttribute()
    {
        if (EndToEnd.CredentialsPath is null || !EndToEnd.IsDemo)
        {
            Skip = "Runs with scripts/Test-EndToEnd.ps1 -Profile demo.";
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Setup;

/// <summary>
/// Takes a fresh Jellyfin to "ready for the demo" through its REST API: finish the startup
/// wizard, create the demo libraries, scan them, then run the plugin's sync task so every
/// item and book page is in the semantic index.
/// </summary>
public sealed class JellyfinBootstrapper(Uri jellyfin, string userName, string password, DemoLibraryManifest manifest, TimeSpan timeout)
    : IDisposable
{
    private const string Client =
        "MediaBrowser Client=\"Semantic Discovery Setup\", Device=\"Aspire\", DeviceId=\"semantic-discovery-setup\", Version=\"0.1.0\"";

    private static readonly string[] SimilarItemProviders = ["Semantic Discovery"];

    // "Image Extractor" reads the cover art embedded in the FLACs. Everything else is local.
    private static readonly string[] ImageFetchers = ["Image Extractor"];

    private readonly HttpClient _http = new() { BaseAddress = jellyfin };

    public void Dispose() => _http.Dispose();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PollAsync("Jellyfin to start", async () =>
        {
            try { return (await _http.GetAsync("System/Info/Public", cancellationToken)).IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        }, cancellationToken);

        JsonElement info = await GetAsync("System/Info/Public", cancellationToken);
        if (!info.GetProperty("StartupWizardCompleted").GetBoolean())
        {
            await CompleteStartupWizardAsync(cancellationToken);
        }

        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"{Client}, Token=\"{await AuthenticateAsync(cancellationToken)}\"");

        await CreateLibrariesAsync(cancellationToken);
        await ScanLibrariesAsync(cancellationToken);
        await RunTaskAsync("SemanticDiscoveryRebuild", cancellationToken);
        Console.WriteLine("Semantic index is up to date.");
    }

    private async Task CompleteStartupWizardAsync(CancellationToken cancellationToken)
    {
        await GetAsync("Startup/User", cancellationToken);
        await PostAsync("Startup/User", new { Name = userName, Password = password }, cancellationToken);
        await PostAsync("Startup/Configuration", new { UICulture = "en-US", MetadataCountryCode = "US", PreferredMetadataLanguage = "en" }, cancellationToken);
        await PostAsync("Startup/RemoteAccess", new { EnableRemoteAccess = false, EnableAutomaticPortMapping = false }, cancellationToken);
        await PostAsync("Startup/Complete", new { }, cancellationToken);
    }

    private async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "Users/AuthenticateByName")
        {
            Content = JsonContent.Create(new { Username = userName, Pw = password }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Client);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("AccessToken").GetString()!;
    }

    /// <summary>Local metadata only, and "Semantic Discovery" as the More Like This provider.</summary>
    private async Task CreateLibrariesAsync(CancellationToken cancellationToken)
    {
        JsonElement folders = await GetAsync("Library/VirtualFolders", cancellationToken);
        HashSet<string?> existing = folders.EnumerateArray().Select(f => f.GetProperty("Name").GetString()).ToHashSet();

        foreach (DemoLibrary library in manifest.Libraries.Where(l => !existing.Contains(l.Name)))
        {
            await PostAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(library.Name)}&collectionType={library.CollectionType}" +
                $"&paths={Uri.EscapeDataString(library.Path)}&refreshLibrary=false",
                new
                {
                    LibraryOptions = new
                    {
                        EnableInternetProviders = false,
                        EnableRealtimeMonitor = false,
                        EnableChapterImageExtraction = false,
                        EnableTrickplayImageExtraction = false,
                        SaveLocalMetadata = false,
                        TypeOptions = new[]
                        {
                            new
                            {
                                Type = library.ItemType,
                                MetadataFetchers = Array.Empty<string>(),
                                ImageFetchers,
                                SimilarItemProviderOrder = SimilarItemProviders,
                            },
                        },
                    },
                },
                cancellationToken);
            Console.WriteLine($"Created library '{library.Name}'.");
        }
    }

    private async Task ScanLibrariesAsync(CancellationToken cancellationToken)
    {
        await PostAsync("Library/Refresh", null, cancellationToken);
        int expected = manifest.Libraries.Sum(l => l.Items.Length);
        string types = string.Join(',', manifest.Libraries.Select(l => l.ItemType).Distinct());
        await PollAsync($"the library scan to find {expected} items", async () =>
        {
            JsonElement items = await GetAsync($"Items?Recursive=true&IncludeItemTypes={types}&Limit=0", cancellationToken);
            return items.GetProperty("TotalRecordCount").GetInt32() >= expected;
        }, cancellationToken);
        await RunTaskAsync("RefreshLibrary", cancellationToken, start: false);
    }

    /// <summary>Starts a scheduled task (or just waits for a running one) and waits for it to complete.</summary>
    private async Task RunTaskAsync(string key, CancellationToken cancellationToken, bool start = true)
    {
        JsonElement tasks = await GetAsync("ScheduledTasks", cancellationToken);
        string id = tasks.EnumerateArray().Single(t => t.GetProperty("Key").GetString() == key).GetProperty("Id").GetString()!;

        await PollAsync($"'{key}' to be idle", async () => State(await GetAsync($"ScheduledTasks/{id}", cancellationToken)) == "Idle", cancellationToken);
        if (!start)
        {
            return;
        }

        string? before = LastEnd(await GetAsync($"ScheduledTasks/{id}", cancellationToken));
        await PostAsync($"ScheduledTasks/Running/{id}", null, cancellationToken);
        await PollAsync($"'{key}' to finish", async () =>
        {
            JsonElement task = await GetAsync($"ScheduledTasks/{id}", cancellationToken);
            if (State(task) != "Idle" || LastEnd(task) == before)
            {
                return false;
            }

            string status = task.GetProperty("LastExecutionResult").GetProperty("Status").GetString()!;
            return status == "Completed" ? true : throw new InvalidOperationException(
                $"'{key}' ended with status {status}: {task.GetProperty("LastExecutionResult").GetProperty("ErrorMessage")}");
        }, cancellationToken);
    }

    private static string? State(JsonElement task) => task.GetProperty("State").GetString();

    private static string? LastEnd(JsonElement task) =>
        task.TryGetProperty("LastExecutionResult", out JsonElement result) && result.ValueKind == JsonValueKind.Object
            ? result.GetProperty("EndTimeUtc").GetString()
            : null;

    private async Task PollAsync(string what, Func<Task<bool>> done, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (!await done())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<JsonElement>(path, cancellationToken);

    private async Task PostAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = body is null
            ? await _http.PostAsync(path, null, cancellationToken)
            : await _http.PostAsJsonAsync(path, body, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            response.EnsureSuccessStatusCode();
        }
    }
}

using System.Globalization;
using System.Text.Json;
using Jellyfin.SemanticDiscovery.Setup;

// Aspire runs this once Jellyfin is healthy. Everything comes from environment variables.
string Env(string name, string? fallback = null) =>
    Environment.GetEnvironmentVariable(name) ?? fallback
    ?? throw new InvalidOperationException($"{name} is required.");

DemoLibraryManifest manifest = JsonSerializer.Deserialize<DemoLibraryManifest>(
    File.ReadAllText(Env("JELLYFIN_LIBRARY_MANIFEST")),
    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

using JellyfinBootstrapper bootstrapper = new(
    new Uri(Env("JELLYFIN_URL").TrimEnd('/') + "/"),
    Env("JELLYFIN_ADMIN_USER", "semantic-admin"),
    Env("JELLYFIN_ADMIN_PASSWORD"),
    manifest,
    TimeSpan.FromSeconds(int.Parse(Env("JELLYFIN_READY_TIMEOUT_SECONDS", "600"), CultureInfo.InvariantCulture)));

await bootstrapper.RunAsync(CancellationToken.None);

if (Environment.GetEnvironmentVariable("JELLYFIN_CREDENTIALS_PATH") is { Length: > 0 } credentialsPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(credentialsPath))!);
    File.WriteAllText(credentialsPath, JsonSerializer.Serialize(new
    {
        Url = Env("JELLYFIN_URL"),
        UserName = Env("JELLYFIN_ADMIN_USER", "semantic-admin"),
        Password = Env("JELLYFIN_ADMIN_PASSWORD"),
    }));
    Console.WriteLine($"Jellyfin login written to {credentialsPath}.");
}

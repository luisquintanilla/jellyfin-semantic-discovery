using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.SemanticDiscovery.Setup;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Setup;

public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"plugin-installer-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing-managed")]
    [InlineData("missing-native")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("host-shared")]
    public void InstallRejectsIncompleteOrUncuratedGraphAndPreservesDestination(
        string mutation)
    {
        string source = CreateValidSource();
        string destination = Path.Combine(_root, "installed");
        Directory.CreateDirectory(destination);
        string sentinel = Path.Combine(destination, "sentinel.txt");
        File.WriteAllText(sentinel, "prior");
        JsonObject manifest = ReadManifest(source);

        switch (mutation)
        {
            case "missing-managed":
                File.Delete(Path.Combine(source, "Google.Protobuf.dll"));
                break;
            case "missing-native":
                File.Delete(Path.Combine(source, "libonnxruntime.so"));
                break;
            case "duplicate":
                manifest["assemblies"]!.AsArray().Add(
                    PluginInstaller.ExpectedManagedAssemblies[0]);
                WriteManifest(source, manifest);
                break;
            case "extra":
                manifest["assemblies"]!.AsArray().Add("Unexpected.Private.dll");
                WriteManifest(source, manifest);
                break;
            case "host-shared":
                manifest["assemblies"]!.AsArray().Add(
                    "Microsoft.Extensions.Logging.Abstractions.dll");
                WriteManifest(source, manifest);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.ThrowsAny<Exception>(
            () => PluginInstaller.Install(source, destination));
        Assert.Equal("prior", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(destination));
    }

    [Fact]
    public void InstallCopiesOnlyTheCuratedGraph()
    {
        string source = CreateValidSource();
        File.WriteAllText(Path.Combine(source, "Jellyfin.Controller.dll"), "host");
        string destination = Path.Combine(_root, "installed");

        PluginInstaller.Install(source, destination);

        Assert.Equal(
            PluginInstaller.ExpectedManagedAssemblies
                .Append("meta.json")
                .Concat(
                [
                    "libonnxruntime.so",
                    "libonnxruntime_providers_shared.so",
                    "onnxruntime.dll",
                    "onnxruntime_providers_shared.dll",
                ])
                .Order(StringComparer.Ordinal),
            Directory.GetFiles(destination)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
        Assert.False(File.Exists(
            Path.Combine(destination, "Jellyfin.Controller.dll")));
    }

    private string CreateValidSource()
    {
        string source = Path.Combine(_root, $"source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        foreach (string file in PluginInstaller.ExpectedManagedAssemblies.Concat(
        [
            "libonnxruntime.so",
            "libonnxruntime_providers_shared.so",
            "onnxruntime.dll",
            "onnxruntime_providers_shared.dll",
        ]))
        {
            File.WriteAllText(Path.Combine(source, file), file);
        }

        JsonObject manifest = new()
        {
            ["guid"] = "de965148-5789-4e8c-9774-6d1409ef3478",
            ["targetAbi"] = "12.0.0.0",
            ["version"] = "0.1.0.0",
            ["assemblies"] = new JsonArray(
                PluginInstaller.ExpectedManagedAssemblies
                    .Select(static value => (JsonNode?)value)
                    .ToArray()),
        };
        WriteManifest(source, manifest);
        return source;
    }

    private static JsonObject ReadManifest(string source) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(source, "meta.json")))!
            .AsObject();

    private static void WriteManifest(string source, JsonObject manifest) =>
        File.WriteAllText(
            Path.Combine(source, "meta.json"),
            manifest.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
}

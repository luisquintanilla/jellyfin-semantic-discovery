using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Setup;

public static class PluginInstaller
{
    internal static readonly string[] ExpectedManagedAssemblies =
    [
        "Jellyfin.SemanticDiscovery.Plugin.dll",
        "Jellyfin.SemanticDiscovery.Core.dll",
        "CommunityToolkit.VectorData.InMemory.dll",
        "Google.Protobuf.dll",
        "Markdig.Signed.dll",
        "Microsoft.Extensions.AI.Abstractions.dll",
        "Microsoft.Extensions.DataIngestion.Abstractions.dll",
        "Microsoft.Extensions.DataIngestion.Markdig.dll",
        "Microsoft.Extensions.VectorData.Abstractions.dll",
        "Microsoft.ML.OnnxRuntime.dll",
        "Microsoft.ML.Tokenizers.dll",
        "System.Numerics.Tensors.dll",
    ];

    private static readonly string[] RequiredFiles =
    [
        .. ExpectedManagedAssemblies,
        "meta.json",
        "libonnxruntime.so",
        "libonnxruntime_providers_shared.so",
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
    ];

    public static void Install(string sourceDirectory, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string source = Path.GetFullPath(sourceDirectory);
        string destination = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"Plugin build output '{source}' does not exist.");
        }

        foreach (string file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(source, file)))
            {
                throw new FileNotFoundException(
                    $"Plugin build output is missing '{file}'.",
                    Path.Combine(source, file));
            }
        }

        ValidateManifest(Path.Combine(source, "meta.json"), source);
        string? parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException(
                "The plugin destination must have a parent directory.");
        }

        Directory.CreateDirectory(parent);
        string staging = $"{destination}.{Guid.NewGuid():N}.staging";
        string backup = $"{destination}.{Guid.NewGuid():N}.backup";
        try
        {
            Directory.CreateDirectory(staging);
            foreach (string file in RequiredFiles)
            {
                File.Copy(
                    Path.Combine(source, file),
                    Path.Combine(staging, file));
            }
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
            }

            Directory.Move(staging, destination);
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            if (Directory.Exists(backup) && !Directory.Exists(destination))
            {
                Directory.Move(backup, destination);
            }
        }
    }

    private static void ValidateManifest(string path, string sourceDirectory)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("guid").GetGuid() !=
                Guid.Parse("de965148-5789-4e8c-9774-6d1409ef3478") ||
            root.GetProperty("targetAbi").GetString() != "12.0.0.0" ||
            root.GetProperty("version").GetString() != "0.1.0.0")
        {
            throw new InvalidDataException("The plugin manifest identity is invalid.");
        }

        if (!root.TryGetProperty("assemblies", out JsonElement assemblies) ||
            assemblies.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The plugin manifest assemblies allowlist is required.");
        }

        string[] actual = assemblies
            .EnumerateArray()
            .Select(element =>
                element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : null)
            .Where(static value => value is not null)
            .Cast<string>()
            .ToArray();
        if (actual.Length != assemblies.GetArrayLength() ||
            actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            !actual.SequenceEqual(
                ExpectedManagedAssemblies,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The plugin manifest assemblies allowlist is not the exact curated dependency graph.");
        }

        foreach (string assembly in actual)
        {
            if (!File.Exists(Path.Combine(sourceDirectory, assembly)))
            {
                throw new FileNotFoundException(
                    $"Plugin build output is missing allowlisted assembly '{assembly}'.",
                    Path.Combine(sourceDirectory, assembly));
            }
        }
    }
}

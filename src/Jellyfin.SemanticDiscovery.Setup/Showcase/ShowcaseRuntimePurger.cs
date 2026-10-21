using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Setup.Showcase;

public static class ShowcaseRuntimePurger
{
    public const string OwnershipMarkerFileName =
        ".showcase-runtime-owner.json";
    public const string OwnershipPurpose =
        "jellyfin-semantic-discovery-showcase-runtime";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    public static void EnsureOwned(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        string root = Path.GetFullPath(runtimeRoot);
        ValidateExpectedLeaf(root);
        bool existed = Directory.Exists(root);
        ShowcaseInstaller.EnsureNoReparsePoints(root);
        if (!existed)
        {
            Directory.CreateDirectory(root);
        }

        string markerPath = ShowcaseInstaller.CombineContainedPath(
            root,
            OwnershipMarkerFileName);
        ShowcaseInstaller.EnsureNoReparsePoints(markerPath);
        if (File.Exists(markerPath))
        {
            ValidateMarker(markerPath);
            return;
        }

        if (existed)
        {
            throw new InvalidDataException(
                "Refusing to adopt a pre-existing unowned showcase runtime.");
        }

        FileStreamOptions options = new()
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using FileStream marker = new(markerPath, options);
        JsonSerializer.Serialize(
            marker,
            new ShowcaseRuntimeOwnershipMarker(
                SchemaVersion: 1,
                ShowcaseSourceLock.ExpectedProfileId,
                OwnershipPurpose),
            JsonOptions);
        marker.Flush(flushToDisk: true);
    }

    public static void Purge(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        string root = Path.GetFullPath(runtimeRoot);
        ValidateExpectedLeaf(root);
        if (!Directory.Exists(root))
        {
            throw new InvalidDataException(
                "Showcase runtime purge requires an existing expected runtime directory.");
        }

        ShowcaseInstaller.EnsureNoReparsePoints(root);
        string markerPath = ShowcaseInstaller.CombineContainedPath(
            root,
            OwnershipMarkerFileName);
        ShowcaseInstaller.EnsureNoReparsePoints(markerPath);
        if (!File.Exists(markerPath))
        {
            throw new InvalidDataException(
                "Showcase runtime purge refused a directory without its ownership marker.");
        }

        ValidateMarker(markerPath);
        Directory.Delete(root, recursive: true);
        if (Directory.Exists(root))
        {
            throw new IOException(
                "Showcase runtime purge left residual files.");
        }
    }

    private static void ValidateExpectedLeaf(string root)
    {
        string leaf = Path.GetFileName(root);
        if (leaf is not ("showcase-runtime" or
                         "showcase-publication-runtime" or
                         "showcase-measurement-runtime"))
        {
            throw new InvalidDataException(
                "Showcase runtime path does not have an expected leaf name.");
        }
    }

    private static void ValidateMarker(string markerPath)
    {
        using JsonDocument marker =
            JsonDocument.Parse(File.ReadAllBytes(markerPath));
        if (!marker.RootElement.TryGetProperty(
                "SchemaVersion",
                out JsonElement schema) ||
            schema.GetInt32() != 1 ||
            !marker.RootElement.TryGetProperty(
                "ProfileId",
                out JsonElement profile) ||
            profile.GetString() != ShowcaseSourceLock.ExpectedProfileId ||
            !marker.RootElement.TryGetProperty(
                "Purpose",
                out JsonElement purpose) ||
            purpose.GetString() != OwnershipPurpose)
        {
            throw new InvalidDataException(
                "Showcase runtime ownership marker is invalid.");
        }
    }
}

internal sealed record ShowcaseRuntimeOwnershipMarker(
    int SchemaVersion,
    string ProfileId,
    string Purpose);

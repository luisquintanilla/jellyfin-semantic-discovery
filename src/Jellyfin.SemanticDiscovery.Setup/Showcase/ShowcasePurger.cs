using System.Text.Json;

namespace Jellyfin.SemanticDiscovery.Setup.Showcase;

public static class ShowcasePurger
{
    public static void Purge(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        string root = Path.GetFullPath(outputRoot);
        string filesystemRoot = Path.GetPathRoot(root)
            ?? throw new InvalidDataException("Showcase purge path has no root.");
        string? parent = Path.GetDirectoryName(root);
        if (string.Equals(
                root.TrimEnd(Path.DirectorySeparatorChar),
                filesystemRoot.TrimEnd(Path.DirectorySeparatorChar),
                PathComparison()) ||
            parent is null ||
            !Directory.Exists(root))
        {
            throw new InvalidDataException(
                "Showcase purge requires an existing non-root owned directory.");
        }

        ShowcaseInstaller.EnsureNoReparsePoints(root);
        string ownerPath = Path.Combine(
            root,
            ShowcaseInstaller.OwnershipMarkerFileName);
        string legacyPath = Path.Combine(root, "showcase.actual.json");
        string markerPath = File.Exists(ownerPath)
            ? ownerPath
            : legacyPath;
        if (!File.Exists(markerPath))
        {
            throw new InvalidDataException(
                "Showcase purge refused a directory without its ownership marker.");
        }

        ShowcaseInstaller.EnsureNoReparsePoints(markerPath);
        using JsonDocument marker =
            JsonDocument.Parse(File.ReadAllBytes(markerPath));
        if (!marker.RootElement.TryGetProperty(
                "ProfileId",
                out JsonElement profile) ||
            profile.GetString() != ShowcaseSourceLock.ExpectedProfileId ||
            !marker.RootElement.TryGetProperty(
                "DownloaderIdentity",
                out JsonElement downloader) ||
            downloader.GetString() !=
                ShowcaseSourceLock.ExpectedDownloaderIdentity ||
            (markerPath == ownerPath &&
             (!marker.RootElement.TryGetProperty(
                 "SchemaVersion",
                 out JsonElement schema) ||
              schema.GetInt32() != 1)))
        {
            throw new InvalidDataException(
                "Showcase purge ownership marker is invalid.");
        }

        Directory.Delete(root, recursive: true);
        if (Directory.Exists(root))
        {
            throw new IOException(
                "Showcase purge left residual files.");
        }
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

using System.Security.Cryptography;
using Jellyfin.SemanticDiscovery.Extraction.Configuration;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Manifest;
using Jellyfin.SemanticDiscovery.Extraction.Pdf;

namespace Jellyfin.SemanticDiscovery.Extraction.Validation;

public sealed record ValidatedExtractionSource(
    ExtractionManifestItem ManifestItem,
    string SourcePath,
    string SourceSha256,
    int PageCount);

public interface IExtractionSourceValidator
{
    ValueTask<ValidatedExtractionSource> ValidateAsync(
        string manifestItemId,
        string expectedSourceSha256,
        ExtractionProfile profile,
        CancellationToken cancellationToken);
}

public sealed class ExtractionSourceValidator(
    IExtractionManifestCatalog manifest,
    IPdfDocumentInspector pdfInspector,
    ExtractionOptions options) : IExtractionSourceValidator
{
    public async ValueTask<ValidatedExtractionSource> ValidateAsync(
        string manifestItemId,
        string expectedSourceSha256,
        ExtractionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestItemId);
        string normalizedExpectedHash = NormalizeSha256(expectedSourceSha256);
        if (!Enum.IsDefined(profile))
        {
            throw new ExtractionValidationException(
                "The extraction profile is not supported.");
        }

        ExtractionManifestItem item =
            await manifest.FindAsync(
                manifestItemId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new ExtractionValidationException(
                $"Manifest item '{manifestItemId}' was not found.");
        string manifestHash = NormalizeSha256(item.SourceSha256);
        if (!string.Equals(
                normalizedExpectedHash,
                manifestHash,
                StringComparison.Ordinal))
        {
            throw new ExtractionValidationException(
                "The expected source SHA-256 does not match the manifest.");
        }

        string sourcePath = ResolveContainedPath(
            manifest.ManifestRoot,
            item.SourcePath);
        if (!string.Equals(
                Path.GetExtension(sourcePath),
                ".pdf",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionValidationException(
                "Only .pdf manifest sources are supported.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new ExtractionValidationException(
                "The manifest source does not exist.");
        }

        EnsureNoReparsePoints(manifest.ManifestRoot, sourcePath);
        FileInfo source = new(sourcePath);
        if (source.Length != item.SourceSizeBytes)
        {
            throw new ExtractionValidationException(
                "The source size does not match the manifest.");
        }

        if (source.Length > options.MaximumSourceBytes)
        {
            throw new ExtractionValidationException(
                "The source exceeds the configured size limit.");
        }

        string actualHash = await ComputeSha256Async(
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, manifestHash, StringComparison.Ordinal))
        {
            throw new ExtractionValidationException(
                "The source SHA-256 does not match the manifest.");
        }

        int pageCount = await pdfInspector.GetPageCountAsync(
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        if (pageCount != item.PageCount)
        {
            throw new ExtractionValidationException(
                "The PDF page count does not match the manifest.");
        }

        if (pageCount > options.MaximumPages)
        {
            throw new ExtractionValidationException(
                "The PDF exceeds the configured page limit.");
        }

        return new ValidatedExtractionSource(
            item,
            sourcePath,
            actualHash,
            pageCount);
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ExtractionValidationException(
                "The manifest source path must be relative.");
        }

        string normalizedRelativePath = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath) ||
            (normalizedRelativePath.Length >= 2 &&
             normalizedRelativePath[1] == ':'))
        {
            throw new ExtractionValidationException(
                "The manifest source path must be relative.");
        }

        string fullRoot = Path.GetFullPath(root);
        string sourcePath = Path.GetFullPath(
            Path.Combine(fullRoot, normalizedRelativePath));
        string relative = Path.GetRelativePath(fullRoot, sourcePath);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ExtractionValidationException(
                "The manifest source path escapes the manifest root.");
        }

        return sourcePath;
    }

    private static void EnsureNoReparsePoints(string root, string sourcePath)
    {
        string fullRoot = Path.GetFullPath(root);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExtractionValidationException(
                "The manifest root cannot be a symbolic link or reparse point.");
        }

        string relative = Path.GetRelativePath(fullRoot, sourcePath);
        string current = fullRoot;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ExtractionValidationException(
                    "Manifest sources cannot traverse symbolic links or reparse points.");
            }
        }
    }

    private static string NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ExtractionValidationException(
                "A 64-character hexadecimal source SHA-256 is required.");
        }

        return value.ToLowerInvariant();
    }

    private static async ValueTask<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}

public sealed class ExtractionValidationException(string message) :
    Exception(message);

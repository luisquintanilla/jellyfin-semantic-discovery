using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.SemanticDiscovery.Core.Projection;

public static partial class TextNormalization
{
    public static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormKC);
        return WhitespaceRegex().Replace(normalized, " ").Trim();
    }

    public static string NormalizeForIdentity(string? value) =>
        NormalizeWhitespace(value).ToUpperInvariant();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

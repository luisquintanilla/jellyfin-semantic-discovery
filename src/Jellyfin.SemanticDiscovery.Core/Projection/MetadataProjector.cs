using System.Globalization;
using System.Text;
using Jellyfin.SemanticDiscovery.Core.Domain;

namespace Jellyfin.SemanticDiscovery.Core.Projection;

public static class MetadataProjector
{
    public static string Project(MetadataRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);

        StringBuilder builder = new();
        Append(builder, "title", item.Title);
        Append(builder, "original title", item.OriginalTitle);
        Append(builder, "type", item.Kind.ToString());
        Append(builder, "media type", item.MediaType);
        Append(builder, "year", item.ProductionYear?.ToString(CultureInfo.InvariantCulture));
        Append(builder, "overview", item.Overview);
        AppendMany(builder, "genres", item.Genres);
        AppendMany(builder, "tags", item.Tags);
        AppendMany(builder, "people", item.People);
        AppendMany(builder, "studios", item.Studios);

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string label, string? value)
    {
        string normalized = TextNormalization.NormalizeWhitespace(value);
        if (normalized.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(label).Append(": ").Append(normalized);
    }

    private static void AppendMany(StringBuilder builder, string label, IEnumerable<string> values)
    {
        string joined = string.Join(
            ", ",
            values
                .Select(TextNormalization.NormalizeWhitespace)
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(static value => value, StringComparer.Ordinal));

        Append(builder, label, joined);
    }
}

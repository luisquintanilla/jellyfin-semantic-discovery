using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.SemanticDiscovery.Core.Projection;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

public sealed partial class SubtitleCueReader
{
    public static ValueTask<IReadOnlyList<SubtitleCue>> ReadAsync(
        TextReader reader,
        CancellationToken cancellationToken = default) =>
        ReadAsync(reader, 50_000, 20_000, cancellationToken);

    public static async ValueTask<IReadOnlyList<SubtitleCue>> ReadAsync(
        TextReader reader,
        int maximumLines,
        int maximumCues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCues);

        List<string> lines = [];
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (lines.Count >= maximumLines)
            {
                throw new InvalidDataException(
                    $"Subtitle input exceeds the {maximumLines}-line limit.");
            }

            lines.Add(line.TrimStart('\uFEFF'));
        }

        bool isWebVtt = lines.FirstOrDefault(static line => line.Length > 0)?
            .StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) == true;

        return ParseBlocks(lines, isWebVtt, maximumCues);
    }

    private static List<SubtitleCue> ParseBlocks(
        List<string> lines,
        bool isWebVtt,
        int maximumCues)
    {
        List<SubtitleCue> cues = [];
        int index = isWebVtt ? 1 : 0;

        while (index < lines.Count)
        {
            while (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
            }

            if (index >= lines.Count)
            {
                break;
            }

            if (isWebVtt &&
                (lines[index].StartsWith("NOTE", StringComparison.Ordinal) ||
                 lines[index].StartsWith("STYLE", StringComparison.Ordinal) ||
                 lines[index].StartsWith("REGION", StringComparison.Ordinal)))
            {
                index = SkipBlock(lines, index);
                continue;
            }

            if (!lines[index].Contains("-->", StringComparison.Ordinal))
            {
                index++;
            }

            if (index >= lines.Count ||
                !TryParseTiming(lines[index], out long startTicks, out long endTicks))
            {
                index = SkipBlock(lines, index);
                continue;
            }

            index++;
            List<string> textLines = [];
            while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
            {
                textLines.Add(CueTagRegex().Replace(lines[index], string.Empty));
                index++;
            }

            string text = TextNormalization.NormalizeWhitespace(string.Join(' ', textLines));
            if (text.Length > 0 && endTicks >= startTicks)
            {
                if (cues.Count >= maximumCues)
                {
                    throw new InvalidDataException(
                        $"Subtitle input exceeds the {maximumCues}-cue limit.");
                }

                cues.Add(new SubtitleCue(startTicks, endTicks, text));
            }
        }

        return cues;
    }

    private static int SkipBlock(List<string> lines, int index)
    {
        while (index < lines.Count && !string.IsNullOrWhiteSpace(lines[index]))
        {
            index++;
        }

        return index;
    }

    private static bool TryParseTiming(string line, out long startTicks, out long endTicks)
    {
        string[] timing = line.Split("-->", 2, StringSplitOptions.TrimEntries);
        if (timing.Length != 2)
        {
            startTicks = 0;
            endTicks = 0;
            return false;
        }

        string endValue = timing[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        bool hasStart = TryParseTimestamp(timing[0], out startTicks);
        bool hasEnd = TryParseTimestamp(endValue, out endTicks);
        return hasStart && hasEnd;
    }

    private static bool TryParseTimestamp(string value, out long ticks)
    {
        string normalized = value.Trim().Replace(',', '.');
        string[] parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            ticks = 0;
            return false;
        }

        int hourOffset = parts.Length == 3 ? 1 : 0;
        if (!int.TryParse(
                parts.Length == 3 ? parts[0] : "0",
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int hours) ||
            !int.TryParse(
                parts[hourOffset],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int minutes) ||
            !decimal.TryParse(
                parts[hourOffset + 1],
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal seconds))
        {
            ticks = 0;
            return false;
        }

        if (hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or >= 60)
        {
            ticks = 0;
            return false;
        }

        decimal totalSeconds = (hours * 3600m) + (minutes * 60m) + seconds;
        decimal roundedTicks = decimal.Round(
            totalSeconds * TimeSpan.TicksPerSecond,
            0,
            MidpointRounding.AwayFromZero);
        if (roundedTicks > long.MaxValue)
        {
            ticks = 0;
            return false;
        }

        ticks = decimal.ToInt64(roundedTicks);
        return true;
    }

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex CueTagRegex();
}

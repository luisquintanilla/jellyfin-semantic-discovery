using Jellyfin.SemanticDiscovery.Core.Domain;
using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Core.Ingestion;

public sealed class SubtitleChunker(ITokenCounter tokenCounter, int maxTokens = 192)
{
    private readonly ITokenCounter _tokenCounter =
        tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));

    private readonly int _maxTokens = maxTokens > 0
        ? maxTokens
        : throw new ArgumentOutOfRangeException(nameof(maxTokens));

    public IReadOnlyList<ContentChunk> Chunk(
        Guid ownerId,
        string sourcePath,
        string? language,
        ContentSourceKind sourceKind,
        IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(cues);

        List<ContentChunk> chunks = [];
        List<SubtitleCue> pending = [];

        foreach (SubtitleCue cue in cues)
        {
            foreach (SubtitleCue boundedCue in SplitCue(cue))
            {
                if (pending.Count > 0 &&
                    _tokenCounter.CountTokens(Join(pending.Append(boundedCue))) > _maxTokens)
                {
                    chunks.Add(CreateChunk(
                        ownerId,
                        sourcePath,
                        language,
                        sourceKind,
                        chunks.Count,
                        pending));
                    pending.Clear();
                }

                pending.Add(boundedCue);
            }
        }

        if (pending.Count > 0)
        {
            chunks.Add(CreateChunk(
                ownerId,
                sourcePath,
                language,
                sourceKind,
                chunks.Count,
                pending));
        }

        return chunks;
    }

    private IEnumerable<SubtitleCue> SplitCue(SubtitleCue cue)
    {
        IReadOnlyList<string> parts =
            TokenBoundedTextSplitter.Split(cue.Text, _tokenCounter, _maxTokens);
        foreach (string part in parts)
        {
            yield return cue with { Text = part };
        }
    }

    private static ContentChunk CreateChunk(
        Guid ownerId,
        string sourcePath,
        string? language,
        ContentSourceKind sourceKind,
        int ordinal,
        List<SubtitleCue> cues)
    {
        string text = Join(cues);
        string contentHash = ContentHash.Compute(text);
        return new ContentChunk
        {
            Id = ContentChunkIdentity.Compute(
                ownerId,
                sourcePath,
                sourceKind,
                ordinal,
                contentHash),
            OwnerId = ownerId,
            SourceKind = sourceKind,
            SourcePath = sourcePath,
            Ordinal = ordinal,
            Text = text,
            Language = language,
            StartTicks = cues.Min(static cue => cue.StartTicks),
            EndTicks = cues.Max(static cue => cue.EndTicks),
            ContentHash = contentHash,
        };
    }

    private static string Join(IEnumerable<SubtitleCue> cues) =>
        string.Join(' ', cues.Select(static cue => cue.Text));
}

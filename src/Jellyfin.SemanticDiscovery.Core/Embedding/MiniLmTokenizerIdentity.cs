using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public static class MiniLmTokenizerIdentity
{
    public static string EffectiveConfiguration { get; } = CreateConfiguration(
        new MiniLmTokenizerSettings(),
        maxSequenceLength: 256,
        MiniLmIndexIdentity.VocabularySha256);

    public static string Hash { get; } = ContentHash.Compute(EffectiveConfiguration);

    public static string ComputeHash(
        MiniLmTokenizerSettings settings,
        int maxSequenceLength,
        string vocabularySha256) =>
        ContentHash.Compute(
            CreateConfiguration(settings, maxSequenceLength, vocabularySha256));

    public static string CreateConfiguration(
        MiniLmTokenizerSettings settings,
        int maxSequenceLength,
        string vocabularySha256)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabularySha256);
        return
            "Microsoft.ML.Tokenizers.BertTokenizer/2.0.0;" +
            $"vocab-sha256={vocabularySha256.ToLowerInvariant()};" +
            $"lowercase={Format(settings.LowerCaseBeforeTokenization)};" +
            $"basic={Format(settings.ApplyBasicTokenization)};" +
            $"split-special={Format(settings.SplitOnSpecialTokens)};" +
            $"cjk={Format(settings.IndividuallyTokenizeCjk)};" +
            $"strip-accents={Format(settings.RemoveNonSpacingMarks)};" +
            $"max-input-chars-per-word={settings.MaxInputCharsPerWord};" +
            $"unknown-token={MiniLmTokenizerSettings.UnknownToken};" +
            $"separator-token={MiniLmTokenizerSettings.SeparatorToken};" +
            $"padding-token={MiniLmTokenizerSettings.PaddingToken};" +
            $"classification-token={MiniLmTokenizerSettings.ClassificationToken};" +
            $"masking-token={MiniLmTokenizerSettings.MaskingToken};" +
            "cls-id=101;sep-id=102;pad-id=0;unk-id=100;mask-id=103;" +
            $"max-sequence-length={maxSequenceLength}";
    }

    private static string Format(bool value) => value ? "true" : "false";
}

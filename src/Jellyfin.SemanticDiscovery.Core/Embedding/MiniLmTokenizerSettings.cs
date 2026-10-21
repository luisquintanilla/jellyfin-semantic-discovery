using Microsoft.ML.Tokenizers;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public sealed record MiniLmTokenizerSettings
{
    public const string UnknownToken = "[UNK]";
    public const string SeparatorToken = "[SEP]";
    public const string PaddingToken = "[PAD]";
    public const string ClassificationToken = "[CLS]";
    public const string MaskingToken = "[MASK]";

    public bool LowerCaseBeforeTokenization { get; init; } = true;

    public bool ApplyBasicTokenization { get; init; } = true;

    public bool SplitOnSpecialTokens { get; init; } = true;

    public bool IndividuallyTokenizeCjk { get; init; } = true;

    public bool RemoveNonSpacingMarks { get; init; } = true;

    public int MaxInputCharsPerWord { get; init; } = 100;

    internal BertOptions CreateBertOptions() => new()
    {
        LowerCaseBeforeTokenization = LowerCaseBeforeTokenization,
        ApplyBasicTokenization = ApplyBasicTokenization,
        SplitOnSpecialTokens = SplitOnSpecialTokens,
        IndividuallyTokenizeCjk = IndividuallyTokenizeCjk,
        RemoveNonSpacingMarks = RemoveNonSpacingMarks,
        MaxInputCharsPerWord = MaxInputCharsPerWord,
        UnknownToken = UnknownToken,
        SeparatorToken = SeparatorToken,
        PaddingToken = PaddingToken,
        ClassificationToken = ClassificationToken,
        MaskingToken = MaskingToken,
    };
}

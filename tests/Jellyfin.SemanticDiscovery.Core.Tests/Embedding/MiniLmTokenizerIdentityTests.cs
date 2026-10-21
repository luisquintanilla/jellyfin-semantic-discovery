using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Embedding;

public sealed class MiniLmTokenizerIdentityTests
{
    [Fact]
    public void HashEqualsShaOfCommittedEffectiveConfiguration()
    {
        string expected = ContentHash.Compute(MiniLmTokenizerIdentity.EffectiveConfiguration);

        Assert.Equal(expected, MiniLmTokenizerIdentity.Hash);
        Assert.Equal(64, MiniLmTokenizerIdentity.Hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", MiniLmTokenizerIdentity.Hash);
        Assert.Contains("unknown-token=[UNK]", MiniLmTokenizerIdentity.EffectiveConfiguration);
        Assert.Contains("masking-token=[MASK]", MiniLmTokenizerIdentity.EffectiveConfiguration);
    }

    [Theory]
    [InlineData("lowercase")]
    [InlineData("basic")]
    [InlineData("split-special")]
    [InlineData("cjk")]
    [InlineData("strip-accents")]
    [InlineData("max-input-chars")]
    public void HashChangesWhenAnyConfigurableTokenizerOptionChanges(string option)
    {
        MiniLmTokenizerSettings settings = option switch
        {
            "lowercase" => new() { LowerCaseBeforeTokenization = false },
            "basic" => new() { ApplyBasicTokenization = false },
            "split-special" => new() { SplitOnSpecialTokens = false },
            "cjk" => new() { IndividuallyTokenizeCjk = false },
            "strip-accents" => new() { RemoveNonSpacingMarks = false },
            "max-input-chars" => new() { MaxInputCharsPerWord = 99 },
            _ => throw new ArgumentOutOfRangeException(nameof(option)),
        };
        string mutatedHash = MiniLmTokenizerIdentity.ComputeHash(
            settings,
            maxSequenceLength: 256,
            MiniLmIndexIdentity.VocabularySha256);

        Assert.NotEqual(MiniLmTokenizerIdentity.Hash, mutatedHash);
    }

    [Fact]
    public void MiniLmIndexIdentityIncludesEffectiveTokenizerHash()
    {
        MiniLmEmbeddingOptions options = new()
        {
            ModelPath = "unused-model",
            VocabularyPath = "unused-vocabulary",
        };
        IndexIdentity identity = MiniLmIndexIdentity.CreateFromValidatedHashes(
            options,
            MiniLmIndexIdentity.ModelSha256,
            MiniLmIndexIdentity.VocabularySha256);

        Assert.Equal(MiniLmTokenizerIdentity.Hash, identity.TokenizerHash);
        Assert.Contains(
            "Microsoft.ML.Tokenizers/2.0.0",
            identity.TokenizerRevision,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IndexIdentityChangesWithSequenceTokenizerAndArtifactHashes()
    {
        MiniLmEmbeddingOptions baselineOptions = new()
        {
            ModelPath = "unused-model",
            VocabularyPath = "unused-vocabulary",
        };
        IndexIdentity baseline = MiniLmIndexIdentity.CreateFromValidatedHashes(
            baselineOptions,
            "model-a",
            "vocab-a");
        IndexIdentity sequenceChanged = MiniLmIndexIdentity.CreateFromValidatedHashes(
            baselineOptions with { MaxSequenceLength = 128 },
            "model-a",
            "vocab-a");
        IndexIdentity tokenizerChanged = MiniLmIndexIdentity.CreateFromValidatedHashes(
            baselineOptions with
            {
                Tokenizer = baselineOptions.Tokenizer with
                {
                    RemoveNonSpacingMarks = false,
                },
            },
            "model-a",
            "vocab-a");
        IndexIdentity modelChanged = MiniLmIndexIdentity.CreateFromValidatedHashes(
            baselineOptions,
            "model-b",
            "vocab-a");
        IndexIdentity vocabularyChanged = MiniLmIndexIdentity.CreateFromValidatedHashes(
            baselineOptions,
            "model-a",
            "vocab-b");

        Assert.NotEqual(baseline.CompositeHash, sequenceChanged.CompositeHash);
        Assert.NotEqual(baseline.CompositeHash, tokenizerChanged.CompositeHash);
        Assert.NotEqual(baseline.CompositeHash, modelChanged.CompositeHash);
        Assert.NotEqual(baseline.CompositeHash, vocabularyChanged.CompositeHash);
    }

    [Fact]
    public void CreateRejectsArtifactBytesThatDoNotMatchPinnedHashes()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"semantic-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string modelPath = Path.Combine(temporaryDirectory, "model.onnx");
            string vocabularyPath = Path.Combine(temporaryDirectory, "vocab.txt");
            File.WriteAllText(modelPath, "not the pinned model");
            File.WriteAllText(vocabularyPath, "not the pinned vocabulary");
            MiniLmEmbeddingOptions options = new()
            {
                ModelPath = modelPath,
                VocabularyPath = vocabularyPath,
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => MiniLmIndexIdentity.Create(options));

            Assert.Contains("checksum mismatch", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void CreateDerivesIdentityFromExactArtifactBytes()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"semantic-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string modelPath = Path.Combine(temporaryDirectory, "model.onnx");
            string vocabularyPath = Path.Combine(temporaryDirectory, "vocab.txt");
            File.WriteAllText(modelPath, "model-a");
            File.WriteAllText(vocabularyPath, "vocab-a");

            string modelHashA = ContentHash.Compute("model-a");
            string vocabularyHashA = ContentHash.Compute("vocab-a");
            MiniLmEmbeddingOptions firstOptions = new()
            {
                ModelPath = modelPath,
                VocabularyPath = vocabularyPath,
                ExpectedModelSha256 = modelHashA,
                ExpectedVocabularySha256 = vocabularyHashA,
            };
            IndexIdentity first = MiniLmIndexIdentity.Create(firstOptions);

            File.WriteAllText(modelPath, "model-b");
            string modelHashB = ContentHash.Compute("model-b");
            IndexIdentity second = MiniLmIndexIdentity.Create(
                firstOptions with { ExpectedModelSha256 = modelHashB });

            File.WriteAllText(vocabularyPath, "vocab-b");
            string vocabularyHashB = ContentHash.Compute("vocab-b");
            IndexIdentity third = MiniLmIndexIdentity.Create(
                firstOptions with
                {
                    ExpectedModelSha256 = modelHashB,
                    ExpectedVocabularySha256 = vocabularyHashB,
                });

            Assert.NotEqual(first.CompositeHash, second.CompositeHash);
            Assert.NotEqual(second.CompositeHash, third.CompositeHash);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

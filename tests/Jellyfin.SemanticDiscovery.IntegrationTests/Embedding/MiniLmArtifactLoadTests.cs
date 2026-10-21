using Jellyfin.SemanticDiscovery.Core.Embedding;
using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

public sealed class MiniLmArtifactLoadTests
{
    [ModelFact]
    public async Task GeneratorIdentityAndInferenceUseTheSameReadOnceArtifactBytes()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"semantic-artifact-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string modelPath = Path.Combine(temporaryDirectory, "model.onnx");
            string vocabularyPath = Path.Combine(temporaryDirectory, "vocab.txt");
            File.Copy(
                Path.Combine(MiniLmModelLocator.ModelDirectory!, "model.onnx"),
                modelPath);
            File.Copy(
                Path.Combine(MiniLmModelLocator.ModelDirectory!, "vocab.txt"),
                vocabularyPath);
            MiniLmEmbeddingOptions options = new()
            {
                ModelPath = modelPath,
                VocabularyPath = vocabularyPath,
            };

            MiniLmModelArtifacts artifacts = MiniLmModelArtifacts.Load(options);
            File.WriteAllText(modelPath, "mutated after the read-once boundary");
            File.WriteAllText(vocabularyPath, "mutated after the read-once boundary");

            using MiniLmEmbeddingGenerator generator = new(options, artifacts);
            MiniLmParityResult parity = await MiniLmParityVerifier.VerifyAsync(
                generator,
                CancellationToken.None);

            Assert.Equal(MiniLmIndexIdentity.ModelSha256, generator.Identity.ModelHash);
            Assert.Equal(
                MiniLmTokenizerIdentity.Hash,
                generator.Identity.TokenizerHash);
            Assert.Equal(384, parity.Dimensions);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}

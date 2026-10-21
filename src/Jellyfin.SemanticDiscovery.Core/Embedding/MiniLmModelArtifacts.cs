using Jellyfin.SemanticDiscovery.Core.Identity;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

internal sealed class MiniLmModelArtifacts
{
    private MiniLmModelArtifacts(
        byte[] modelBytes,
        byte[] vocabularyBytes,
        IndexIdentity identity)
    {
        ModelBytes = modelBytes;
        VocabularyBytes = vocabularyBytes;
        Identity = identity;
    }

    public byte[] ModelBytes { get; }

    public byte[] VocabularyBytes { get; }

    public IndexIdentity Identity { get; }

    public static MiniLmModelArtifacts Load(MiniLmEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        byte[] modelBytes = File.ReadAllBytes(options.ModelPath);
        byte[] vocabularyBytes = File.ReadAllBytes(options.VocabularyPath);
        IndexIdentity identity = MiniLmIndexIdentity.Create(
            options,
            modelBytes,
            vocabularyBytes);
        return new MiniLmModelArtifacts(modelBytes, vocabularyBytes, identity);
    }
}

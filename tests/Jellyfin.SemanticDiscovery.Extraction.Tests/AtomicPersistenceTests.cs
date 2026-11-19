using Jellyfin.SemanticDiscovery.Extraction.Persistence;

namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

public sealed class AtomicPersistenceTests
{
    [Fact]
    public async Task FailedReplaceKeepsPreviousStateAndRemovesTemporaryFile()
    {
        using TestDirectory directory = new();
        string path = directory.GetPath("state.json");
        AtomicJsonFile writer = new(new AtomicFileOperations());
        await writer.WriteAsync(
            path,
            new PersistedValue(1),
            CancellationToken.None);
        AtomicJsonFile failingWriter = new(new FailingReplaceOperations());

        await Assert.ThrowsAsync<IOException>(
            async () => await failingWriter.WriteAsync(
                path,
                new PersistedValue(2),
                CancellationToken.None));

        PersistedValue persisted =
            await AtomicJsonFile.ReadAsync<PersistedValue>(
                path,
                CancellationToken.None);
        Assert.Equal(1, persisted.Value);
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    private sealed record PersistedValue(int Value);

    private sealed class FailingReplaceOperations : IAtomicFileOperations
    {
        public void Replace(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected replace failure.");

        public void Delete(string path) => File.Delete(path);
    }
}

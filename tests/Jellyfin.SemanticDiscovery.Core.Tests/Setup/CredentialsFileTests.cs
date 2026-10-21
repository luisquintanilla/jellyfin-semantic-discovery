using Jellyfin.SemanticDiscovery.Setup;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Setup;

public sealed class CredentialsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"credentials-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteCredentialsFileAsyncAtomicallyReplacesWithoutStaleTemp()
    {
        string path = Path.Combine(_root, "scoped", "login.json");

        await JellyfinBootstrapper.WriteCredentialsFileAsync(
            path,
            """{"secret":"first"}"""u8.ToArray(),
            CancellationToken.None);
        await JellyfinBootstrapper.WriteCredentialsFileAsync(
            path,
            """{"secret":"second"}"""u8.ToArray(),
            CancellationToken.None);

        Assert.Equal("""{"secret":"second"}""", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(path)!,
            "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
            Assert.Equal(
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
    }

    [Fact]
    public async Task WriteCredentialsFileAsyncCancellationLeavesNoSecretOrTemp()
    {
        string path = Path.Combine(_root, "scoped", "login.json");
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => JellyfinBootstrapper.WriteCredentialsFileAsync(
                path,
                new byte[4096],
                cancellation.Token));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(path)!,
            "*.tmp"));
    }
}

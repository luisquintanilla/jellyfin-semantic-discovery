using Jellyfin.SemanticDiscovery.Plugin;

namespace Jellyfin.SemanticDiscovery.IntegrationTests;

public sealed class NativeOnnxRuntimeResolverTests
{
    [Fact]
    public void GetFileNameDoesNotDuplicateThePlatformSuffix()
    {
        string alreadySuffixed = OperatingSystem.IsWindows()
            ? "onnxruntime.dll"
            : OperatingSystem.IsLinux()
                ? "libonnxruntime.so"
                : "libonnxruntime.dylib";

        Assert.Equal(
            alreadySuffixed,
            NativeOnnxRuntimeResolver.GetFileName(alreadySuffixed));
    }

    [Theory]
    [InlineData("my-onnxruntime-proxy")]
    [InlineData("../onnxruntime")]
    [InlineData("libsomething.so")]
    public void GetFileNameRejectsUnexpectedOrPathBasedImports(string name)
    {
        Assert.Null(NativeOnnxRuntimeResolver.GetFileName(name));
    }

    [Fact]
    public void GetRequiredPathFailsInsteadOfFallingBackToGlobalNativeSearch()
    {
        string emptyDirectory = Path.Combine(
            Path.GetTempPath(),
            $"missing-ort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDirectory);
        try
        {
            DllNotFoundException exception = Assert.Throws<DllNotFoundException>(
                () => NativeOnnxRuntimeResolver.GetRequiredPath(
                    "onnxruntime",
                    emptyDirectory));

            Assert.Contains(
                "plugin-local",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(emptyDirectory);
        }
    }
}

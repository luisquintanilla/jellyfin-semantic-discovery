using Jellyfin.SemanticDiscovery.Core.Embedding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.SemanticDiscovery.Plugin;

public sealed partial class OnnxStartupSmokeService(
    ILogger<OnnxStartupSmokeService> logger) : IHostedService
{
    private readonly ILogger<OnnxStartupSmokeService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_STARTUP_SMOKE"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string modelDirectory =
            Environment.GetEnvironmentVariable("SEMANTIC_DISCOVERY_MODEL_DIRECTORY")
            ?? throw new InvalidOperationException(
                "SEMANTIC_DISCOVERY_MODEL_DIRECTORY is required for the startup smoke.");
        try
        {
            using MiniLmEmbeddingGenerator generator = new(
                new MiniLmEmbeddingOptions
                {
                    ModelPath = Path.Combine(modelDirectory, "model.onnx"),
                    VocabularyPath = Path.Combine(modelDirectory, "vocab.txt"),
                });
            MiniLmParityResult result = await MiniLmParityVerifier.VerifyAsync(
                generator,
                cancellationToken).ConfigureAwait(false);
            LogSuccess(
                _logger,
                result.Dimensions,
                result.MaximumAbsoluteError,
                result.MaximumCosineError,
                result.MaximumNormError);
        }
        catch (Exception exception) when (IsExpectedSmokeFailure(exception))
        {
            LogFailure(_logger, exception);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsExpectedSmokeFailure(Exception exception) =>
        exception is FileNotFoundException
            or DllNotFoundException
            or BadImageFormatException
            or InvalidDataException
            or OnnxRuntimeException
            or TypeInitializationException ||
        exception.InnerException is not null &&
        IsExpectedSmokeFailure(exception.InnerException);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message =
            "SEMANTIC_DISCOVERY_ONNX_SMOKE_OK dimensions={Dimensions} " +
            "maxAbsError={MaximumAbsoluteError} maxCosineError={MaximumCosineError} " +
            "maxNormError={MaximumNormError}")]
    private static partial void LogSuccess(
        ILogger logger,
        int dimensions,
        float maximumAbsoluteError,
        float maximumCosineError,
        float maximumNormError);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Critical,
        Message = "SEMANTIC_DISCOVERY_ONNX_SMOKE_FAILED")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}

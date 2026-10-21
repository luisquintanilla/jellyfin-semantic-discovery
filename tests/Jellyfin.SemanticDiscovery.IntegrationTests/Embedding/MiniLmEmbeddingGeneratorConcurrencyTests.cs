using Jellyfin.SemanticDiscovery.Core.Embedding;
using Microsoft.Extensions.AI;

namespace Jellyfin.SemanticDiscovery.IntegrationTests.Embedding;

/// <summary>
/// Verifies the lifetime-tracking fix in <see cref="MiniLmEmbeddingGenerator"/>:
/// <see cref="MiniLmEmbeddingGenerator.Dispose"/> must drain accepted
/// (in-flight and waiting) operations before disposing the ONNX session and
/// run-gate semaphore, rather than racing with them.
/// </summary>
public sealed class MiniLmEmbeddingGeneratorConcurrencyTests
{
    [ModelFact]
    public async Task DisposeDrainsInFlightAndCancelledWaitingOperationsBeforeDisposingSessionAndSemaphore()
    {
        MiniLmEmbeddingGenerator generator = MiniLmModelFixture.CreateGenerator(maxConcurrentRuns: 1);
        try
        {
            using CancellationTokenSource waitingCancellation = new();

            // Started first: with MaxConcurrentRuns=1 this call synchronously
            // acquires the run-gate semaphore and then suspends on the ONNX
            // Task.Run continuation, holding the gate while inference runs.
            Task<GeneratedEmbeddings<Embedding<float>>> inFlight =
                generator.GenerateAsync(["in-flight probe"]);

            // Started second while the gate is still held: EnterOperation
            // accepts it (it counts as an active/accepted operation for drain
            // purposes) but it blocks inside SemaphoreSlim.WaitAsync behind the
            // in-flight call.
            Task<GeneratedEmbeddings<Embedding<float>>> waiting =
                generator.GenerateAsync(
                    ["waiting probe"],
                    cancellationToken: waitingCancellation.Token);

            waitingCancellation.Cancel();

            // Dispose races with both operations. It must drain them rather
            // than disposing the session/semaphore out from under an active run.
            Task firstDispose = Task.Run(generator.Dispose);
            Task secondDispose = Task.Run(generator.Dispose);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            GeneratedEmbeddings<Embedding<float>> inFlightResult = await inFlight;
            Assert.Single(inFlightResult);

            await Task.WhenAll(firstDispose, secondDispose)
                .WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => generator.GenerateAsync(["after-dispose probe"]));
            Assert.Throws<ObjectDisposedException>(() => generator.CountTokens("after-dispose probe"));
        }
        finally
        {
            await Task.Run(generator.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [ModelFact]
    public async Task DisposeTimeoutRejectsNewWorkAndAllowsCleanupRetryAfterDrain()
    {
        MiniLmEmbeddingGenerator generator = MiniLmModelFixture.CreateGenerator(
            maxInputsPerRequest: 64,
            maxBatchSize: 64,
            maxConcurrentRuns: 1,
            disposeTimeout: TimeSpan.FromMilliseconds(1));
        bool disposed = false;
        try
        {
            string input = string.Join(' ', Enumerable.Repeat("bounded inference", 300));
            Task<GeneratedEmbeddings<Embedding<float>>> inFlight =
                generator.GenerateAsync(Enumerable.Repeat(input, 64));

            Assert.Throws<TimeoutException>(generator.Dispose);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => generator.GenerateAsync(["rejected after timeout"]));

            GeneratedEmbeddings<Embedding<float>> completed = await inFlight;
            Assert.Equal(64, completed.Count);

            generator.Dispose();
            disposed = true;
        }
        finally
        {
            if (!disposed)
            {
                await Task.Run(generator.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}

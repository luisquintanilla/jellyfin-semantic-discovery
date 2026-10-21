using System.Buffers;
using System.Numerics.Tensors;
using Jellyfin.SemanticDiscovery.Core.Identity;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public sealed class MiniLmEmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>,
    Ingestion.ITokenCounter
{
    private static readonly string[] InputNames =
        ["input_ids", "attention_mask", "token_type_ids"];

    private static readonly string[] OutputNames = ["last_hidden_state"];

    private readonly MiniLmEmbeddingOptions _options;
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly SemaphoreSlim _runGate;
    private readonly Lock _lifetimeLock = new();
    private TaskCompletionSource? _drained;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private int _activeOperations;
    private bool _disposing;
    private bool _disposed;
    private Exception? _disposeException;

    public IndexIdentity Identity { get; }

    public MiniLmSpecialTokenIds SpecialTokenIds { get; }

    public MiniLmEmbeddingGenerator(MiniLmEmbeddingOptions options)
        : this(options, MiniLmModelArtifacts.Load(options))
    {
    }

    internal MiniLmEmbeddingGenerator(
        MiniLmEmbeddingOptions options,
        MiniLmModelArtifacts artifacts)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(artifacts);
        Identity = artifacts.Identity;

        using MemoryStream vocabularyStream = new(
            artifacts.VocabularyBytes,
            writable: false);
        _tokenizer = BertTokenizer.Create(
            vocabularyStream,
            _options.Tokenizer.CreateBertOptions());
        SpecialTokenIds = new MiniLmSpecialTokenIds(
            _tokenizer.PaddingTokenId,
            _tokenizer.UnknownTokenId,
            _tokenizer.ClassificationTokenId,
            _tokenizer.SeparatorTokenId,
            _tokenizer.MaskingTokenId);
        if (SpecialTokenIds != MiniLmSpecialTokenIds.Pinned)
        {
            throw new InvalidDataException(
                $"The pinned vocabulary has unexpected special-token IDs: {SpecialTokenIds}.");
        }

        using SessionOptions sessionOptions = new()
        {
            IntraOpNumThreads = _options.IntraOpThreads,
            InterOpNumThreads = _options.InterOpThreads,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(artifacts.ModelBytes, sessionOptions);
            ValidateModelContract(session);
            _session = session;
            session = null;
        }
        finally
        {
            session?.Dispose();
        }

        _runGate = new SemaphoreSlim(
            _options.MaxConcurrentRuns,
            _options.MaxConcurrentRuns);
    }

    public int CountTokens(string text)
    {
        EnterOperation();
        try
        {
            ArgumentNullException.ThrowIfNull(text);
            return _tokenizer.EncodeToIds(
                text,
                addSpecialTokens: true,
                considerPreTokenization: true,
                considerNormalization: true).Count;
        }

        finally
        {
            ExitOperation();
        }
    }

    public IReadOnlyList<int> EncodeTokenIds(string text)
    {
        EnterOperation();
        try
        {
            ArgumentNullException.ThrowIfNull(text);
            return _tokenizer.EncodeToIds(
                text,
                _options.MaxSequenceLength,
                addSpecialTokens: true,
                out _,
                out _,
                considerPreTokenization: true,
                considerNormalization: true);
        }
        finally
        {
            ExitOperation();
        }
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            ArgumentNullException.ThrowIfNull(values);
            if (options?.Dimensions is { } dimensions &&
                dimensions != _options.Dimensions)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"The model emits exactly {_options.Dimensions} dimensions.");
            }

            string[] inputs = values.Take(_options.MaxInputsPerRequest + 1).ToArray();
            if (inputs.Length > _options.MaxInputsPerRequest)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    $"A request may contain at most {_options.MaxInputsPerRequest} inputs.");
            }

            if (inputs.Any(static value => value is null))
            {
                throw new ArgumentException(
                    "Embedding inputs cannot contain null values.",
                    nameof(values));
            }

            GeneratedEmbeddings<Embedding<float>> generated = new(inputs.Length);
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                for (int offset = 0; offset < inputs.Length; offset += _options.MaxBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(_options.MaxBatchSize, inputs.Length - offset);
                    IReadOnlyList<float[]> vectors = await RunBatchAsync(
                        inputs.AsMemory(offset, count),
                        cancellationToken).ConfigureAwait(false);

                    foreach (float[] vector in vectors)
                    {
                        generated.Add(new Embedding<float>(vector)
                        {
                            ModelId = _options.ModelId,
                            CreatedAt = DateTimeOffset.UtcNow,
                        });
                    }
                }
            }
            finally
            {
                _runGate.Release();
            }

            return generated;
        }
        finally
        {
            ExitOperation();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        if (!_disposeGate.Wait(_options.DisposeTimeout))
        {
            throw new TimeoutException(
                "Timed out waiting for another caller to finish disposing the embedding generator.");
        }

        try
        {
            Task drain;
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    if (_disposeException is not null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo
                            .Capture(_disposeException)
                            .Throw();
                    }

                    return;
                }

                _disposing = true;
                if (_activeOperations == 0)
                {
                    drain = Task.CompletedTask;
                }
                else
                {
                    _drained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drain = _drained.Task;
                }
            }

            if (!WaitWithoutPropagating(drain, _options.DisposeTimeout))
            {
                throw new TimeoutException(
                    "Timed out draining in-flight embedding operations; native resources were left intact.");
            }

            drain.GetAwaiter().GetResult();
            Exception? cleanupFailure = null;
            try
            {
                _session.Dispose();
                _runGate.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            lock (_lifetimeLock)
            {
                _disposeException = cleanupFailure;
                _disposed = true;
            }

            if (cleanupFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(cleanupFailure)
                    .Throw();
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<float[]>> RunBatchAsync(
        ReadOnlyMemory<string> inputs,
        CancellationToken cancellationToken)
    {
        List<IReadOnlyList<int>> encoded = new(inputs.Length);
        int sequenceLength = 0;
        foreach (string input in inputs.Span)
        {
            IReadOnlyList<int> ids = _tokenizer.EncodeToIds(
                input,
                _options.MaxSequenceLength,
                addSpecialTokens: true,
                out _,
                out _,
                considerPreTokenization: true,
                considerNormalization: true);
            encoded.Add(ids);
            sequenceLength = Math.Max(sequenceLength, ids.Count);
        }

        sequenceLength = Math.Max(sequenceLength, 2);
        int elementCount = checked(inputs.Length * sequenceLength);
        long[] inputIds = ArrayPool<long>.Shared.Rent(elementCount);
        long[] attentionMask = ArrayPool<long>.Shared.Rent(elementCount);
        long[] tokenTypeIds = ArrayPool<long>.Shared.Rent(elementCount);
        Array.Clear(inputIds, 0, elementCount);
        Array.Clear(attentionMask, 0, elementCount);
        Array.Clear(tokenTypeIds, 0, elementCount);

        try
        {
            for (int row = 0; row < encoded.Count; row++)
            {
                IReadOnlyList<int> ids = encoded[row];
                int rowOffset = row * sequenceLength;
                for (int column = 0; column < ids.Count; column++)
                {
                    inputIds[rowOffset + column] = ids[column];
                    attentionMask[rowOffset + column] = 1;
                }
            }

            long[] shape = [inputs.Length, sequenceLength];
            using OrtValue idsValue = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance,
                inputIds.AsMemory(0, elementCount),
                shape);
            using OrtValue maskValue = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance,
                attentionMask.AsMemory(0, elementCount),
                shape);
            using OrtValue typeValue = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance,
                tokenTypeIds.AsMemory(0, elementCount),
                shape);
            using RunOptions runOptions = new();
            using CancellationTokenRegistration cancellationRegistration =
                cancellationToken.Register(
                    static state => ((RunOptions)state!).Terminate = true,
                    runOptions);

            IDisposableReadOnlyCollection<OrtValue> output;
            try
            {
                output = await Task.Run(
                    () => _session.Run(
                        runOptions,
                        InputNames,
                        [idsValue, maskValue, typeValue],
                        OutputNames),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            using (output)
            {
                OrtValue tokenEmbeddings = output.Single();
                ReadOnlySpan<float> values = tokenEmbeddings.GetTensorDataAsSpan<float>();
                int expectedValues = checked(
                    inputs.Length * sequenceLength * _options.Dimensions);
                if (values.Length != expectedValues)
                {
                    throw new InvalidDataException(
                        $"Unexpected ONNX output length {values.Length}; expected {expectedValues}.");
                }

                List<float[]> vectors = new(inputs.Length);
                for (int row = 0; row < inputs.Length; row++)
                {
                    float[] pooled = new float[_options.Dimensions];
                    int activeTokens = 0;
                    for (int column = 0; column < sequenceLength; column++)
                    {
                        int maskIndex = (row * sequenceLength) + column;
                        if (attentionMask[maskIndex] == 0)
                        {
                            continue;
                        }

                        int vectorOffset = maskIndex * _options.Dimensions;
                        TensorPrimitives.Add(
                            pooled,
                            values.Slice(vectorOffset, _options.Dimensions),
                            pooled);
                        activeTokens++;
                    }

                    if (activeTokens == 0)
                    {
                        throw new InvalidDataException(
                            "The tokenizer emitted no active tokens for an input.");
                    }

                    TensorPrimitives.Divide(pooled, activeTokens, pooled);
                    VectorMath.Normalize(pooled);
                    vectors.Add(pooled);
                }

                return vectors;
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(inputIds, clearArray: true);
            ArrayPool<long>.Shared.Return(attentionMask, clearArray: true);
            ArrayPool<long>.Shared.Return(tokenTypeIds, clearArray: true);
        }
    }

    private void ValidateModelContract(InferenceSession session)
    {
        foreach (string inputName in InputNames)
        {
            if (!session.InputMetadata.ContainsKey(inputName))
            {
                throw new InvalidDataException(
                    $"The ONNX model is missing required input '{inputName}'.");
            }

        }

        if (!session.OutputMetadata.TryGetValue(OutputNames[0], out NodeMetadata? outputMetadata) ||
            outputMetadata is null)
        {
            throw new InvalidDataException(
                $"The ONNX model is missing required output '{OutputNames[0]}'.");
        }

        if (outputMetadata.Dimensions.Length != 3 ||
            outputMetadata.Dimensions[^1] != _options.Dimensions)
        {
            throw new InvalidDataException(
                "The ONNX model output must be [batch, sequence, 384].");
        }
    }

    private void EnterOperation()
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposing || _disposed, this);
            _activeOperations++;
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifetimeLock)
        {
            _activeOperations--;
            if (_disposing && _activeOperations == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    private static bool WaitWithoutPropagating(Task task, TimeSpan timeout) =>
        ReferenceEquals(
            Task.WhenAny(task, Task.Delay(timeout)).GetAwaiter().GetResult(),
            task);
}

public readonly record struct MiniLmSpecialTokenIds(
    int Padding,
    int Unknown,
    int Classification,
    int Separator,
    int Masking)
{
    public static MiniLmSpecialTokenIds Pinned { get; } =
        new(0, 100, 101, 102, 103);
}

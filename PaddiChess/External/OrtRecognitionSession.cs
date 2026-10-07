using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.Json;

namespace PaddiXiangqi.External;

internal sealed class OrtRecognitionSession : IRecognitionSession
{
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string[] _outputs;
    private readonly string? _profileDirectory;
    private readonly bool _hardware;
    private readonly int[] _shape;
    private readonly OrtRecognitionSession? _singleGlyph;
    private bool _profileEnded;
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal OrtRecognitionSession(string path, int[] shape, Action<SessionOptions>? configure = null)
    {
        _hardware = configure != null; _shape = shape;
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 2, InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        // Fixed dimensions enable constant folding and hardware compilation without
        // altering weights, preprocessing, precision or recognition thresholds.
        if (shape[2] == 315)
        {
            options.AddFreeDimensionOverrideByName("batch", shape[0]);
            options.AddFreeDimensionOverrideByName("height", shape[2]);
            options.AddFreeDimensionOverrideByName("width", shape[3]);
        }
        else
        {
            if (_hardware) options.AddFreeDimensionOverrideByName("DynamicDimension.0", shape[0]);
            options.AddFreeDimensionOverrideByName("DynamicDimension.1", shape[3]);
        }
        if (_hardware)
        {
            _profileDirectory = Path.Combine(Path.GetTempPath(), "PaddiChess-inference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_profileDirectory);
            // ORT reads the prefix at the moment profiling is enabled.
            options.ProfileOutputPathPrefix = Path.Combine(_profileDirectory, "warmup"); options.EnableProfiling = true;
        }
        try
        {
            configure?.Invoke(options);
            _session = new InferenceSession(path, options);
            _input = _session.InputMetadata.Keys.Single(); _outputs = _session.OutputMetadata.Keys.ToArray();
            Metadata = new Dictionary<string, string>(_session.ModelMetadata.CustomMetadataMap);
            if (_hardware && shape[0] == 8 && shape[2] == 48)
            {
                try { _singleGlyph = new(path, [1, 3, 48, 160], configure); }
                catch { _session.Dispose(); throw; }
            }
        }
        catch { CleanupProfile(); throw; }
    }

    public T Run<T>(DenseTensor<float> input, Func<Tensor<float>, T> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_singleGlyph != null && input.Dimensions[0] <= 2)
        {
            if (input.Dimensions[0] == 1) return _singleGlyph.Run(input, read, ct);
            DenseTensor<float>? combined = null;
            for (var i = 0; i < input.Dimensions[0]; i++)
            {
                var row = new DenseTensor<float>(input.Buffer.Slice(i * 3 * 48 * 160, 3 * 48 * 160), [1, 3, 48, 160]);
                _singleGlyph.Run(row, output =>
                {
                    var dimensions = output.Dimensions.ToArray(); dimensions[0] = input.Dimensions[0];
                    combined ??= new DenseTensor<float>(dimensions);
                    output.ToArray().CopyTo(combined.Buffer.Span.Slice(i * (int)output.Length));
                    return 0;
                }, ct);
            }
            return read(combined!);
        }
        if (_hardware && _shape[2] == 48 && input.Dimensions[0] < _shape[0])
        {
            // GPU compilation needs a fixed batch. The CPU keeps a variable batch so
            // supplementing a single uncertain glyph never runs eight CPU rows.
            var padded = new DenseTensor<float>(_shape);
            input.Buffer.Span.CopyTo(padded.Buffer.Span); input = padded;
        }
        using var options = new RunOptions();
        using var cancellation = ct.Register(() => options.Terminate = true);
        try
        {
            using var output = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)], _outputs, options);
            ct.ThrowIfCancellationRequested();
            return read(output.First().AsTensor<float>());
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    public void WarmUp(int[] shape)
    {
        Run(new DenseTensor<float>(shape), output =>
        {
            foreach (var value in output) if (!float.IsFinite(value)) throw new IOException("加速模型预热结果无效。");
            return 0;
        }, CancellationToken.None);
        if (!_hardware) return;
        var profile = _session.EndProfiling(); _profileEnded = true;
        try
        {
            using var trace = JsonDocument.Parse(File.ReadAllText(profile));
            if (!trace.RootElement.EnumerateArray().Any(e => e.TryGetProperty("args", out var args) &&
                args.TryGetProperty("provider", out var ep) && ep.GetString() is { Length: > 0 } name && name != "CPUExecutionProvider"))
                throw new NotSupportedException("该模型未使用任何加速算子。");
        }
        finally { CleanupProfile(); }
        _singleGlyph?.WarmUp([1, 3, 48, 160]);
    }

    private void CleanupProfile()
    {
        if (_profileDirectory == null) return;
        try { Directory.Delete(_profileDirectory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        try { if (_hardware && !_profileEnded) _session.EndProfiling(); }
        finally { _session.Dispose(); _singleGlyph?.Dispose(); CleanupProfile(); }
    }
}

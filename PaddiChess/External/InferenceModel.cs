using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PaddiChess.Tests")]

namespace PaddiXiangqi.External;

internal interface IRecognitionSession : IDisposable
{
    IReadOnlyDictionary<string, string> Metadata { get; }
    T Run<T>(DenseTensor<float> input, Func<Tensor<float>, T> read, CancellationToken ct);
    void WarmUp(int[] shape);
}

internal sealed record InferenceBackend(string Name, Func<IRecognitionSession> Create);

/// <summary>A warm accelerator replaces the CPU atomically between requests. Each model
/// has one session, one preparation attempt, and a permanent CPU fallback on device failure.</summary>
internal sealed class InferenceModel : IDisposable
{
    private readonly object _gate = new();
    private readonly string _name;
    private readonly int[] _shape;
    private readonly Func<IRecognitionSession> _cpu;
    private readonly Func<IReadOnlyList<InferenceBackend>> _accelerators;
    private readonly Action<RecognitionDeviceStatus> _report;
    private IRecognitionSession _active;
    private Task? _preparation;
    private bool _hardware, _disposed;
    internal IReadOnlyDictionary<string, string> Metadata { get; }

    internal InferenceModel(string name, int[] shape, Func<IRecognitionSession> cpu,
        Func<IReadOnlyList<InferenceBackend>> accelerators, Action<RecognitionDeviceStatus>? report = null)
    {
        _name = name; _shape = shape; _cpu = cpu; _accelerators = accelerators;
        _report = report ?? RecognitionAcceleration.Report;
        _active = cpu(); Metadata = new Dictionary<string, string>(_active.Metadata);
        _report(new(_name, "CPU", "CPU 已就绪；优先使用可用加速设备。"));
    }

    internal static InferenceModel Open(string name, string path, int[] shape)
    {
        var model = new InferenceModel(name, shape,
            () => new OrtRecognitionSession(path, shape), () => InferenceDevices.Find(path, shape));
        _ = model.PrepareAsync();
        return model;
    }

    internal Task PrepareAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _preparation ??= Task.Run(PrepareCoreAsync);
        }
    }

    private async Task PrepareCoreAsync()
    {
        await RecognitionAcceleration.PreparationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate) { if (_disposed) return; }
            _report(new(_name, "CPU · 准备加速", "首次编译在后台完成，期间仍可识别。", true));
            var failures = new List<string>();
            foreach (var backend in _accelerators())
            {
                lock (_gate) { if (_disposed) return; }
                IRecognitionSession? candidate = null;
                try
                {
                    candidate = backend.Create();
                    candidate.WarmUp(_shape); // Confirms at least one device kernel actually ran.
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _active.Dispose(); _active = candidate; candidate = null; _hardware = true;
                        _report(new(_name, backend.Name, "已预热；不支持的算子由 CPU 执行，设备故障自动回退。"));
                    }
                    return;
                }
                catch (Exception ex) when (IsBackendFailure(ex)) { failures.Add($"{backend.Name}：{ex.GetType().Name}"); }
                finally { candidate?.Dispose(); }
            }
            _report(new(_name, "CPU", failures.Count == 0
                ? "未找到兼容加速设备，或已指定 CPU。Windows NPU 需要已安装的认证执行提供程序及兼容模型。"
                : "加速不可用，已回退 CPU。" + string.Join("；", failures)));
        }
        catch (Exception ex) when (IsBackendFailure(ex))
        { _report(new(_name, "CPU", $"设备检测不可用，继续使用 CPU（{ex.GetType().Name}）。")); }
        finally { RecognitionAcceleration.PreparationGate.Release(); }
    }

    internal T Run<T>(DenseTensor<float> input, Func<Tensor<float>, T> read, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            try { return _active.Run(input, read, ct); }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (Exception ex) when (_hardware && IsBackendFailure(ex))
            {
                // The failed result is discarded. Retry this SAME input once on CPU;
                // never accept a partial result, retry input delivery, or reset the game.
                _active.Dispose(); _hardware = false; _active = _cpu();
                _report(new(_name, "CPU", $"加速推理失败，已回退 CPU（{ex.GetType().Name}）；本次启动不再重复尝试故障设备。"));
                ct.ThrowIfCancellationRequested();
                return _active.Run(input, read, ct);
            }
        }
    }

    internal static bool IsBackendFailure(Exception ex) => ex is OnnxRuntimeException or DllNotFoundException or
        EntryPointNotFoundException or BadImageFormatException or NotSupportedException or IOException or
        InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or
        System.Runtime.InteropServices.COMException ||
        ex is TypeInitializationException { InnerException: { } inner } && IsBackendFailure(inner);

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _active.Dispose(); }
    }
}

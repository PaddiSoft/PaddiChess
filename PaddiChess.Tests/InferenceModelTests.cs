using Microsoft.ML.OnnxRuntime.Tensors;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class InferenceModelTests
{
    private static readonly int[] Shape = [1, 1, 1, 1];
    private static DenseTensor<float> Input => new(new float[] { 42 }, Shape);
    private static float Read(InferenceModel model, CancellationToken ct = default) => model.Run(Input, t => t.First(), ct);
    private sealed class Session(float value) : IRecognitionSession
    {
        public bool Disposed { get; private set; }
        public int Runs { get; private set; }
        public float LastInput { get; private set; }
        public Action? Warm { get; init; }
        public Action? OnRun { get; set; }
        public IReadOnlyDictionary<string, string> Metadata => new Dictionary<string, string>();
        public void WarmUp(int[] shape) => Warm?.Invoke();
        public T Run<T>(DenseTensor<float> input, Func<Tensor<float>, T> read, CancellationToken ct)
        {
            Assert.False(Disposed); Runs++; LastInput = input.First();
            OnRun?.Invoke(); ct.ThrowIfCancellationRequested();
            return read(new DenseTensor<float>(new float[] { value }, Shape));
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task CpuContinuesRecognizingWhileGpuCompilesAndOnlySwapsAfterWarmup()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var cpu = new Session(1); var gpu = new Session(2) { Warm = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        using var model = new InferenceModel("test", Shape, () => cpu, () => [new("GPU", () => gpu)]);
        var prepare = model.PrepareAsync();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
            Assert.Equal(1, Read(model)); Assert.False(cpu.Disposed);
        }
        finally { release.Set(); }
        await prepare;
        Assert.Equal(2, Read(model)); Assert.True(cpu.Disposed);
    }

    [Fact]
    public async Task FailedGpuConstructionAndWarmupCanSelectCompatibleNpu()
    {
        var failed = new Session(2) { Warm = () => throw new NotSupportedException() };
        var npu = new Session(3); var attempts = 0;
        using var model = new InferenceModel("test", Shape, () => new Session(1), () =>
            [new("GPU 1", () => { attempts++; throw new DllNotFoundException(); }), new("GPU 2", () => failed), new("NPU", () => npu)]);
        await model.PrepareAsync(); await model.PrepareAsync();
        Assert.Equal(3, Read(model)); Assert.True(failed.Disposed); Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task InferenceFailureDiscardsGpuResultAndRetriesIdenticalInputOnceOnCpu()
    {
        var gpu = new Session(2); var cpus = new List<Session>(); var statuses = new List<RecognitionDeviceStatus>();
        using var model = new InferenceModel("test", Shape, () => { var cpu = new Session(1); cpus.Add(cpu); return cpu; },
            () => [new("GPU", () => gpu)], statuses.Add);
        await model.PrepareAsync();
        gpu.OnRun = () => throw new InvalidOperationException("device removed");
        Assert.Equal(1, Read(model)); Assert.Equal(1, Read(model));
        Assert.True(gpu.Disposed); Assert.Equal(1, gpu.Runs); Assert.Equal(2, cpus.Count);
        Assert.Equal(gpu.LastInput, cpus[1].LastInput); Assert.Equal("CPU", statuses[^1].Backend);
        await model.PrepareAsync(); Assert.Equal(1, gpu.Runs);
    }

    [Fact]
    public async Task CancellationDoesNotDisableWorkingAccelerator()
    {
        var gpu = new Session(2); var created = 0;
        using var model = new InferenceModel("test", Shape, () => { created++; return new Session(1); }, () => [new("GPU", () => gpu)]);
        await model.PrepareAsync();
        using var cancel = new CancellationTokenSource();
        gpu.OnRun = () => { cancel.Cancel(); throw new InvalidOperationException("terminated by caller"); };
        Assert.Throws<OperationCanceledException>(() => Read(model, cancel.Token));
        gpu.OnRun = null; Assert.Equal(2, Read(model)); Assert.False(gpu.Disposed); Assert.Equal(1, created);
    }

    [Fact]
    public async Task NoDeviceAndCpuFailuresRemainVisibleWithoutEndlessRetries()
    {
        var cpu = new Session(1) { OnRun = () => throw new IOException("bad model") }; var attempts = 0;
        using var model = new InferenceModel("test", Shape, () => cpu, () => { attempts++; return []; });
        await model.PrepareAsync(); await model.PrepareAsync();
        Assert.Throws<IOException>(() => Read(model)); Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DisposingDuringWarmupReleasesCandidateWithoutPublishingIt()
    {
        var gpu = new Session(2); InferenceModel? model = null;
        gpu = new Session(2) { Warm = () => model!.Dispose() };
        model = new("test", Shape, () => new Session(1), () => [new("GPU", () => gpu)]);
        await model.PrepareAsync(); Assert.True(gpu.Disposed);
        Assert.Throws<ObjectDisposedException>(() => Read(model));
    }
}

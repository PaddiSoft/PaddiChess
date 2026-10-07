using Microsoft.ML.OnnxRuntime;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class InferenceRuntimeTests
{
    [Fact]
    public void WindowsDistributionIncludesDirectMlAndCanEnumerateProvidersWithoutAGpu()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var name in new[] { "onnxruntime.dll", "DirectML.dll", "Microsoft.Windows.AI.MachineLearning.dll" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), name);
        var providers = OrtEnv.Instance().GetAvailableProviders();
        Assert.Contains("CPUExecutionProvider", providers);
        Assert.Contains("DmlExecutionProvider", providers);
        // Catalog absence, old Windows and no vendor NPU package must remain harmless.
        WindowsMlCatalog.RegisterInstalledProviders();
        Assert.NotNull(OrtEnv.Instance().GetEpDevices());
    }
}

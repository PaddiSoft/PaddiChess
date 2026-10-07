using Microsoft.ML.OnnxRuntime;

namespace PaddiXiangqi.External;

internal static class InferenceDevices
{
    internal static IReadOnlyList<InferenceBackend> Find(string path, int[] shape)
    {
        if (Environment.GetEnvironmentVariable("PADDI_RECOGNITION_DEVICE")?.Equals("cpu", StringComparison.OrdinalIgnoreCase) == true)
            return [];
        var result = new List<InferenceBackend>();
        var env = OrtEnv.Instance();
        var available = env.GetAvailableProviders();
        void Add(string name, Action<SessionOptions> configure) => result.Add(new(name,
            () => new OrtRecognitionSession(path, shape, configure)));
        if (OperatingSystem.IsMacOS() && available.Contains("CoreMLExecutionProvider"))
            Add("CoreML · 混合加速", options => options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
            {
                ["ModelFormat"] = "MLProgram", ["MLComputeUnits"] = "ALL", ["RequireStaticInputShapes"] = "1"
            })); // CoreML schedules GPU/ANE; it does not expose which physical device executed each partition.

        if (OperatingSystem.IsWindows())
        {
            // The bundled DirectML path covers NVIDIA/AMD/Intel, including integrated GPUs.
            // Certified vendor EPs add NPU support on Windows 11 24H2+ when installed.
            WindowsMlCatalog.RegisterInstalledProviders();
            var devices = env.GetEpDevices();
            foreach (var device in devices.Where(d => d.HardwareDevice.Type is OrtHardwareDeviceType.GPU or OrtHardwareDeviceType.NPU)
                .Where(d => d.HardwareDevice.VendorId != 0x1414) // Exclude Microsoft's software adapter.
                .OrderBy(d => d.HardwareDevice.Type == OrtHardwareDeviceType.GPU ? 0 : 1)
                .ThenBy(d => d.EpName == "DmlExecutionProvider" ? 0 : 1)
                .ThenBy(d => d.HardwareDevice.VendorId is 0x10de or 0x1002 ? 0 : 1))
            {
                var label = device.EpName.Replace("ExecutionProvider", "", StringComparison.Ordinal);
                Add($"{label} · {device.HardwareDevice.Type} 混合", options =>
                {
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider(env, [device], new Dictionary<string, string>());
                });
            }
            // Older drivers may expose the legacy DML interface but no OrtEpDevice.
            if (!devices.Any(d => d.EpName == "DmlExecutionProvider") && available.Contains("DmlExecutionProvider"))
                Add("DirectML · GPU 混合", options =>
                {
                    options.EnableMemoryPattern = false; options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider_DML(0);
                });
        }
        return result;
    }
}

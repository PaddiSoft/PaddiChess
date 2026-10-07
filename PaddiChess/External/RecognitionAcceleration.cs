using System.Collections.Concurrent;

namespace PaddiXiangqi.External;

public sealed record RecognitionDeviceStatus(string Model, string Backend, string Detail, bool Preparing = false);

/// <summary>Read-only runtime status; no board images or private settings are retained.</summary>
public static class RecognitionAcceleration
{
    private static readonly ConcurrentDictionary<string, RecognitionDeviceStatus> States = new();
    internal static readonly SemaphoreSlim PreparationGate = new(1, 1);
    public static IReadOnlyList<RecognitionDeviceStatus> Status => States.Values.OrderBy(s => s.Model).ToArray();
    internal static void Report(RecognitionDeviceStatus status) => States[status.Model] = status;

    // Start at the first visit to external takeover. Compilation remains off the UI
    // thread and a ready CPU session serves recognition until the accelerator is warm.
    public static async Task PrepareAsync()
    {
        try
        {
            await Task.Run(async () =>
            {
                await LocalBoardClassifier.PrepareAsync().ConfigureAwait(false);
                await LocalGlyphOcr.PrepareAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (InferenceModel.IsBackendFailure(ex))
        { Report(new("识别", "未就绪", "本地模型初始化失败；识别时会报告具体错误。")); }
    }
}

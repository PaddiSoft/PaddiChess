using Avalonia.Controls;
using Avalonia.Threading;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private DispatcherTimer? _recognitionStatusTimer;

    private void EnsureRecognitionPrepared()
    {
        if (_recognitionStatusTimer != null) return;
        _recognitionStatusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            var statuses = RecognitionAcceleration.Status;
            if (statuses.Count == 0) return;
            var text = string.Join(" · ", statuses.Select(s => $"{(s.Model == "棋子模型" ? "棋子" : "OCR")} {s.Backend.Split(" · ")[0]}{(s.Preparing ? "（预热）" : "")}"));
            var detail = string.Join("\n", statuses.Select(s => $"{s.Model} · {s.Backend}：{s.Detail}"));
            if (RecognitionDeviceText.Text == text && Equals(RecognitionDeviceText.Tag, detail)) return;
            RecognitionDeviceText.Text = text;
            RecognitionDeviceText.Tag = detail;
            ToolTip.SetTip(RecognitionDeviceText, detail);
        });
        Closed += (_, _) => _recognitionStatusTimer.Stop();
        _recognitionStatusTimer.Start();
        _ = RecognitionAcceleration.PrepareAsync();
    }
}

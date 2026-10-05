using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.Services;
using PaddiXiangqi.Views;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public sealed class ExternalHistoryWindowTests
{
    [Theory]
    [InlineData(1100, 720)]
    [InlineData(1000, 640)]
    public async Task EventPreviewLoadsEachSavedPositionAndNeverTreatsRecognitionAsTurnEvidence(int width, int height)
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-history-preview-" + Guid.NewGuid().ToString("N"));
        var before = JjRookCaptureTests.BeforeFen;
        var adopted = before.Replace(" b ", " w ");
        var observed = JjRookCaptureTests.AfterPieces + " b - - 0 1";
        var confirmed = JjRookCaptureTests.AfterPieces + " w - - 0 1";
        try
        {
            await using (var store = new ExternalHistoryStore(folder))
            {
                await store.AppendAsync(new ExternalHistoryEvent { Kind = "started", Fen = before },
                    new GameRecord { Title = "JJ 车吃车 · 事件预览测试", StartFen = before });
                await store.AppendAsync(new ExternalHistoryEvent { Kind = "blocked", Fen = adopted,
                    BeforeFen = before, ObservedFen = observed, Move = "c2e3", Candidates = ["a6b6"],
                    Message = "图像中的黑车已经吃掉红车，当前程序局面仍保留红车；本次落子被拦截。" });
                await store.AppendAsync(new ExternalHistoryEvent { Kind = "confirmed", Fen = confirmed, Move = "a6b6" });
                await store.AppendAsync(new ExternalHistoryEvent { Kind = "observation", Fen = "invalid" });
            }
            await ExternalSessionTests.Session.Dispatch(async () =>
            {
                var window = new ExternalHistoryWindow(folder, Path.Combine(folder, "Recovery"), true)
                    { Width = width, Height = height };
                try
                {
                    window.Show();
                    var events = window.FindControl<ListBox>("EventsList")!;
                    for (var attempt = 0; attempt < 100 && events.Items.Count != 4; attempt++) await Task.Delay(20);
                    Assert.Equal(4, events.Items.Count);
                    var board = window.FindControl<BoardView>("EventBoard")!;
                    Assert.False(board.IsHitTestVisible);
                    Assert.Equal(0, board.AnimationDurationMs);
                    Assert.Null(board.Game);
                    Assert.Contains("无法绘制", window.FindControl<TextBlock>("PreviewEmptyText")!.Text);

                    events.SelectedIndex = 1;
                    Assert.Equal(adopted, board.Game!.CurrentFen());
                    Click(window, "BeforePositionButton");
                    Assert.Equal(before, board.Game!.CurrentFen());
                    Click(window, "RecognizedPositionButton");
                    Assert.Equal(observed, board.Game!.CurrentFen());
                    var caption = window.FindControl<TextBlock>("PreviewCaption")!.Text;
                    Assert.Contains("行棋方以确认记录为准", caption);
                    Assert.DoesNotContain("轮到", caption);
                    Click(window, "PreviewFlipButton"); Assert.True(board.Flipped);
                    Click(window, "PreviewFlipButton"); Assert.False(board.Flipped);
                    window.UpdateLayout();
                    foreach (var name in new[] { "SessionsList", "EventsList", "EventDetails", "EventBoard",
                        "ProgramPositionButton", "BeforePositionButton", "RecognizedPositionButton", "PreviewFlipButton", "ReplayButton" })
                    {
                        var control = window.FindControl<Control>(name)!;
                        var origin = control.TranslatePoint(default, window)!.Value;
                        var end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value;
                        Assert.True(origin.X >= 0 && origin.Y >= 0 && end.X <= window.ClientSize.Width + 1 &&
                            end.Y <= window.ClientSize.Height + 1, $"{name} 超出历史窗口：{origin} → {end}");
                    }
                    var screenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui"));
                    Directory.CreateDirectory(screenshots);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    await Task.Delay(80);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(screenshots, $"external-history-{width}.png"));

                    events.SelectedIndex = 0;
                    Assert.Equal(before, board.Game!.CurrentFen());
                    Assert.False(window.FindControl<Button>("BeforePositionButton")!.IsEnabled);
                    Assert.False(window.FindControl<Button>("RecognizedPositionButton")!.IsEnabled);
                    events.SelectedIndex = 2;
                    Assert.Equal(confirmed, board.Game!.CurrentFen());
                    Assert.Contains("轮到红方", window.FindControl<TextBlock>("PreviewCaption")!.Text);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static void Click(Window window, string name) => window.FindControl<Button>(name)!
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoryShowsDecisionEvidenceAndOnlyEnablesReplayWhenDisconnected(bool canReplay)
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-history-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var store = new ExternalHistoryStore(folder))
                await store.AppendAsync(new ExternalHistoryEvent { Kind = "decision", Move = "h2e2",
                    Fen = XiangqiGame.InitialFen, ObservedFen = XiangqiGame.InitialFen, Candidates = ["h2e2", "b2e2"] },
                    new GameRecord { Title = "吃车后决策" });
            await ExternalSessionTests.Session.Dispatch(async () =>
            {
                var window = new ExternalHistoryWindow(folder, Path.Combine(folder, "Recovery"), canReplay);
                try
                {
                    window.Show();
                    var details = window.FindControl<TextBox>("EventDetails")!;
                    for (var attempt = 0; attempt < 100 && !(details.Text?.Contains("h2e2") ?? false); attempt++)
                        await Task.Delay(20);
                    Assert.Single(window.FindControl<ListBox>("SessionsList")!.Items);
                    Assert.Single(window.FindControl<ListBox>("EventsList")!.Items);
                    Assert.Contains("选择着法", details.Text);
                    Assert.Contains("图像识别局面", details.Text);
                    Assert.Contains(XiangqiGame.InitialFen, details.Text);
                    Assert.Contains("h2e2 b2e2", details.Text);
                    Assert.Equal(canReplay, window.FindControl<Button>("ReplayButton")!.IsEnabled);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData(false, 860, 700)]
    [InlineData(true, 1160, 880)]
    public async Task StandardOpeningToolFillsAllPiecesAndPreservesChosenOrientation(bool flipped, int width, int height)
    {
        await Session.Dispatch(async () =>
        {
            var target = new XiangqiGame();
            var frame = new ExternalFrame(new(882, 882, "opening fixture", 0, 0, 480, 530), ExternalBoardTests.Render(target, flipped));
            var reads = 0;
            var owner = new Window(); owner.Show();
            var dialog = new ExternalPositionEditorWindow(new(JjRookCaptureTests.BeforeFen, "手动核对", null,
                frame, new(40, 40, 440, 490, flipped), flipped, 1), null,
                (_, _, _, _, _) => { reads++; return Task.FromResult<SkinRecognition?>(null); }, default)
                { Width = width, Height = height };
            var resultTask = dialog.ShowDialog<ExternalPositionEditResult?>(owner);
            try
            {
                var standard = dialog.FindControl<Button>("ExternalEditorStandard")!;
                dialog.UpdateLayout();
                Assert.True(standard.IsVisible); Assert.True(standard.IsEnabled);
                Assert.Empty(standard.GetVisualAncestors().OfType<Expander>());
                standard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var view = dialog.FindControl<BoardView>("ExternalEditorBoard")!;
                Assert.Equal(32, dialog.Editor.Board.Cast<char>().Count(piece => piece != '\0'));
                Assert.Equal(XiangqiGame.InitialFen, PositionSetup.BuildFen(dialog.Editor.Board, true));
                Assert.Equal(flipped, view.Flipped);
                Assert.Equal(0, dialog.FindControl<ComboBox>("ExternalEditorTurn")!.SelectedIndex);
                SaveOpeningScreenshot(dialog, $"external-standard-opening-editor-{(flipped ? "red-top" : "black-top")}-{width}");
                AssertOpeningControlContained(standard, dialog);
                AssertOpeningControlContained(dialog.FindControl<Button>("ExternalEditorAccept")!, dialog);
                // Changing the visual orientation of a generated draft must not
                // replace the draft by re-recognizing the older screenshot.
                dialog.FindControl<Button>("ExternalEditorFlip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(!flipped, view.Flipped);
                Assert.Equal(XiangqiGame.InitialFen, PositionSetup.BuildFen(dialog.Editor.Board, true));
                dialog.FindControl<Button>("ExternalEditorFlip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, reads);
                dialog.Editor.Click(new(4, 6)); dialog.Editor.Click(new(4, 5));
                Assert.Equal('P', dialog.Editor.Board[5, 4]);
                Assert.Equal('\0', dialog.Editor.Board[6, 4]);
                var expected = PositionSetup.BuildFen(dialog.Editor.Board, true);
                dialog.FindControl<Button>("ExternalEditorAccept")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var result = await resultTask;
                Assert.NotNull(result);
                Assert.Equal(expected, result.Fen);
                Assert.Equal(flipped, result.RedAtTop);
                Assert.Equal(flipped, result.Geometry!.RedAtTop);
                Assert.True(result.RedToMove);
            }
            finally { dialog.Close(); owner.Close(); }
            return true;
        }, default);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MainStandardOpeningPausesInputAndPreservesHistoryUntilTheDraftIsAdopted(bool flipped, bool adopt)
    {
        await WithPreflightAsync(flipped, false, async fixture =>
        {
            var window = fixture.Window;
            window.FindControl<Button>("ExternalWorkspaceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await fixture.ConnectAsync();
            var previousFen = Get<XiangqiGame>(window, "_game").CurrentFen();
            var history = Get<ExternalHistoryStore>(window, "_externalHistory");
            await history.FlushAsync();
            window.UpdateLayout();
            var button = window.FindControl<Button>("ExternalStandardOpeningButton")!;
            Assert.True(button.IsVisible); Assert.True(button.IsEnabled);
            Assert.Empty(button.GetVisualAncestors().OfType<Expander>());
            AssertOpeningControlContained(button, window);
            SaveOpeningScreenshot(window, "external-standard-opening-main");
            // Arm and click within this UI turn. The real observation lifetime is
            // cancelled before it can issue input; the fake desktop records attempts.
            Set(window, "_externalRunning", true);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!window.OwnedWindows.OfType<ExternalPositionEditorWindow>().Any() && DateTime.UtcNow < deadline)
                await Task.Delay(15);
            var dialog = Assert.Single(window.OwnedWindows.OfType<ExternalPositionEditorWindow>());
            Assert.False(Get<bool>(window, "_externalRunning"));
            Assert.False(Get<bool>(window, "_externalObserving"));
            Assert.Equal(0, fixture.Desktop.InputCount);
            Assert.Equal(previousFen, Get<XiangqiGame>(window, "_game").CurrentFen());
            Assert.Same(history, Get<ExternalHistoryStore>(window, "_externalHistory"));
            Assert.Equal(XiangqiGame.InitialFen, PositionSetup.BuildFen(dialog.Editor.Board, true));
            Assert.Equal(flipped, dialog.FindControl<BoardView>("ExternalEditorBoard")!.Flipped);
            if (adopt)
            {
                dialog.Editor.Click(new(4, 6)); dialog.Editor.Click(new(4, 5));
                dialog.FindControl<ComboBox>("ExternalEditorTurn")!.SelectedIndex = 1;
                fixture.Desktop.Advance("e3e4");
                dialog.FindControl<Button>("ExternalEditorAccept")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else dialog.FindControl<Button>("ExternalEditorCancel")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline = DateTime.UtcNow.AddSeconds(8);
            while ((!Get<bool>(window, "_externalObserving") || Get<bool>(window, "_externalCalibrating")) && DateTime.UtcNow < deadline)
                await Task.Delay(15);
            Assert.True(Get<bool>(window, "_externalObserving"));
            Assert.False(Get<bool>(window, "_externalRunning"));
            Assert.Equal(0, fixture.Desktop.InputCount);
            Assert.True(File.Exists(history.RecordPath));
            Assert.Equal(adopt ? fixture.Desktop.Game.CurrentFen() : previousFen, Get<XiangqiGame>(window, "_game").CurrentFen());
            if (adopt)
            {
                var events = await ExternalHistoryStore.ReadEventsAsync(history.EventsPath);
                Assert.Contains(events, entry => entry.Kind == "ended" && entry.Fen == previousFen);
                Assert.NotSame(history, Get<ExternalHistoryStore>(window, "_externalHistory"));
            }
            else Assert.Same(history, Get<ExternalHistoryStore>(window, "_externalHistory"));
        });
    }

    [Fact]
    public async Task FlippingStandardDraftCancelsStaleRecognitionBeforeItCanOverwriteTheBoard()
    {
        await Session.Dispatch(async () =>
        {
            var frame = new ExternalFrame(new(883, 883, "draft race fixture", 0, 0, 480, 530),
                ExternalBoardTests.Render(new XiangqiGame(), false));
            var pending = new TaskCompletionSource<SkinRecognition?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken readToken = default;
            var owner = new Window(); owner.Show();
            var dialog = new ExternalPositionEditorWindow(new(XiangqiGame.InitialFen, "标准开局", null,
                frame, new(40, 40, 440, 490, false), false, 0, StandardOpening: true), null,
                (_, _, _, ct, _) => { readToken = ct; return pending.Task; }, default);
            var resultTask = dialog.ShowDialog<ExternalPositionEditResult?>(owner);
            try
            {
                dialog.FindControl<Button>("ExternalEditorRefresh")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(readToken.CanBeCanceled);
                Assert.False(dialog.FindControl<Button>("ExternalEditorAccept")!.IsEnabled);
                dialog.FindControl<Button>("ExternalEditorFlip")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(readToken.IsCancellationRequested);
                Assert.True(dialog.FindControl<Button>("ExternalEditorAccept")!.IsEnabled);
                pending.SetResult(null);
                await Task.Yield();
                Assert.True(dialog.FindControl<BoardView>("ExternalEditorBoard")!.Flipped);
                Assert.Equal(XiangqiGame.InitialFen, PositionSetup.BuildFen(dialog.Editor.Board, true));
                dialog.FindControl<Button>("ExternalEditorAccept")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True((await resultTask)!.RedAtTop);
            }
            finally { pending.TrySetResult(null); dialog.Close(); owner.Close(); }
            return true;
        }, default);
    }

    private static void AssertOpeningControlContained(Control control, Window window)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        Assert.True(point.X >= 0 && point.Y >= 0 && point.X + control.Bounds.Width <= window.ClientSize.Width + 1 &&
            point.Y + control.Bounds.Height <= window.ClientSize.Height + 1, $"{control.Name} must remain visible.");
    }

    private static void SaveOpeningScreenshot(Window window, string name)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui"));
        Directory.CreateDirectory(folder); window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(folder, name + ".png"));
    }
}

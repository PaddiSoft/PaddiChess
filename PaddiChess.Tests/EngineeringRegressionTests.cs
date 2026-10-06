using Avalonia.Controls;
using Avalonia.VisualTree;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;
using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class EngineeringRegressionTests
{
    [Fact]
    public async Task SettingsCoalesceAndFlushTheLatestSnapshotAtomically()
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        var writer = new PreferencesWriter(path, TimeSpan.FromMilliseconds(25));
        try
        {
            for (int i = 0; i < 100; i++) writer.Schedule($"{{\"Level\":{i}}}");
            await writer.FlushAsync();
            Assert.Equal("{\"Level\":99}", await File.ReadAllTextAsync(path));
            Assert.Equal(1, writer.WriteCount);
            Assert.Null(writer.LastError);
            Assert.Single(Directory.GetFiles(folder));
            writer.Schedule("{\"Level\":20}"); await writer.FlushAsync();
            Assert.Equal("{\"Level\":20}", await File.ReadAllTextAsync(path));
            Assert.Equal(2, writer.WriteCount);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task FailedSettingsWriteDoesNotReplaceExistingFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-settings-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var obstacle = Path.Combine(folder, "file-not-directory");
        await File.WriteAllTextAsync(obstacle, "retained");
        try
        {
            var writer = new PreferencesWriter(Path.Combine(obstacle, "settings.json"), TimeSpan.Zero);
            writer.Schedule("{}"); await writer.FlushAsync();
            Assert.IsAssignableFrom<IOException>(writer.LastError);
            Assert.Equal("retained", await File.ReadAllTextAsync(obstacle));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockOnlyRefreshReusesBoardButOneChangedPixelInvalidatesIt(bool flipped)
    {
        var geometry = new BoardCalibration(40, 40, 440, 490, flipped);
        var frame = RawCaptureTests.RawFrame(ExternalBoardTests.Render(new XiangqiGame(), flipped),
            new(1, 1, "fixture", 0, 0, 480, 530), padding: 64);
        var pixels = frame.Pixels!;
        var reader = new ExternalObservationReader(geometry);
        var first = await reader.ReadAsync(frame, default);
        var changed = (byte[])pixels.Bgra.Clone(); changed[0] ^= 0xff; // Outside the board, like a running clock.
        var outside = new ExternalFrame(frame.Window, pixels with { Bgra = changed });
        Assert.Same(first, await reader.ReadAsync(outside, default));
        changed = (byte[])changed.Clone(); changed[(40 * pixels.RowBytes) + 40 * 4] ^= 0xff;
        var inside = new ExternalFrame(frame.Window, pixels with { Bgra = changed });
        Assert.NotSame(first, await reader.ReadAsync(inside, default));
        Assert.False(outside.PngEncoded); Assert.False(inside.PngEncoded);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(inside, cancellation.Token));
    }

    [Fact]
    public async Task EnginePreparationIsReusedWithoutStartingASearchOrExtraHandshake()
    {
        await using var engine = new PikafishClient();
        var settings = new EngineSettings(20, 1, 16, 1, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await engine.PrepareAsync(settings, timeout.Token);
        var ready = engine.ReadyHandshakeCount;
        Assert.Equal(1, engine.ProcessStartCount);
        await engine.PrepareAsync(settings, timeout.Token);
        var result = await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        Assert.Equal(ready, engine.ReadyHandshakeCount);
        Assert.Equal(1, engine.ProcessStartCount);
        Assert.Contains(result.BestMove, new XiangqiGame().AllLegalMoves().Select(m => m.Uci));
        await engine.PrepareAsync(settings with { HashMb = 32 }, timeout.Token);
        Assert.Equal(ready + 1, engine.ReadyHandshakeCount);
    }

    [Fact]
    public async Task ReleasingIdleEngineMemoryStillAllowsANewLocalSearch()
    {
        await using var engine = new PikafishClient();
        var settings = new EngineSettings(20, 1, 16, 1, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await engine.PrepareAsync(settings, timeout.Token);
        var processField = typeof(PikafishClient).GetField("_process",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.NotNull(processField.GetValue(engine));
        await engine.ReleaseResourcesAsync(timeout.Token);
        Assert.Null(processField.GetValue(engine));
        var result = await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        Assert.Equal(2, engine.ProcessStartCount);
        Assert.Contains(result.BestMove, new XiangqiGame().AllLegalMoves().Select(m => m.Uci));
    }

    [Fact]
    public async Task LongHistoryVirtualizesAndRetainsNavigationAndNotes()
    {
        await ExternalSessionTests.Session.Dispatch(() =>
        {
            var game = new XiangqiGame();
            var cycle = new List<ChessMove>();
            foreach (var move in new[] { "b0c2", "b9c7", "c2b0", "c7b9" })
            { Assert.True(game.TryMoveUci(move, out var parsed)); cycle.Add(parsed); }
            var history = Enumerable.Range(0, 2000).Select(i => cycle[i % 4]).ToArray();
            int navigated = -1;
            var model = new MoveHistoryViewModel(ply => navigated = ply);
            model.Update(XiangqiGame.InitialFen, history, 2000, new Dictionary<int, string> { [1999] = "注释" });
            var view = new MoveHistoryView { DataContext = model };
            var host = new Window { Content = view, Width = 300, Height = 400 };
            try
            {
                host.Show(); host.UpdateLayout();
                Assert.Equal(1001, model.Rows.Count);
                Assert.InRange(view.GetVisualDescendants().OfType<Button>().Count(), 1, 60);
                view.ScrollToEnd(); host.UpdateLayout();
                Assert.InRange(view.GetVisualDescendants().OfType<Button>().Count(), 1, 60);
                Assert.Contains("✎", model.Moves[1999].Label);
                model.Moves[1999].Select.Execute(null); Assert.Equal(1999, navigated);
                model.Moves[0].Select.Execute(null); Assert.Equal(0, navigated);
            }
            finally { host.Close(); }
            return true;
        }, default);
    }
}

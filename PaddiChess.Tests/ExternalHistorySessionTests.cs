using Avalonia.Controls;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public partial class ExternalSessionTests
{
    [Fact]
    public async Task TakeoverRecordsDecisionInputConfirmationPauseAndReconnectWithoutOverwriting()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            var first = Get<ExternalHistoryStore>(window, "_externalHistory");
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            Click(window, "ExternalStartButton");
            await WaitPreflightAsync(() => Get<XiangqiGame>(window, "_game").Ply == 1, window);
            Click(window, "ExternalStopButton");
            await Get<Task>(window, "_externalTask");

            var events = await ExternalHistoryStore.ReadEventsAsync(first.EventsPath);
            Assert.Equal("started", events[0].Kind);
            var decision = Assert.Single(events, entry => entry.Kind == "decision");
            var sent = Assert.Single(events, entry => entry.Kind == "sent");
            var confirmed = Assert.Single(events, entry => entry.Kind == "confirmed");
            Assert.Equal(fixture.Desktop.LastInput, decision.Move);
            Assert.Equal(decision.Move, sent.Move);
            Assert.Equal(sent.Move, confirmed.Move);
            Assert.True(decision.Sequence < sent.Sequence && sent.Sequence < confirmed.Sequence);
            Assert.NotEqual(sent.Fen, confirmed.Fen);
            Assert.Equal(sent.Fen, confirmed.BeforeFen);
            Assert.Equal("paused", events[^1].Kind);
            string log, saved;
            using (var logReader = new StreamReader(new FileStream(first.EventsPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite))) log = await logReader.ReadToEndAsync();
            using (var recordReader = new StreamReader(ExternalHistoryStore.OpenRecordRead(first.RecordPath)))
                saved = await recordReader.ReadToEndAsync();
            Assert.DoesNotContain("isolated-dummy-key", log + saved);
            Assert.DoesNotContain("fixture.invalid", log + saved);

            Click(window, "ExternalStartButton");
            await WaitPreflightAsync(() => Get<bool>(window, "_externalObserving"), window);
            Assert.Same(first, Get<ExternalHistoryStore>(window, "_externalHistory"));
            Click(window, "ExternalStopButton");
            await Get<Task>(window, "_externalTask");
            var beforeDisconnect = Get<BoardCalibration>(window, "_externalCalibration");
            Click(window, "ExternalDisconnectButton");
            await WaitPreflightAsync(() => !Get<bool>(window, "_externalLinked") &&
                Get<ExternalHistoryStore?>(window, "_externalHistory") is null, window);
            await Get<Task>(window, "_externalHistoryDrain");
            Assert.DoesNotContain("保存失败", window.FindControl<TextBlock>("BoardFooter")!.Text ?? "");
            events = await ExternalHistoryStore.ReadEventsAsync(first.EventsPath);
            Assert.Contains(events, entry => entry.Kind == "resumed");
            Assert.Equal("ended", events[^1].Kind);
            Assert.Contains("断开", events[^1].Message);

            Set(window, "_externalFrame", await fixture.Desktop.CaptureAsync(fixture.Desktop.Target, default));
            Set(window, "_externalCalibration", beforeDisconnect);
            await fixture.ConnectAsync();
            var next = Get<ExternalHistoryStore>(window, "_externalHistory");
            Assert.NotEqual(first.SessionDirectory, next.SessionDirectory);
            await next.FlushAsync();
            await using var stream = File.OpenRead(first.RecordPath);
            var preserved = await GameRecordStorage.ReadValidatedAsync(stream, default);
            Assert.Equal([fixture.Desktop.LastInput!], preserved.Moves);
            var records = await ExternalHistoryStore.ListAsync(Path.GetDirectoryName(next.SessionDirectory)!);
            Assert.Equal(2, records.Count);
        });
    }

    [Fact]
    public async Task ExplicitPositionReplacementEndsOldHistoryBeforeStartingANewSession()
    {
        await WithPreflightAsync(false, false, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            var first = Get<ExternalHistoryStore>(window, "_externalHistory");
            Click(window, "ExternalStopButton");
            await Get<Task>(window, "_externalTask");
            fixture.Desktop.Advance("h2e2");
            Set(window, "_externalFrame", await fixture.Desktop.CaptureAsync(fixture.Desktop.Target, default));
            await (Task)typeof(MainWindow).GetMethod("ApplyExternalPositionAsync", Private)!
                .Invoke(window, [fixture.Desktop.Game.CurrentFen(), CancellationToken.None])!;
            Assert.Null(Get<ExternalHistoryStore?>(window, "_externalHistory"));
            var events = await ExternalHistoryStore.ReadEventsAsync(first.EventsPath);
            Assert.Equal("ended", events[^1].Kind);
            Assert.Equal(XiangqiGame.InitialFen, events[^1].Fen);
            await fixture.ConnectAsync();
            var next = Get<ExternalHistoryStore>(window, "_externalHistory");
            Assert.NotEqual(first.SessionDirectory, next.SessionDirectory);
            await next.FlushAsync();
            var initial = (await ExternalHistoryStore.ReadEventsAsync(next.EventsPath))[0].Fen;
            Assert.Equal(Get<XiangqiGame>(window, "_game").CurrentFen(), initial);
            Assert.Equal(fixture.Desktop.Game.CurrentFen().Split(' ').Take(2), initial.Split(' ').Take(2));
        });
    }

    [Fact]
    public async Task CandidateEventsDoNotRewriteTheGameAndOverlappingEndsCannotDetachANewSession()
    {
        await WithPreflightAsync(false, false, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            Click(window, "ExternalStopButton");
            await Get<Task>(window, "_externalTask");
            var old = Get<ExternalHistoryStore>(window, "_externalHistory");
            var timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(old.RecordPath, timestamp);
            var record = typeof(MainWindow).GetMethod("RecordExternalHistoryAsync", Private)!;
            foreach (var index in Enumerable.Range(0, 64))
                await (Task)record.Invoke(window, ["observation", "candidate-test-" + index, null,
                    new[] { "h2e2" }, null, null])!;
            await old.FlushAsync();
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(old.RecordPath));
            Assert.Equal(64, (await ExternalHistoryStore.ReadEventsAsync(old.EventsPath))
                .Count(entry => entry.Message.StartsWith("candidate-test-")));

            var end = typeof(MainWindow).GetMethod("EndExternalHistoryAsync", Private)!;
            var firstEnd = (Task)end.Invoke(window, ["disconnect test"] )!;
            Assert.Null(Get<ExternalHistoryStore?>(window, "_externalHistory"));
            var joinedEnd = (Task)end.Invoke(window, ["concurrent shutdown"] )!;
            Assert.Same(firstEnd, joinedEnd);
            await (Task)typeof(MainWindow).GetMethod("BeginExternalHistoryAsync", Private)!
                .Invoke(window, ["new session"] )!;
            var next = Get<ExternalHistoryStore>(window, "_externalHistory");
            await Task.WhenAll(firstEnd, joinedEnd);
            Assert.Same(next, Get<ExternalHistoryStore>(window, "_externalHistory"));
            Assert.Single(await ExternalHistoryStore.ReadEventsAsync(old.EventsPath), entry => entry.Kind == "ended");
        });
    }

    [Fact]
    public async Task WindowShutdownFlushesAndEndsTheActiveHistory()
    {
        await WithPreflightAsync(false, false, async fixture =>
        {
            var window = fixture.Window;
            await fixture.ConnectAsync();
            var history = Get<ExternalHistoryStore>(window, "_externalHistory");
            window.Close();
            await Get<Task>(window, "_cleanupTask");
            Assert.Null(Get<ExternalHistoryStore?>(window, "_externalHistory"));
            var events = await ExternalHistoryStore.ReadEventsAsync(history.EventsPath);
            Assert.Equal("ended", events[^1].Kind);
            Assert.Equal("关闭程序", events[^1].Message);
            Assert.True(File.Exists(history.RecordPath));
        });
    }
}

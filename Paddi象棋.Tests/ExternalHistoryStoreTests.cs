using PaddiXiangqi.Core;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public sealed class ExternalHistoryStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "paddi-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EventsStayOrderedAndQueuedSnapshotsCannotBeChangedByTheProducer()
    {
        await using var store = new ExternalHistoryStore(_folder);
        var record = new GameRecord { Title = "接管测试", Moves = ["h2e2"], CurrentPly = 1 };
        var candidates = new[] { "h9g7" };
        await store.AppendAsync(new ExternalHistoryEvent { Kind = "started", Fen = XiangqiGame.InitialFen }, record);
        await store.AppendAsync(new ExternalHistoryEvent { Kind = "decision", Move = "h9g7", Candidates = candidates }, record);
        record.Moves.Add("a9a0"); record.Title = "later mutation"; candidates[0] = "a9a0";
        for (var index = 0; index < 256; index++)
            await store.AppendAsync(new ExternalHistoryEvent { Kind = "sent", Message = index.ToString() });
        await store.FlushAsync();

        var events = await ExternalHistoryStore.ReadEventsAsync(store.EventsPath);
        Assert.Equal(258, events.Count);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(entry => entry.Sequence));
        Assert.Equal(["h9g7"], events[1].Candidates);
        Assert.Equal("255", events[^1].Message);
        await using var stream = File.OpenRead(store.RecordPath);
        var saved = await GameRecordStorage.ReadValidatedAsync(stream, default);
        Assert.Equal(["h2e2"], saved.Moves);
        Assert.Equal("接管测试", saved.Title);
    }

    [Fact]
    public async Task ReconnectAndResyncKeepPreviousSessionsAndExposeLegacyRecoveryRecords()
    {
        await using (var first = new ExternalHistoryStore(_folder))
            await first.AppendAsync(new ExternalHistoryEvent { Kind = "ended", Message = "重新同步" },
                new GameRecord { Title = "旧局", Moves = ["h2e2"], CurrentPly = 1 });
        await using (var next = new ExternalHistoryStore(_folder))
            await next.AppendAsync(new ExternalHistoryEvent { Kind = "started" }, new GameRecord { Title = "新局" });
        var recovery = Path.Combine(_folder, "Recovery");
        await GameRecordStorage.ArchiveRecoveryAsync(new GameRecord { Title = "旧恢复文件" }, recovery, default);

        var records = await ExternalHistoryStore.ListAsync(_folder, recovery);
        Assert.Equal(3, records.Count);
        Assert.Contains(records, item => item.Title == "旧局" && item.Plies == 1 && !item.Recovery);
        Assert.Contains(records, item => item.Title == "新局" && item.Plies == 0 && !item.Recovery);
        Assert.Contains(records, item => item.Title == "旧恢复文件" && item.Recovery);
        Assert.Equal(3, records.Select(item => item.RecordPath).Distinct().Count());
    }

    [Fact]
    public async Task CorrectionRetainsTheMistakenDecisionAlongsideTheCorrectedReplay()
    {
        await using var store = new ExternalHistoryStore(_folder);
        var mistaken = new XiangqiGame(); Assert.True(mistaken.TryMoveUci("h2e2", out _));
        var corrected = new XiangqiGame(); Assert.True(corrected.TryMoveUci("b2e2", out _));
        await store.AppendAsync(new ExternalHistoryEvent { Kind = "decision", Fen = mistaken.CurrentFen(), Move = "h9g7" },
            new GameRecord { Moves = ["h2e2"], CurrentPly = 1 });
        await store.AppendAsync(new ExternalHistoryEvent { Kind = "corrected", BeforeFen = mistaken.CurrentFen(),
            Fen = corrected.CurrentFen(), ObservedFen = corrected.CurrentFen(), Candidates = ["b2e2"] },
            new GameRecord { Moves = ["b2e2"], CurrentPly = 1 });
        await store.FlushAsync();

        var events = await ExternalHistoryStore.ReadEventsAsync(store.EventsPath);
        Assert.Equal(mistaken.CurrentFen(), events[0].Fen);
        Assert.Equal("h9g7", events[0].Move);
        Assert.Equal(mistaken.CurrentFen(), events[1].BeforeFen);
        Assert.Equal(corrected.CurrentFen(), events[1].ObservedFen);
        await using var stream = File.OpenRead(store.RecordPath);
        Assert.Equal(["b2e2"], (await GameRecordStorage.ReadValidatedAsync(stream, default)).Moves);
    }

    [Fact]
    public async Task IncompleteLastEventDoesNotHideEarlierEvidence()
    {
        string eventsPath;
        await using (var store = new ExternalHistoryStore(_folder))
        {
            eventsPath = store.EventsPath;
            await store.AppendAsync(new ExternalHistoryEvent { Kind = "sent", Move = "h2e2" }, new GameRecord());
        }
        await File.AppendAllTextAsync(eventsPath, "{\"Kind\":\"confirmed\"");
        Assert.Equal("h2e2", Assert.Single(await ExternalHistoryStore.ReadEventsAsync(eventsPath)).Move);
    }

    [Fact]
    public async Task AnOpenReplaySnapshotDoesNotPreventTheNextAtomicCommit()
    {
        await using var store = new ExternalHistoryStore(_folder);
        await store.AppendAsync(new ExternalHistoryEvent { Kind = "started" }, new GameRecord());
        await store.FlushAsync();
        await using var original = ExternalHistoryStore.OpenRecordRead(store.RecordPath);
        await store.AppendAsync(new ExternalHistoryEvent { Kind = "confirmed", Move = "h2e2" },
            new GameRecord { Moves = ["h2e2"], CurrentPly = 1 });
        await store.FlushAsync();
        Assert.Empty((await GameRecordStorage.ReadValidatedAsync(original, default)).Moves);
        await using var current = ExternalHistoryStore.OpenRecordRead(store.RecordPath);
        Assert.Equal(["h2e2"], (await GameRecordStorage.ReadValidatedAsync(current, default)).Moves);
    }

    [Fact]
    public async Task DiskFailureIsReportedWithoutLeavingAFlushWaitingForever()
    {
        Directory.CreateDirectory(_folder);
        var fileRoot = Path.Combine(_folder, "file");
        await File.WriteAllTextAsync(fileRoot, "occupied");
        var store = new ExternalHistoryStore(fileRoot);
        await Assert.ThrowsAnyAsync<Exception>(() => store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<IOException>(() => store.DisposeAsync().AsTask());
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}

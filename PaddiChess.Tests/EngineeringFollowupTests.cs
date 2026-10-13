using System.Text;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;
using PaddiXiangqi.Services;
using PaddiXiangqi.Sessions;

namespace PaddiXiangqi.Tests;

public class EngineeringFollowupTests
{
    [Fact]
    public async Task VoidReturningUiDispatchWaitsForDelayedAssertions()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExternalSessionTests.Session.Dispatch((Func<Task>)(async () =>
            {
                await Task.Delay(30);
                throw new InvalidOperationException("awaited-ui-evidence");
            }), CancellationToken.None));
        Assert.Equal("awaited-ui-evidence", error.Message);
    }

    [Fact]
    public async Task SummaryCanBeReplacedWhileListingReaderIsOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new ExternalHistoryStore(root);
            await store.AppendAsync(new(), new GameRecord { Title = "first" }); await store.FlushAsync();
            using var reader = ExternalHistoryStore.OpenRecordRead(Path.Combine(store.SessionDirectory, "summary.json"));
            await store.AppendAsync(new(), new GameRecord { Title = "second" }); await store.FlushAsync();
            Assert.Equal("second", Assert.Single(await ExternalHistoryStore.ListAsync(root)).Title);
            using var old = new StreamReader(reader);
            Assert.Contains("first", await old.ReadToEndAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PagesRetainOrderAndNeverReadAnUnfinishedTail()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path;
            await using (var store = new ExternalHistoryStore(root))
            {
                path = store.EventsPath;
                for (int i = 0; i < 301; i++) await store.AppendAsync(new() { Message = "事件" + i });
                await store.FlushAsync();
                var first = await ExternalHistoryPaging.ReadAsync(path, 0);
                Assert.Equal(128, first.Events.Count); Assert.True(first.HasNext);
                Assert.Equal(1, first.Events[0].Sequence);
                var last = await ExternalHistoryPaging.ReadAsync(path, 2);
                Assert.Equal(45, last.Events.Count); Assert.False(last.HasNext);
                Assert.Equal(301, last.Events[^1].Sequence);
                Assert.Equal(2, (await ExternalHistoryPaging.ReadLastAsync(path)).Number);
            }
            await File.AppendAllTextAsync(path, "{\"Message\":\"unfinished");
            File.Delete(path + ".idx"); // Legacy JSONL follows the same page contract.
            var page = await ExternalHistoryPaging.ReadAsync(path, 1);
            Assert.Equal(129, page.Events[0].Sequence); Assert.Equal(256, page.Events[^1].Sequence);
            Assert.Equal(45, (await ExternalHistoryPaging.ReadAsync(path, 2)).Events.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CleanupPreservesActiveWriterAndRequiresExplicitPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string folder;
            await using (var store = new ExternalHistoryStore(root))
            {
                folder = store.SessionDirectory;
                await store.AppendAsync(new(), new GameRecord()); await store.FlushAsync();
                File.SetLastWriteTime(store.RecordPath, DateTime.Now.AddDays(-60));
                var plan = await ExternalHistoryPaging.PlanCleanupAsync(root, 30);
                Assert.Single(plan.Directories);
                Assert.Equal(0, await ExternalHistoryPaging.DeleteAsync(plan));
                Assert.True(File.Exists(store.RecordPath));
            }
            Assert.Equal(1, await ExternalHistoryPaging.DeleteAsync(await ExternalHistoryPaging.PlanCleanupAsync(root, 30)));
            Assert.False(Directory.Exists(folder));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CoordinatorCancelsChangedSearchWithoutStartingASecondOne()
    {
        await using var session = new ExternalSessionCoordinator<int, string>(CancellationToken.None);
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Start("first", "config", ct => pending.Task.WaitAsync(ct));
        Assert.Throws<InvalidOperationException>(() => session.Start("next", "config", _ => Task.FromResult(2)));
        session.CancelIfChanged("second", "config");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CollectAsync());
        session.Start("second", "config", _ => Task.FromResult(2));
        Assert.Equal(2, await session.CollectAsync());
        session.SetInputEnabled(true);
        session.InputSubmitted();
        Assert.Contains(session.Metrics.Snapshot(), x => x.Stage == "ready-to-input");
    }

    [Fact]
    public void MetricsUseBoundedRecentSamplesAndExactNearestRankPercentiles()
    {
        var metrics = new SessionLatencyMetrics();
        for (int i = 1; i <= 100; i++) metrics.Record("capture", i);
        metrics.Record("capture", double.NaN);
        var sample = Assert.Single(metrics.Snapshot());
        Assert.Equal(100, sample.Samples); Assert.Equal(50, sample.P50Ms);
        Assert.Equal(95, sample.P95Ms); Assert.Equal(99, sample.P99Ms);
    }

    [Fact]
    public async Task WorkerProtocolRejectsBadLengthsBeforeAllocation()
    {
        using var stream = new MemoryStream(new byte[] {255,255,255,127});
        await Assert.ThrowsAsync<IOException>(() => RecognitionWorkerProtocol.ReadAsync<RecognitionWorkerRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public void UciRuleOptionsDoNotInventAnAllowedMoveQuery()
    {
        var plugin = EnginePlugin.Bundled();
        Assert.True(plugin.Rules.Configurable);
        Assert.False(plugin.Rules.CanQueryAllowedMoves);
        Assert.NotEmpty(plugin.Rules.Options);
        Assert.False(RuleCapabilities.FromUci("generic", []).Configurable);
    }
}

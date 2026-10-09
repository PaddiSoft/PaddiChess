using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Tests;

public class PikafishTests
{
    [Theory]
    [InlineData(9999, true, "红优 9999 分")]
    [InlineData(9999, false, "黑优 9999 分")]
    [InlineData(-25, true, "黑优 25 分")]
    [InlineData(0, false, "均势 0 分")]
    public void RawIntegerScoreIsNotDividedIntoPawnUnits(int value, bool red, string expected)
        => Assert.Equal(expected, AnalysisFormatter.ScoreForPlayers(new(1, 1, value, null, 100, ""), red));

    [Fact]
    public void MateLabelSurvivesRecordSerializationWithoutBecomingAPlotSentinelScore()
    {
        var label = AnalysisFormatter.ScoreForPlayers(new(10, 1, null, -2, 100, ""), false);
        Assert.Equal("红方杀 2", label);
        var record = new GameRecord { Scores = new() { [1] = 1200 }, ScoreLabels = new() { [1] = label } };
        var saved = System.Text.Json.JsonSerializer.Serialize(record);
        Assert.Equal(label, System.Text.Json.JsonSerializer.Deserialize<GameRecord>(saved)!.ScoreLabels[1]);
    }

    [Fact]
    public void UciInfo_ParsesDepthScoreAndPrincipalVariation()
    {
        Assert.True(PikafishClient.TryParseInfo(
            "info depth 12 seldepth 15 multipv 2 score cp 31 nodes 12345 nps 600000 pv h2e2 h9g7", out var info));
        Assert.Equal(12, info.Depth);
        Assert.Equal(2, info.MultiPv);
        Assert.Equal(31, info.Centipawns);
        Assert.Equal(600000, info.Nps);
        Assert.Equal("h2e2", info.FirstMove);
    }

    [Fact]
    public void UciInfo_ParsesPikafishWinDrawLossAndAnalysisLines()
    {
        Assert.True(PikafishClient.TryParseInfo(
            "info depth 5 multipv 1 score cp 22 wdl 61 926 13 nodes 53 pv b2e2", out var info));
        Assert.Equal((61, 926, 13), (info.Wins, info.Draws, info.Losses));
        Assert.Equal(3, new EngineSettings(20, 2, 128, 10, 30) { MultiPvOverride = 3 }.MultiPv);
    }

    [Fact]
    public void AnalysisPresentation_UsesPlayerPerspectiveAndChineseNotation()
    {
        var info = new EngineInfo(12, 1, 25, null, 1000, "h2e2 h9g7");
        Assert.Equal("红优 25 分", AnalysisFormatter.ScoreForPlayers(info, true));
        Assert.Equal("黑优 25 分", AnalysisFormatter.ScoreForPlayers(info, false));
        var variation = AnalysisFormatter.Variation(new XiangqiGame(), info.Pv, 2);
        Assert.Contains("炮二平五", variation);
        Assert.DoesNotContain("h2e2", variation);
    }

    [Fact]
    public async Task BundledEngine_ReturnsLegalMoveFromInitialPosition()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var engineFolder = Path.Combine(root, "Pikafish.2026-09-25");
        var binary = OperatingSystem.IsMacOS() ? "Pikafish-MacOS-universal"
            : OperatingSystem.IsWindows() ? "Pikafish-Windows-x86-64-universal.exe"
            : "Pikafish-Linux-x86-64-universal";
        if (!File.Exists(Path.Combine(engineFolder, binary))) return;
        await using var engine = new PikafishClient
        {
            OverridePath = Path.Combine(engineFolder, binary),
            OverrideEvalPath = Path.Combine(engineFolder, "pikafish.nnue")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var quickSettings = new EngineSettings(0, 1, 16, 1, 12) { MultiPvOverride = 1 };
        var result = await engine.SearchAsync(XiangqiGame.InitialFen, "",
            quickSettings, null, timeout.Token);
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci(result.BestMove, out _), result.BestMove);
        Assert.NotEmpty(result.Candidates);
        Assert.Equal(1, engine.ReadyHandshakeCount);

        using var interrupted = new CancellationTokenSource();
        var searching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = engine.SearchAsync(XiangqiGame.InitialFen, "",
            new EngineSettings(20, 1, 16, 120, 80) { MultiPvOverride = 1 }, _ => searching.TrySetResult(), interrupted.Token);
        // Cancel an actual search, not initialization when CI is under load.
        await searching.Task.WaitAsync(TimeSpan.FromSeconds(10));
        interrupted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        // 0925 declares Skill Level: switching 0 -> 20 must synchronize that
        // option, even though cancellation preserves the same engine process.
        Assert.Equal(2, engine.ReadyHandshakeCount);

        var next = await engine.SearchAsync(XiangqiGame.InitialFen, "",
            quickSettings, null, timeout.Token);
        Assert.True(new XiangqiGame().TryMoveUci(next.BestMove, out _), next.BestMove);
        Assert.Equal(1, engine.ProcessStartCount);
        Assert.Equal(3, engine.ReadyHandshakeCount); // Skill Level 20 -> 0.

        var unchanged = await engine.SearchAsync(XiangqiGame.InitialFen, "",
            quickSettings, null, timeout.Token);
        Assert.True(new XiangqiGame().TryMoveUci(unchanged.BestMove, out _), unchanged.BestMove);
        Assert.Equal(1, engine.ProcessStartCount);
        Assert.Equal(3, engine.ReadyHandshakeCount);
    }

    [Fact]
    public async Task BundledEngine_RespectsAllowedRootMoves()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var engineFolder = Path.Combine(root, "Pikafish.2026-09-25");
        var binary = OperatingSystem.IsMacOS() ? "Pikafish-MacOS-universal"
            : OperatingSystem.IsWindows() ? "Pikafish-Windows-x86-64-universal.exe"
            : "Pikafish-Linux-x86-64-universal";
        if (!File.Exists(Path.Combine(engineFolder, binary))) return;

        var game = new XiangqiGame();
        const string permittedMove = "a3a4";
        Assert.Contains(permittedMove, game.AllLegalMoves().Select(move => move.Uci));
        await using var engine = new PikafishClient
        {
            OverridePath = Path.Combine(engineFolder, binary),
            OverrideEvalPath = Path.Combine(engineFolder, "pikafish.nnue")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var settings = new EngineSettings(0, 1, 16, 1, 12) { MultiPvOverride = 1 };
        var result = await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null,
            timeout.Token, [permittedMove]);
        Assert.Equal(permittedMove, result.BestMove);
        Assert.True(game.TryMoveUci(result.BestMove, out _));
    }

    [Fact]
    public async Task AllowedRootMoves_RejectMalformedUciBeforeStartingEngine()
    {
        await using var engine = new PikafishClient();
        var settings = new EngineSettings(0, 1, 16, 1, 12);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.SearchAsync(
            XiangqiGame.InitialFen, "", settings, null, CancellationToken.None,
            ["a3a4\nquit"]));
        Assert.Equal(0, engine.ProcessStartCount);
    }

    [Fact]
    public async Task Engine_ReusesProcessAndSkipsReadyHandshakeWhenConfigurationIsUnchanged()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var binary = OperatingSystem.IsMacOS() ? "Pikafish-MacOS-universal"
            : OperatingSystem.IsWindows() ? "Pikafish-Windows-x86-64-universal.exe"
            : "Pikafish-Linux-x86-64-universal";
        var folder = Path.Combine(root, "Pikafish.2026-09-25");
        if (!File.Exists(Path.Combine(folder, binary))) return;
        await using var engine = new PikafishClient
        {
            OverridePath = Path.Combine(folder, binary),
            OverrideEvalPath = Path.Combine(folder, "pikafish.nnue")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var settings = new EngineSettings(0, 1, 16, 1, 12) { MultiPvOverride = 1 };
        await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        Assert.Equal(1, engine.ProcessStartCount);
        Assert.Equal(1, engine.ReadyHandshakeCount);

        await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        Assert.Equal(1, engine.ProcessStartCount);
        Assert.Equal(1, engine.ReadyHandshakeCount);

        engine.RequestNewGame();
        await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        Assert.Equal(2, engine.ReadyHandshakeCount);
        engine.RequestClearHash();
        await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        Assert.Equal(3, engine.ReadyHandshakeCount);
    }

    [Fact]
    public async Task Benchmark_ReportsNpsWithoutChangingTheLiveEngineSession()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var binary = OperatingSystem.IsMacOS() ? "Pikafish-MacOS-universal"
            : OperatingSystem.IsWindows() ? "Pikafish-Windows-x86-64-universal.exe"
            : "Pikafish-Linux-x86-64-universal";
        var folder = Path.Combine(root, "Pikafish.2026-09-25");
        if (!File.Exists(Path.Combine(folder, binary))) return;
        await using var engine = new PikafishClient
        {
            OverridePath = Path.Combine(folder, binary),
            OverrideEvalPath = Path.Combine(folder, "pikafish.nnue")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var settings = new EngineSettings(0, 1, 16, 1, 12) { MultiPvOverride = 1 };
        await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        var starts = engine.ProcessStartCount;
        var ready = engine.ReadyHandshakeCount;

        var result = await engine.BenchmarkAsync(threads: 1, hashMb: 16, depth: 5,
            cancellationToken: timeout.Token);
        Assert.Equal(Path.Combine(folder, binary), result.EnginePath);
        Assert.True(result.Nodes > 0);
        Assert.True(result.NodesPerSecond > 0);
        Assert.InRange(result.SearchTimeMs, 0, 30000);
        Assert.Equal(starts, engine.ProcessStartCount);
        Assert.Equal(ready, engine.ReadyHandshakeCount);

        await engine.SearchAsync(XiangqiGame.InitialFen, "", settings, null, timeout.Token);
        Assert.Equal(starts, engine.ProcessStartCount);
        Assert.Equal(ready, engine.ReadyHandshakeCount);
    }
}

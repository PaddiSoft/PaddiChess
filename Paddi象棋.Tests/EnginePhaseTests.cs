using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using System.Diagnostics;
using System.Text.Json;

namespace PaddiXiangqi.Tests;

public class EnginePhaseTests
{
    [Fact]
    public void OpeningBudgetEndsAtConfiguredFullmoveIncludingBlackStarts()
    {
        var profile = new EnginePhaseSettings { OpeningFullmoves = 10 };
        var game = new XiangqiGame();
        game.LoadFen(XiangqiGame.InitialFen.Replace("0 1", "0 10"));
        Assert.Equal(EngineGamePhase.Opening, profile.Phase(game));
        Assert.True(game.TryMoveUci("h2e2", out _));
        Assert.Equal(10, game.FullmoveNumber);
        Assert.True(game.TryMoveUci("h9g7", out _));
        Assert.Equal(11, game.FullmoveNumber);
        Assert.Equal(EngineGamePhase.MiddleGame, profile.Phase(game));
        game.GoToPly(0);
        Assert.Equal(EngineGamePhase.Opening, profile.Phase(game));

        game.LoadFen(XiangqiGame.InitialFen.Replace("w - - 0 1", "b - - 0 10"));
        Assert.True(game.TryMoveUci("h9g7", out _));
        Assert.Equal(11, game.FullmoveNumber);
        Assert.Equal(EngineGamePhase.MiddleGame, profile.Phase(game));
    }

    [Fact]
    public void FirstRedMoveImportedForBlackStillUsesOpeningBudget()
    {
        var profile = new EnginePhaseSettings { Enabled = true };
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        var snapshot = new XiangqiGame(); snapshot.LoadFen(game.CurrentFen());
        Assert.Equal(0, snapshot.Ply);
        Assert.Equal(1, snapshot.FullmoveNumber);
        Assert.False(snapshot.RedToMove);
        Assert.Equal(EngineGamePhase.Opening, profile.Phase(snapshot));
        Assert.Equal(800, profile.Apply(snapshot, new(20, 8, 1024, 2, 80)).MoveTimeMs);
        Assert.True(snapshot.TryMoveUci("h9g7", out _));
        Assert.Equal(EngineGamePhase.MiddleGame,
            new EnginePhaseSettings { OpeningFullmoves = 1 }.Phase(snapshot));
    }

    [Fact]
    public void DevelopedImportedPositionDoesNotUseLocalMoveOneAsOpeningEvidence()
    {
        var game = new XiangqiGame();
        foreach (var move in new[] { "h2e2", "h9g7", "b0c2", "b9c7" })
            Assert.True(game.TryMoveUci(move, out _));
        var snapshot = new XiangqiGame();
        snapshot.LoadFen(game.CurrentFen().Split(' ')[0] + " w - - 0 1");
        var profile = new EnginePhaseSettings { Enabled = true };
        Assert.Equal(EngineGamePhase.MiddleGame, profile.Phase(snapshot));
        Assert.Equal(5000, profile.Apply(snapshot, new(20, 8, 1024, 2, 80)).MoveTimeMs);
    }

    [Fact]
    public void SparsePositionIsEndgameEvenWhenImportedAsMoveOne()
    {
        var game = new XiangqiGame();
        game.LoadFen("3k5/9/9/9/4p4/9/4R4/9/9/4K4 w - - 0 1");
        var profile = new EnginePhaseSettings { Enabled = true, EndgameTimeMs = 9500 };
        var configured = new EngineSettings(20, 6, 1024, 2, 80) { MultiPvOverride = 1 };
        var effective = profile.Apply(game, configured);
        Assert.Equal(EngineGamePhase.Endgame, profile.Phase(game));
        Assert.Equal(9500, effective.MoveTimeMs);
        Assert.Equal((configured.Level, configured.Threads, configured.HashMb, configured.Depth, configured.MultiPv),
            (effective.Level, effective.Threads, effective.HashMb, effective.Depth, effective.MultiPv));
        Assert.Equal(2000, configured.MoveTimeMs);
    }

    [Fact]
    public void DisabledPhasesAndLowerDifficultyKeepTheUserSettings()
    {
        var game = new XiangqiGame();
        var strong = new EngineSettings(20, 4, 512, 2, 30);
        Assert.Same(strong, new EnginePhaseSettings().Apply(game, strong));
        var easy = strong with { Level = 10 };
        Assert.Same(easy, new EnginePhaseSettings { Enabled = true }.Apply(game, easy));
    }

    [Theory]
    [InlineData("rn2k2nr/9/1c5c1/4p4/9/9/4P4/1C5C1/9/RN2K2NR w - - 0 1", 16)]
    [InlineData("rnbakab1r/9/9/p1p1p1p1p/9/9/P1P1P1P1P/9/9/RNBAKAB1R w - - 0 1", 10)]
    public void MissingPawnsOrDefendersDoNotPrematurelyTriggerEndgame(string fen, int material)
    {
        var game = new XiangqiGame(); game.LoadFen(fen);
        var phases = new EnginePhaseSettings { Enabled = true, EndgameMaterialPoints = 6 };
        Assert.Equal(material, EnginePhaseSettings.MaterialPoints(game));
        Assert.Equal(EngineGamePhase.MiddleGame, phases.Phase(game));
        Assert.Equal(phases.MiddleTimeMs, phases.Apply(game, new(20, 1, 16, 12, 30)).MoveTimeMs);
    }

    [Fact]
    public void EndgameRequiresUserMaterialThresholdAndDoesNotDependOnLateRoundNumber()
    {
        var game = new XiangqiGame();
        game.LoadFen("rnbakab1r/9/9/p1p1p1p1p/9/9/P1P1P1P1P/9/9/RNBAKAB1R w - - 0 85");
        Assert.Equal(EngineGamePhase.MiddleGame, new EnginePhaseSettings { EndgameMaterialPoints = 6 }.Phase(game));
        Assert.Equal(EngineGamePhase.Endgame, new EnginePhaseSettings { EndgameMaterialPoints = 10 }.Phase(game));
        Assert.Equal(EngineGamePhase.MiddleGame, new EnginePhaseSettings { EndgameMaterialPoints = 9 }.Phase(game));
    }

    [Fact]
    public void LegacyThresholdAndAllUserTimesSurviveUpgrade()
    {
        var old = JsonSerializer.Deserialize<EnginePhaseSettings>(
            """{"Enabled":true,"EndgameAttackers":4,"OpeningFullmoves":15,"OpeningTimeMs":800,"MiddleTimeMs":3000,"EndgameTimeMs":6000}""")!;
        Assert.Equal(4, old.MaterialThreshold);
        Assert.Equal((15, 800, 3000, 6000), (old.OpeningFullmoves, old.OpeningTimeMs, old.MiddleTimeMs, old.EndgameTimeMs));
        var saved = JsonSerializer.Deserialize<EnginePhaseSettings>(JsonSerializer.Serialize(old with { EndgameMaterialPoints = 7 }))!;
        Assert.Equal(7, saved.MaterialThreshold);
        Assert.Equal(6000, saved.EndgameTimeMs);
        Assert.Equal(new EnginePhaseSettings().MiddleTimeMs, new EnginePhaseSettings().EndgameTimeMs);
    }

    [Fact]
    public async Task RealEngineUsesSubsecondPhaseBudgetInsteadOfUnifiedTwoMinutes()
    {
        var game = new XiangqiGame();
        var profile = new EnginePhaseSettings { Enabled = true, OpeningTimeMs = 250 };
        var settings = profile.Apply(game, new EngineSettings(20, 1, 16, 120, 80));
        await using var engine = new PikafishClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var timer = Stopwatch.StartNew();
        var result = await engine.SearchAsync(game.StartFen, "", settings, null, deadline.Token);
        Assert.True(game.TryMoveUci(result.BestMove, out _));
        Assert.InRange(timer.Elapsed.TotalSeconds, 0, 5);
        Assert.NotEmpty(result.Candidates);
    }
}

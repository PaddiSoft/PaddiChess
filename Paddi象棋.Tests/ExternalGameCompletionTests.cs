using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using Avalonia.Controls;
using Avalonia.Headless;

namespace PaddiXiangqi.Tests;

public class ExternalGameCompletionTests
{
    [Theory]
    [InlineData("3k5/4R4/3R5/9/9/9/9/9/9/4K4 b - - 0 1", GameResult.RedWins)]
    [InlineData("4k4/9/9/9/9/9/9/5r3/3r5/5K3 w - - 0 1", GameResult.BlackWins)]
    public void StableCheckmateNeedsSeparatedConfirmedCaptures(string fen, GameResult winner)
    {
        var game = Load(fen);
        Assert.Equal(winner, game.Result); Assert.True(game.SideInCheck);
        var completion = new ExternalGameCompletion();
        Assert.Null(Observe(completion, game, 0));
        Assert.Null(Observe(completion, game, 400));
        Assert.Null(Observe(completion, game, 749));
        Assert.Equal(winner, Observe(completion, game, 750));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AmbiguousFrameUnknownTurnOrPendingInputRestartsConfirmation(bool confirmed, bool known, bool pending)
    {
        var game = Load("3k5/4R4/3R5/9/9/9/9/9/9/4K4 b - - 0 1");
        var completion = new ExternalGameCompletion();
        Assert.Null(Observe(completion, game, 0));
        Assert.Null(Observe(completion, game, 400));
        Assert.Null(completion.Observe(game, TimeSpan.FromMilliseconds(800), confirmed, known, pending));
        Assert.Null(Observe(completion, game, 900));
        Assert.Null(Observe(completion, game, 1200));
        Assert.Equal(GameResult.RedWins, Observe(completion, game, 1650));
    }

    [Fact]
    public void CaptureInterruptionCannotTurnOldEvidenceIntoCompletion()
    {
        var game = Load("3k5/4R4/3R5/9/9/9/9/9/9/4K4 b - - 0 1");
        var completion = new ExternalGameCompletion();
        Assert.Null(Observe(completion, game, 0));
        Assert.Null(Observe(completion, game, 400));
        Assert.Null(Observe(completion, game, 2000));
        Assert.Null(Observe(completion, game, 2400));
        Assert.Equal(GameResult.RedWins, Observe(completion, game, 2800));
    }

    [Fact]
    public void NewlyConfirmedCheckingMoveFinishesPromptlyButImportedFenDoesNot()
    {
        var game = Load("3k5/4R4/2R6/9/9/9/9/9/9/4K4 w - - 0 1");
        Assert.True(game.TryMoveUci("c7d7", out var move)); Assert.True(move.IsCheck);
        Assert.Equal(GameResult.RedWins, game.Result);
        var completion = new ExternalGameCompletion();
        GameResult? ObserveConfirmed(int milliseconds) => completion.Observe(game, TimeSpan.FromMilliseconds(milliseconds),
            positionConfirmed: true, turnKnown: true, inputPending: false, finalMoveConfirmed: true);
        Assert.Null(ObserveConfirmed(0)); Assert.Null(ObserveConfirmed(75));
        Assert.Equal(GameResult.RedWins, ObserveConfirmed(150));

        var imported = Load(game.CurrentFen()); var importCompletion = new ExternalGameCompletion();
        GameResult? ObserveImported(int milliseconds) => importCompletion.Observe(imported, TimeSpan.FromMilliseconds(milliseconds),
            positionConfirmed: true, turnKnown: true, inputPending: false, finalMoveConfirmed: true);
        Assert.Null(imported.LastMove);
        Assert.Null(ObserveImported(0)); Assert.Null(ObserveImported(75)); Assert.Null(ObserveImported(150));
        Assert.Equal(GameResult.RedWins, ObserveImported(750));
    }

    [Fact]
    public void RepeatedPositionAndDrawAgreementNeverBecomeCheckmate()
    {
        var game = new XiangqiGame { ExternalAdjudication = true };
        string[] cycle = ["b0c2", "b9c7", "c2b0", "c7b9"];
        foreach (var move in cycle.Concat(cycle).Concat(cycle)) Assert.True(game.TryMoveUci(move, out _));
        Assert.Equal(GameResult.Ongoing, game.Result);
        Assert.Equal(game.AllLegalMoves().Count, game.AllControllerLegalMoves().Count);
        var completion = new ExternalGameCompletion();
        foreach (var now in new[] { 0, 400, 800 }) Assert.Null(Observe(completion, game, now));
        Assert.True(game.DeclareDraw()); Assert.Equal(GameResult.Draw, game.Result);
        foreach (var now in new[] { 1200, 1600, 2000 }) Assert.Null(Observe(completion, game, now));
    }

    [Fact]
    public void CheckWithEscapeAndNonCheckStalemateRemainUnderObservation()
    {
        var checking = Load("3k5/9/3R5/9/9/9/9/9/9/5K3 b - - 0 1");
        Assert.True(checking.SideInCheck); Assert.Equal(GameResult.Ongoing, checking.Result);
        var stalemate = Load("3k5/4R4/9/9/9/9/9/9/9/5K3 b - - 0 1");
        Assert.False(stalemate.SideInCheck); Assert.Equal(GameResult.RedWins, stalemate.Result);
        var completion = new ExternalGameCompletion();
        foreach (var game in new[] { checking, stalemate })
        foreach (var now in new[] { 0, 400, 800 }) Assert.Null(Observe(completion, game, now));
    }

    private static XiangqiGame Load(string fen)
    { var game = new XiangqiGame { ExternalAdjudication = true }; game.LoadFen(fen); return game; }
    private static GameResult? Observe(ExternalGameCompletion completion, XiangqiGame game, int milliseconds) =>
        completion.Observe(game, TimeSpan.FromMilliseconds(milliseconds), positionConfirmed: true,
            turnKnown: true, inputPending: false);
}

public partial class ExternalSessionTests
{
    [Theory]
    [InlineData("3k5/4R4/2R6/9/9/9/9/9/9/4K4 w - - 0 1", "c7d7", true, GameResult.RedWins)]
    [InlineData("4k4/9/9/9/9/9/9/6r2/3r5/5K3 b - - 0 1", "g2f2", false, GameResult.BlackWins)]
    public async Task ConfirmedMatingMoveAutomaticallyReleasesTakeoverAndPreservesFinalRecord(
        string fen, string move, bool controlledRed, GameResult winner)
    {
        await Session.Dispatch(async () =>
        {
            var settingsPath = Path.Combine(Path.GetTempPath(), $"paddi-confirmed-mate-{Guid.NewGuid():N}.json");
            var previousSettings = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", settingsPath);
            var window = new MainWindow();
            try
            {
                window.Show();
                window.FindControl<Avalonia.Controls.CheckBox>("AutoAnalyzeCheck")!.IsChecked = false;
                window.FindControl<Avalonia.Controls.ComboBox>("ExternalSideBox")!.SelectedIndex = controlledRed ? 0 : 1;
                var game = Get<XiangqiGame>(window, "_game"); game.ExternalAdjudication = true; game.LoadFen(fen);
                typeof(MainWindow).GetMethod("ShowEngineInfo", Private)!.Invoke(window,
                    [new PaddiXiangqi.Engine.EngineInfo(19, 1, null, 1, 48210, move)]);
                Assert.Equal(GameResult.Ongoing, game.Result);
                var geometry = new BoardCalibration(40, 40, 440, 490, false);
                var baseline = ExternalBoardTests.Render(game);
                var remote = new XiangqiGame { ExternalAdjudication = true }; remote.LoadFen(fen);
                Assert.True(remote.TryMoveUci(move, out _)); Assert.Equal(winner, remote.Result);
                var desktop = new MateObservationDesktop(ExternalBoardTests.Render(remote));
                Set(window, "_externalDesktop", desktop);
                Set(window, "_externalPermissions", new ExternalPermissions(true, true));
                Set(window, "_externalFrame", new ExternalFrame(desktop.Target, baseline));
                Set(window, "_externalCalibration", geometry);
                Set(window, "_externalTracker", new ExternalBoardTracker(BoardObservation.Read(baseline, geometry), game));
                Set(window, "_externalLinked", true); Set(window, "_externalPendingMove", move);

                Click(window, "ExternalStartButton");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (Get<bool>(window, "_externalRunning") && DateTime.UtcNow < deadline) await Task.Delay(20);
                Assert.False(Get<bool>(window, "_externalRunning"), window.FindControl<Avalonia.Controls.TextBlock>("ExternalStatusText")!.Text);
                await Get<Task>(window, "_externalTask");
                Assert.False(Get<bool>(window, "_externalLinked"));
                Assert.True(Get<bool>(window, "_externalCompleted"));
                Assert.Null(Get<ExternalBoardTracker?>(window, "_externalTracker"));
                Assert.Null(Get<string?>(window, "_externalPendingMove"));
                Assert.Equal(winner, game.Result); Assert.Equal(remote.CurrentFen(), game.CurrentFen());
                Assert.Equal(move, Assert.Single(game.History).Uci);
                Assert.Equal(1, desktop.CaptureClosures); Assert.True(desktop.Captures >= 4); Assert.Equal(0, desktop.Inputs);
                Assert.Contains("接管已自动结束", window.FindControl<Avalonia.Controls.TextBlock>("ExternalStatusText")!.Text);
                Assert.Equal("接管下一局", window.FindControl<Avalonia.Controls.Button>("ExternalStartButton")!.Content);
                Assert.True(window.FindControl<Avalonia.Controls.Button>("NewButton")!.IsEnabled);
                Assert.Equal(winner == GameResult.RedWins ? "红方获胜" : "黑方获胜",
                    window.FindControl<TextBlock>("ScoreText")!.Text);
                Assert.Contains("最后一手前的搜索", window.FindControl<TextBlock>("TerminalSearchText")!.Text);
                Assert.Contains("深度 19", window.FindControl<TextBlock>("TerminalSearchText")!.Text);
                Assert.True(window.FindControl<Border>("TerminalAnalysisPanel")!.IsVisible);
                Assert.False(window.FindControl<StackPanel>("BestMovePanel")!.IsVisible);
                Assert.Equal(winner == GameResult.RedWins ? 1200 : -1200, Get<Dictionary<int, double>>(window, "_scores")[1]);
                typeof(MainWindow).GetMethod("NavigateToPly", Private)!.Invoke(window, [0]);
                Assert.False(window.FindControl<Border>("TerminalAnalysisPanel")!.IsVisible);
                Assert.Equal("19", window.FindControl<TextBlock>("DepthText")!.Text);
                Assert.Contains("杀 1", window.FindControl<TextBlock>("ScoreText")!.Text);
                Assert.Null(window.FindControl<AdvantageChart>("Chart")!.ScoreLabelOverride);
                typeof(MainWindow).GetMethod("NavigateToPly", Private)!.Invoke(window, [1]);
                Assert.True(window.FindControl<Border>("TerminalAnalysisPanel")!.IsVisible);
                Click(window, "ExternalWorkspaceButton"); window.UpdateLayout();
                var imageFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui"));
                Directory.CreateDirectory(imageFolder);
                using (var image = window.CaptureRenderedFrame())
                    image?.Save(Path.Combine(imageFolder, controlledRed ? "terminal-result-red.png" : "terminal-result-black.png"));
                var captures = desktop.Captures;
                await Task.Delay(150); Assert.Equal(captures, desktop.Captures);
            }
            finally
            {
                Get<CancellationTokenSource?>(window, "_externalCancellation")?.Cancel();
                window.Close(); await Get<Task>(window, "_cleanupTask");
                Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", previousSettings);
                if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            return true;
        }, default);
    }

    private sealed class MateObservationDesktop(byte[] png) : IExternalDesktop
    {
        public ExternalWindow Target { get; } = new(91, 91, "checkmate observation fixture", 0, 0, 480, 530);
        public int Captures { get; private set; }
        public int CaptureClosures { get; private set; }
        public int Inputs { get; private set; }
        public bool EscapePressed() => false;
        public Task<IReadOnlyList<ExternalWindow>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalWindow>>([Target]);
        public Task<ExternalFrame> CaptureAsync(ExternalWindow target, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Captures++; return Task.FromResult(new ExternalFrame(Target, png) { Sequence = Captures }); }
        public Task CloseCaptureAsync() { CaptureClosures++; return Task.CompletedTask; }
        public Task MoveAsync(ExternalWindow target, double fx, double fy, double tx, double ty, CancellationToken ct)
        { Inputs++; throw new InvalidOperationException("A confirmed terminal board must never issue another move."); }
    }
}

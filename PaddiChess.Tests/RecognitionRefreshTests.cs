using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;

namespace PaddiXiangqi.Tests;

public class RecognitionRefreshTests
{
    [Fact]
    public async Task CachedFullReadChecksLiftedSamplesEvenWhenEveryCoreIsIdentical()
    {
        var game = new XiangqiGame();
        var source = Frame(game);
        source.PrepareLiftedCells([89]);
        using var bitmap = SkiaSharp.SKBitmap.Decode(ExternalBoardTests.Render(game));
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White };
        canvas.DrawRect(434, 462, 12, 6, paint);
        using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var latest = BoardObservation.Read(png.ToArray(), new(40, 40, 440, 490, false));
        for (var i = 0; i < 90; i++) Assert.Equal(source.Cells[i], latest.Cells[i]);
        Assert.False(ExternalRecognitionRefresh.SameSamples(source, latest));
        Assert.True(ExternalRecognitionRefresh.SameSamples(source, Frame(game)));
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None);
        // An uncertain earlier read cannot use an identical core to masquerade
        // as new evidence after its lifted recognition region has changed.
        var read = new SkinRecognition(game.CurrentFen(), [new(8, 9)], .1, null);
        refresh.Seed("history", null, source, read);
        Assert.Null(refresh.Current("history", null, latest));
    }

    private static BoardObservation Frame(XiangqiGame game) => BoardObservation.Read(
        ExternalBoardTests.Render(game), new(40, 40, 440, 490, false));

    [Fact]
    public async Task RefreshDoesNotHideCompletedEvidenceWhileTheNextReadIsRunning()
    {
        var game = new XiangqiGame(); var frame = Frame(game);
        var read = new SkinRecognition(game.CurrentFen(), [], 0, null);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None, TimeSpan.Zero);
        refresh.Seed("history", null, frame, read);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(refresh.TryStart("history", null, frame, TimeSpan.Zero, async ct =>
        { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // The final capture can match the completed read even if the main loop's
        // preceding animation frame scheduled a refresh. It must not wait for it.
        Assert.Same(read, refresh.Current("history", null, Frame(game)));
        Assert.Null(refresh.Current("different history", null, frame));
        Assert.Null(refresh.Current("history", "e3e4", frame));
    }

    [Fact]
    public async Task ANewerConfidentConflictReplacesCompletedEvidence()
    {
        var game = new XiangqiGame(); var frame = Frame(game);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None, TimeSpan.Zero);
        refresh.Seed("history", null, frame, new(game.CurrentFen(), [], 0, null));
        Assert.True(game.TryMoveUci("e3e4", out _));
        var conflict = new SkinRecognition(game.CurrentFen(), [], 0, null);
        Assert.True(refresh.TryStart("history", null, frame, TimeSpan.Zero, _ => Task.FromResult<SkinRecognition?>(conflict)));
        for (var i = 0; i < 100 && refresh.IsRunning; i++) await Task.Delay(5);
        Assert.Same(conflict, refresh.Current("history", null, frame));
    }

    [Fact]
    public async Task TimedOutReadReleasesTheSingleFlightAndRetriesWithoutManualMove()
    {
        var game = new XiangqiGame(); var frame = Frame(game);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        Assert.True(refresh.TryStart("history", null, frame, TimeSpan.Zero, async ct =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }));
        for (var i = 0; i < 100 && refresh.IsRunning; i++) await Task.Delay(5);
        Assert.False(refresh.IsRunning);
        Assert.Null(refresh.Current("history", null, frame));
        Assert.Contains("超时", refresh.LastFailure);
        var read = new SkinRecognition(game.CurrentFen(), [], 0, null);
        Assert.True(refresh.TryStart("history", null, frame, TimeSpan.FromSeconds(1), _ => Task.FromResult<SkinRecognition?>(read)));
        for (var i = 0; i < 100 && refresh.IsRunning; i++) await Task.Delay(5);
        Assert.Same(read, refresh.Current("history", null, frame));
        Assert.Null(refresh.LastFailure);
        Assert.Equal(2, refresh.CompletedAttempts);
    }

    [Fact]
    public async Task ANewerUncertainReadCannotBeHiddenByOldConfidence()
    {
        var game = new XiangqiGame(); var frame = Frame(game);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None, TimeSpan.Zero);
        refresh.Seed("history", null, frame, new(game.CurrentFen(), [], 0, null));
        var uncertain = new SkinRecognition(game.CurrentFen(), [new(0, 0)], .2, null);
        Assert.True(refresh.TryStart("history", null, frame, TimeSpan.Zero, _ => Task.FromResult<SkinRecognition?>(uncertain)));
        for (var i = 0; i < 100 && refresh.IsRunning; i++) await Task.Delay(5);
        Assert.Same(uncertain, refresh.Current("history", null, frame));
    }

    [Fact]
    public async Task RecoveryIsSingleFlightAndReclassifiesChangesWithoutReusingOldHistory()
    {
        var game = new XiangqiGame(); var original = Frame(game);
        var completion = new TaskCompletionSource<SkinRecognition?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None);
        Assert.True(refresh.TryStart("history", "e3e4", original, TimeSpan.Zero, _ => completion.Task));
        Assert.False(refresh.TryStart("history", "e3e4", original, TimeSpan.FromSeconds(3), _ => throw new Exception()));
        Assert.Null(refresh.Current("history", "e3e4", original));
        var read = new SkinRecognition(game.CurrentFen(), [], 0, null);
        completion.SetResult(read);
        for (var i = 0; i < 100 && refresh.Current("history", "e3e4", original) == null; i++) await Task.Delay(5);
        Assert.Same(read, refresh.Current("history", "e3e4", Frame(game)));
        Assert.Null(refresh.Current("new game", "e3e4", original));
        Assert.Null(refresh.Current("history", null, original));
        Assert.True(game.TryMoveUci("e3e4", out _));
        Assert.Equal(game.CurrentFen().Split(' ')[0], refresh.Current("history", "e3e4", Frame(game))?.Fen.Split(' ')[0]);
    }

    [Fact]
    public async Task FailedRecognitionIsRateLimitedAndClosingCancelsPendingWork()
    {
        var frame = Frame(new XiangqiGame());
        var refresh = new ExternalRecognitionRefresh(CancellationToken.None);
        Assert.True(refresh.TryStart("history", null, frame, TimeSpan.Zero, _ => throw new IOException()));
        Assert.Null(refresh.Current("history", null, frame));
        Assert.False(refresh.TryStart("history", null, frame, TimeSpan.FromSeconds(1), _ => throw new Exception()));
        for (var i = 0; i < 100 && refresh.IsRunning; i++) await Task.Delay(5);
        Assert.False(refresh.IsRunning);
        var cancelled = false;
        Assert.True(refresh.TryStart("history", null, frame, TimeSpan.FromSeconds(2), async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled = true; }
            return null;
        }));
        await refresh.DisposeAsync();
        Assert.True(cancelled);
    }

    [Fact]
    public void FullReadRecoversLegalSuccessorButCannotReplaceHistoryWithAnUnrelatedBoard()
    {
        var local = new XiangqiGame();
        var tracker = new ExternalBoardTracker(Frame(local), local);
        var target = new XiangqiGame(); Assert.True(target.TryMoveUci("e3e4", out _));
        var changedTheme = Frame(target);
        // Force incremental visual matching to fail: the target changed its whole
        // palette. The independent full reader still identifies the actual pieces.
        foreach (var cell in changedTheme.Cells)
            for (var i = 0; i < cell.Length; i++) cell[i] = 1 - cell[i];
        Assert.False(tracker.Match(changedTheme, local).Recognized);
        var fullRead = new SkinRecognition(target.CurrentFen(), [], 0, null);
        var session = new ExternalSynchronizationSession();
        var first = session.Observe(changedTheme, tracker, local, null, false, null, null, TimeSpan.Zero, fullRead);
        Assert.False(first.Confirmed);
        Assert.Equal(fullRead.Fen, first.ObservedFen);
        var stable = session.Observe(changedTheme, tracker, local, null, false, null, null,
            TimeSpan.FromMilliseconds(100), fullRead);
        Assert.True(stable.Confirmed); Assert.Equal(new[] { "e3e4" }, stable.Match.Moves);
        Assert.Equal(0, local.Ply); // Only the host can commit the verified successor.
        Assert.True(target.TryMoveUci("e6e5", out _));
        Assert.True(target.TryMoveUci("g3g4", out _));
        var unrelated = new SkinRecognition(target.CurrentFen(), [], 0, null);
        var refused = ExternalSynchronizationSession.Match(changedTheme, tracker, local, null, null, unrelated);
        Assert.False(refused.Recognized);
        Assert.Equal(0, local.Ply);
    }

    [Fact]
    public void ConfidentFullReadSuppliesAMoveEvenWhenPixelDescriptorsLookUnchanged()
    {
        var local = new XiangqiGame();
        var original = Frame(local);
        var tracker = new ExternalBoardTracker(original, local);
        var target = new XiangqiGame();
        Assert.True(target.TryMoveUci("e3e4", out _));
        var observed = Frame(target);
        // Simulate a lossy descriptor match while the independent full reader has
        // correctly identified this frame. This is not a real screenshot fixture.
        for (var i = 0; i < 90; i++) observed.Cells[i] = original.Cells[i];
        var pixel = tracker.Match(observed, local);
        Assert.True(pixel.Recognized);
        Assert.Empty(pixel.Moves);

        var fullRead = new SkinRecognition(target.CurrentFen(), [], 0, null);
        var match = ExternalSynchronizationSession.Match(observed, tracker, local, null, null, fullRead);
        Assert.True(match.Recognized, match.Message);
        Assert.Equal(new[] { "e3e4" }, match.Moves);
        Assert.Equal(0, local.Ply);
    }

    [Fact]
    public void ConfidentUnrelatedBoardVetoesAnOtherwiseSuccessfulPixelMove()
    {
        var local = new XiangqiGame();
        var tracker = new ExternalBoardTracker(Frame(local), local);
        var target = new XiangqiGame();
        Assert.True(target.TryMoveUci("e3e4", out _));
        var observed = Frame(target);
        Assert.Equal(new[] { "e3e4" }, tracker.Match(observed, local).Moves);
        Assert.True(target.TryMoveUci("e6e5", out _));
        Assert.True(target.TryMoveUci("g3g4", out _));
        // Inject a conflicting independent reader result. Three plies cannot be
        // silently reconciled as the single move suggested by the pixel matcher.
        var conflict = new SkinRecognition(target.CurrentFen(), [], 0, null);
        var match = ExternalSynchronizationSession.Match(observed, tracker, local, null, null, conflict);
        Assert.False(match.Recognized);
        Assert.Empty(match.Moves);
    }

    [Fact]
    public void UncertainFullReadDoesNotOverrideAValidatedPixelMove()
    {
        var local = new XiangqiGame();
        var tracker = new ExternalBoardTracker(Frame(local), local);
        var target = new XiangqiGame(); Assert.True(target.TryMoveUci("e3e4", out _));
        var observed = Frame(target);
        var uncertain = new SkinRecognition(local.CurrentFen(), [new Square(4, 5)], .12, null);
        var match = ExternalSynchronizationSession.Match(observed, tracker, local, null, null, uncertain);
        Assert.True(match.Recognized);
        Assert.Equal(new[] { "e3e4" }, match.Moves);
    }

    [Fact]
    public void IndependentSessionSkinVetoesAPixelMoveWhoseBaselineHasWrongUnchangedIdentities()
    {
        var actual = new XiangqiGame();
        var before = Frame(actual);
        var skin = BoardSkin.Learn("independent original identities", before, actual);
        var mistaken = new XiangqiGame();
        // The recorded red horse/cannon identities were swapped, while their pixels
        // stayed unchanged. The pawn move alone still fits that mistaken game legally.
        mistaken.LoadFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1N5C1/9/RCBAKABNR w - - 0 1");
        var tracker = new ExternalBoardTracker(before, mistaken);
        Assert.True(actual.TryMoveUci("e3e4", out _));
        var observed = Frame(actual);
        var pixel = tracker.Match(observed, mistaken);
        Assert.True(pixel.Recognized);
        Assert.Equal(new[] { "e3e4" }, pixel.Moves);
        Assert.True(skin.Recognize(observed, mistaken.RedToMove).Confident);
        Assert.False(ExternalSynchronizationSession.Match(observed, tracker, mistaken, null, skin).Recognized);
        Assert.Equal(0, mistaken.Ply);
    }

    [Fact]
    public void IncompleteSessionVocabularyKeepsTheValidatedPixelFallback()
    {
        var game = new XiangqiGame(); var before = Frame(game);
        var all = BoardSkin.Learn("complete fixture", before, game);
        var incomplete = new BoardSkin("missing king glyph", all.Samples.Where(pair => pair.Key != 'k')
            .ToDictionary(pair => pair.Key, pair => pair.Value));
        var tracker = new ExternalBoardTracker(before, game);
        var target = new XiangqiGame(); Assert.True(target.TryMoveUci("e3e4", out _));
        var observed = Frame(target);
        Assert.False(incomplete.Recognize(observed, game.RedToMove).Confident);
        var match = ExternalSynchronizationSession.Match(observed, tracker, game, null, incomplete);
        Assert.True(match.Recognized);
        Assert.Equal(new[] { "e3e4" }, match.Moves);
    }

    [Fact]
    public void ConfidentFullReadConflictCannotBeBypassedByAnimationCorrection()
    {
        const string startFen = "4k4/9/9/9/4P4/9/9/9/9/2R1K4 w - - 0 1";
        var start = new XiangqiGame(); start.LoadFen(startFen);
        var recovery = new ExternalMoveRecovery(start, Frame(start), ["c0c3"], controlledRed: false);
        var current = new XiangqiGame(); current.LoadFen(startFen);
        Assert.True(current.TryMoveUci("c0c3", out _));
        var tracker = new ExternalBoardTracker(Frame(current), current);
        var target = new XiangqiGame(); target.LoadFen(startFen);
        Assert.True(target.TryMoveUci("c0c8", out _));
        var observed = Frame(target);
        var skin = BoardSkin.Learn("correction fixture", observed, target, requireAllPieces: false);
        Assert.True(recovery.Match(current, observed, [skin]).Recognized);
        Assert.True(target.TryMoveUci("e9f9", out _));
        Assert.True(target.TryMoveUci("e5e6", out _));
        var conflict = new SkinRecognition(target.CurrentFen(), [], 0, null);
        var session = new ExternalSynchronizationSession();
        Assert.False(session.Observe(observed, tracker, current, null, false, skin, recovery,
            TimeSpan.Zero, conflict).Confirmed);
        var repeated = session.Observe(observed, tracker, current, null, false, skin, recovery,
            TimeSpan.FromMilliseconds(100), conflict);
        Assert.False(repeated.Confirmed);
        Assert.Null(repeated.Correction);
        Assert.Equal(0, session.CorrectionCount);
        Assert.Equal(new[] { "c0c3" }, current.AppliedMoves.Select(move => move.Uci));
    }
}

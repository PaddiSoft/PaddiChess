using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;
using SkiaSharp;
using System.Collections.Concurrent;

namespace PaddiXiangqi.Tests;

/// <summary>
/// Three consecutive board crops supplied in the October 5 takeover report.
/// These tests reproduce the visible moves, not the unavailable live animation or engine input.
/// </summary>
public class JjRookCaptureTests
{
    private static readonly ConcurrentDictionary<double, Lazy<(byte[][] Images, BoardCalibration Geometry)>> Reports = new();
    public const string BeforeFen = "2bak3r/4a4/2ncb2c1/rRp1C1n1p/6p2/2P1P4/p5P1P/2N1C1N2/9/2BAKABR1 b - - 0 1";
    public const string AfterPieces = "2bak3r/4a4/2ncb2c1/1rp1C1n1p/6p2/2P1P4/p5P1P/2N1C1N2/9/2BAKABR1";
    public const string HorsePieces = "2bak3r/4a4/2ncb2c1/1rp1C1n1p/6p2/2P1P4/p3N1P1P/4C1N2/9/2BAKABR1";

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void ActualBlackRookCaptureRemovesRedRookAndChangesTurnBeforeReply(double scale)
    {
        var (before, after, _) = Frames(scale);
        var game = BeforeGame();
        var tracker = new ExternalBoardTracker(before, game);
        var match = tracker.Match(after, game);
        Assert.True(match.Recognized, $"{match.Error:P2}: {match.Message}");
        Assert.Equal(new[] { "a6b6" }, match.Moves);
        Assert.True(game.TryMoveUci(match.Moves.Single(), out var capture));
        Assert.Equal('R', capture.Captured);
        Assert.Equal(AfterPieces, game.CurrentFen().Split(' ')[0]);
        Assert.True(game.RedToMove);
        Assert.Contains(game.AllLegalMoves(), move => move.Uci == "e6b6");
        tracker.Accept(after, game);
        var settled = tracker.Match(after, game);
        Assert.True(settled.Recognized);
        Assert.Empty(settled.Moves);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void ActualCaptureAndHorseReplyRecoverBothPliesInOrder(double scale)
    {
        var (before, after, horse) = Frames(scale);
        var game = BeforeGame();
        var tracker = new ExternalBoardTracker(before, game);
        var pair = tracker.Match(horse, game, recoverMissedPair: true);
        Assert.True(pair.Recognized, $"{pair.Error:P2}: {pair.Message}");
        Assert.Equal(new[] { "a6b6", "c2e3" }, pair.Moves);
        Assert.True(game.TryMoveUci("a6b6", out _));
        tracker.Accept(after, game);
        var reply = tracker.Match(horse, game);
        Assert.True(reply.Recognized, reply.Message);
        Assert.Equal(new[] { "c2e3" }, reply.Moves);
        Assert.True(game.TryMoveUci(reply.Moves.Single(), out _));
        Assert.Equal(HorsePieces, game.CurrentFen().Split(' ')[0]);
        Assert.False(game.RedToMove);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void ConfirmedSessionSamplesRecognizeCapturedRookAndCannotAuthorizeADifferentMove(double scale)
    {
        var (before, after, _) = Frames(scale);
        var game = BeforeGame();
        var tracker = new ExternalBoardTracker(before, game);
        var sessionSkin = BoardSkin.Learn("JJ confirmed report position", before, game);
        var complete = sessionSkin.Recognize(after, game.RedToMove);
        Assert.True(complete.Confident, $"{complete.Problem}: {string.Join(',', complete.Uncertain)}");
        Assert.Equal(AfterPieces, complete.Fen.Split(' ')[0]);
        var previousRead = sessionSkin.Recognize(before, game.RedToMove);
        Assert.True(previousRead.Confident);
        var cached = sessionSkin.RecognizeFromPreviousRead(after, game.RedToMove, before, previousRead);
        Assert.True(cached.Confident);
        Assert.Equal(complete.Fen, cached.Fen);
        var recognized = tracker.RecognizeChanges(after, game, new[] { sessionSkin }.Concat(BuiltInBoardSkins.All));
        Assert.NotNull(recognized);
        Assert.True(recognized.Confident, $"{recognized.Problem}: {string.Join(',', recognized.Uncertain)}");
        Assert.Equal(AfterPieces, recognized.Fen.Split(' ')[0]);
        var match = tracker.MatchRecognizedPosition(recognized, game, frame: after);
        Assert.True(match.Recognized, match.Message);
        Assert.Equal(new[] { "a6b6" }, match.Moves);
        Assert.False(tracker.MatchRecognizedPosition(recognized, game, "a6a7", after).Recognized);
        Assert.False(tracker.Match(after, game, "a6a7").Recognized);
        var synchronized = ExternalSynchronizationSession.Match(after, tracker, game, null, sessionSkin);
        Assert.Equal(new[] { "a6b6" }, synchronized.Moves);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SyntheticPartialCaptureFromRealCellsCannotConfirmAMove(int changedFile)
    {
        var (before, after, _) = Frames(1);
        var (partial, _, _) = Frames(1);
        // Synthetic observation only: one endpoint has arrived while the other still
        // has the original pixels. This is not claimed to be a supplied animation frame.
        var index = 3 * 9 + changedFile;
        partial.Cells[index] = after.Cells[index];
        var match = new ExternalBoardTracker(before, BeforeGame()).Match(partial, BeforeGame());
        Assert.False(match.Recognized, $"{match.Error:P2}: {string.Join(',', match.Moves)}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void KnownCannonSubmissionAndImmediateRookCaptureRequireBothPliesAndOpponentConfirmation(double scale)
    {
        var (baseline, _, _) = Frames(scale);
        var (_, after, _) = Frames(scale);
        var game = BeforeCannonGame();
        ReconstructBeforeCannon(baseline);
        var tracker = new ExternalBoardTracker(baseline, game);
        var skin = BoardSkin.Learn("synthetic prior JJ position", baseline, game);
        var session = new ExternalSynchronizationSession();
        var first = session.Observe(after, tracker, game, "e5e6", false, skin, null, TimeSpan.Zero);
        Assert.Equal(new[] { "e5e6", "a6b6" }, first.Match.Moves);
        Assert.False(first.Confirmed);
        var tooSoon = session.Observe(after, tracker, game, "e5e6", false, skin, null, TimeSpan.FromMilliseconds(16));
        Assert.False(tooSoon.Confirmed);
        var confirmed = session.Observe(after, tracker, game, "e5e6", false, skin, null, TimeSpan.FromMilliseconds(90));
        Assert.True(confirmed.Confirmed, confirmed.Status ?? confirmed.Match.Message);
        Assert.Equal(new[] { "e5e6", "a6b6" }, confirmed.Match.Moves);
        foreach (var move in confirmed.Match.Moves) Assert.True(game.TryMoveUci(move, out _));
        Assert.True(game.RedToMove);
        Assert.Equal(AfterPieces, game.CurrentFen().Split(' ')[0]);
        Assert.Equal('R', game.LastMove!.Value.Captured);
    }

    [Fact]
    public void UnknownTurnDoesNotGuessTheOrderOfTwoIndependentVisibleMoves()
    {
        var (baseline, after, _) = Frames();
        var game = BeforeCannonGame();
        ReconstructBeforeCannon(baseline);
        var skin = BoardSkin.Learn("synthetic prior JJ position", baseline, game);
        var observer = new ExternalTurnObserver(baseline, game.CurrentFen());
        // These two moves also form a legal black-first sequence. Without a known
        // submitted cannon move, a final frame alone cannot establish the next side.
        Assert.Null(observer.Observe(after, TimeSpan.Zero, [skin]));
        Assert.Null(observer.Observe(after, TimeSpan.FromMilliseconds(90), [skin]));
        Assert.False(observer.HasCandidate);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void RealRookCaptureNeverConfirmsTheRecordedA6A7Retreat(double scale)
    {
        var (before, after, _) = Frames(scale);
        var game = BeforeGame();
        var tracker = new ExternalBoardTracker(before, game);
        var skin = BoardSkin.Learn("JJ confirmed position", before, game);
        var session = new ExternalSynchronizationSession();
        // The old recovery record explicitly stored a6a7, followed by c2e3.
        // It is perpendicular to the real capture; no supplied image shows that
        // retreat, so this test makes no claim about the unavailable live animation.
        Assert.Contains(game.AllLegalMoves(), move => move.Uci == "a6a7");
        Assert.False(tracker.Match(after, game, "a6a7").Recognized);
        foreach (var time in new[] { 0, 90 })
        {
            var update = session.Observe(after, tracker, game, null, false, skin, null,
                TimeSpan.FromMilliseconds(time));
            Assert.Equal(new[] { "a6b6" }, update.Match.Moves);
            Assert.Equal(time > 0, update.Confirmed);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(.5, false)]
    [InlineData(1, true)]
    [InlineData(.5, true)]
    public void RecordedA6A7BranchCannotAuthorizeInputAndRecoversTheRealCapture(double scale, bool poisonedBaseline)
    {
        var (before, after, _) = Frames(scale);
        var game = BeforeCannonGame();
        Assert.True(game.TryMoveUci("e5e6", out _));
        var skin = BoardSkin.Learn("JJ confirmed position", before, game);
        var recovery = new ExternalMoveRecovery(game, before, ["a6a7"], controlledRed: true);
        Assert.True(game.TryMoveUci("a6a7", out var wrongMove));
        Assert.Equal('\0', wrongMove.Captured);
        Assert.Equal('R', game.Board[3, 1]);
        // Synthetic fault injection, never a claimed fourth real screenshot:
        // either a matching retreat frame was accepted, or the real capture's
        // pixels were accidentally saved against the wrong a6a7 game branch.
        var wrongBaseline = poisonedBaseline ? after : SyntheticRookRetreat(scale);
        var tracker = new ExternalBoardTracker(wrongBaseline, game);
        if (poisonedBaseline)
        {
            var pixelsOnly = tracker.Match(after, game);
            Assert.True(pixelsOnly.Recognized);
            Assert.Empty(pixelsOnly.Moves);
        }
        var independentRead = skin.Recognize(after, game.RedToMove);
        Assert.True(independentRead.Confident);
        Assert.Equal(AfterPieces, independentRead.Fen.Split(' ')[0]);
        // This is the same identity check used immediately before native input.
        // Even a pixel-identical corrupted baseline must not authorize the horse.
        Assert.False(ExternalInputVerification.Check(tracker, game, after, independentRead).Recognized);
        var recovered = recovery.Match(game, after, [skin], independentRead);
        Assert.True(recovered.Recognized, recovered.Message);
        Assert.Equal(new[] { "a6b6" }, recovered.Moves);

        var session = new ExternalSynchronizationSession();
        Assert.False(session.Observe(after, tracker, game, null, false, skin, recovery,
            TimeSpan.Zero, independentRead).Confirmed);
        var corrected = session.Observe(after, tracker, game, null, false, skin, recovery,
            TimeSpan.FromMilliseconds(90), independentRead);
        Assert.True(corrected.Confirmed, corrected.Status ?? corrected.Match.Message);
        Assert.Same(recovery, corrected.Correction);
        Assert.Equal(new[] { "a6b6" }, corrected.Match.Moves);
        game.GoToPly(recovery.StartPly);
        Assert.True(game.TryMoveUci(corrected.Match.Moves.Single(), out var capture));
        Assert.Equal('R', capture.Captured);
        Assert.Equal(new[] { "e5e6", "a6b6" }, game.AppliedMoves.Select(move => move.Uci));
        Assert.Equal(AfterPieces, game.CurrentFen().Split(' ')[0]);
        recovery.Tracker.Accept(after, game);
        var verified = ExternalInputVerification.Check(recovery.Tracker, game, after, independentRead);
        Assert.True(verified.Recognized);
        Assert.Empty(verified.Moves);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void SyntheticA6A7CandidateIsDiscardedWhenTheRealCaptureArrives(double scale)
    {
        var (before, after, _) = Frames(scale);
        var game = BeforeGame();
        var tracker = new ExternalBoardTracker(before, game);
        var skin = BoardSkin.Learn("JJ confirmed position", before, game);
        var session = new ExternalSynchronizationSession();
        var candidate = session.Observe(SyntheticRookRetreat(scale), tracker, game, null, false,
            skin, null, TimeSpan.Zero);
        Assert.False(candidate.Confirmed);
        Assert.Equal(new[] { "a6a7" }, candidate.Match.Moves);
        var arrival = session.Observe(after, tracker, game, null, false, skin, null,
            TimeSpan.FromMilliseconds(90));
        Assert.False(arrival.Confirmed);
        Assert.Equal(new[] { "a6b6" }, arrival.Match.Moves);
        var settled = session.Observe(after, tracker, game, null, false, skin, null,
            TimeSpan.FromMilliseconds(180));
        Assert.True(settled.Confirmed);
        Assert.Equal(new[] { "a6b6" }, settled.Match.Moves);
        Assert.Equal(0, game.Ply);
    }

    [Fact]
    public void RecordedRetreatThenHorseCannotAuthorizeMoreInputOrRewriteAnOwnConfirmedMove()
    {
        var (before, _, horse) = Frames();
        var game = BeforeGame();
        var skin = BoardSkin.Learn("JJ confirmed position", before, game);
        var recovery = new ExternalMoveRecovery(game, before, ["a6a7"], controlledRed: true);
        Assert.True(game.TryMoveUci("a6a7", out _));
        Assert.True(game.TryMoveUci("c2e3", out _));
        // Fault injection after both recorded plies: the safety check still stops
        // input, but bounded opponent correction must not undo our confirmed horse.
        var tracker = new ExternalBoardTracker(horse, game);
        var read = skin.Recognize(horse, game.RedToMove);
        Assert.True(read.Confident);
        Assert.Equal(HorsePieces, read.Fen.Split(' ')[0]);
        Assert.False(ExternalInputVerification.Check(tracker, game, horse, read).Recognized);
        Assert.False(recovery.Match(game, horse, [skin], read).Recognized);
    }

    private static BoardObservation SyntheticRookRetreat(double scale)
    {
        var (synthetic, after, _) = Frames(scale);
        // Move only cell samples: a6 is empty, a7 has the black rook, b6 retains
        // the red rook. This is a manufactured wrong branch, not observed animation.
        synthetic.Cells[2 * 9] = synthetic.Cells[3 * 9];
        synthetic.Cells[3 * 9] = after.Cells[3 * 9];
        return synthetic;
    }

    private static XiangqiGame BeforeCannonGame()
    {
        var game = new XiangqiGame();
        game.LoadFen("2bak3r/4a4/2ncb2c1/rRp3n1p/4C1p2/2P1P4/p5P1P/2N1C1N2/9/2BAKABR1 w - - 0 1");
        return game;
    }

    private static void ReconstructBeforeCannon(BoardObservation baseline)
    {
        // The supplied BEFORE frame's last-move marker is at e5. Reconstruct the
        // preceding sample by swapping e5/e6 patches. All other cells are original.
        // This is a synthetic earlier baseline, not a fourth real screenshot.
        int e5 = 4 * 9 + 4, e6 = 3 * 9 + 4;
        (baseline.Cells[e5], baseline.Cells[e6]) = (baseline.Cells[e6], baseline.Cells[e5]);
    }

    public static XiangqiGame BeforeGame()
    {
        var game = new XiangqiGame();
        game.LoadFen(BeforeFen);
        return game;
    }

    public static (BoardObservation Before, BoardObservation After, BoardObservation Horse) Frames(double scale = 1)
    {
        var report = Reports.GetOrAdd(scale, value => new(() => ReadReport(value))).Value;
        // Observation patches remain independent so synthetic animation tests can
        // replace their own cells without modifying another test's confirmed frame.
        var frames = report.Images.Select(data => BoardObservation.Read(data, report.Geometry)).ToArray();
        return (frames[0], frames[1], frames[2]);
    }

    private static (byte[][] Images, BoardCalibration Geometry) ReadReport(double scale)
    {
        var images = new[] { "before", "after", "horse" }.Select(name =>
        {
            var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"jj-rook-capture-{name}.png"));
            if (scale == 1) return data;
            using var source = SKBitmap.Decode(data);
            using var resized = source.Resize(new SKImageInfo((int)(source.Width * scale), (int)(source.Height * scale)),
                new SKSamplingOptions(SKFilterMode.Linear));
            using var png = resized.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }).ToArray();
        var geometry = BoardLocator.Locate(images[0]);
        Assert.NotNull(geometry);
        return (images, geometry);
    }
}

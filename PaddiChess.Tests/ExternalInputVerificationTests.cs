using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public class ExternalInputVerificationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(.5)]
    public void RealCaptureCannotUseAnUnchangedButWrongPixelBaseline(double scale)
    {
        var (before, after, _) = JjRookCaptureTests.Frames(scale);
        var game = JjRookCaptureTests.BeforeGame();
        var skin = BoardSkin.Learn("confirmed JJ", before, game);
        var read = skin.Recognize(after, game.RedToMove);
        Assert.True(read.Confident, read.Problem + string.Join(',', read.Uncertain));
        Assert.Equal(JjRookCaptureTests.AfterPieces, read.Fen.Split(' ')[0]);
        // Deliberately corrupt the visual baseline as though an earlier capture had
        // been missed. Pixel equality alone would authorize input against the old game.
        var tracker = new ExternalBoardTracker(after, game);
        var pixel = tracker.Match(after, game);
        Assert.True(pixel.Recognized); Assert.Empty(pixel.Moves);
        var verified = ExternalInputVerification.Check(tracker, game, after, read);
        Assert.True(verified.Recognized);
        Assert.Equal(new[] { "a6b6" }, verified.Moves);
        Assert.True(game.TryMoveUci("a6b6", out _));
        tracker.Accept(after, game);
        verified = ExternalInputVerification.Check(tracker, game, after, read);
        Assert.True(verified.Recognized); Assert.Empty(verified.Moves);
    }

    [Fact]
    public void UncertainIdentitiesCannotBeFilledFromThePossiblyIncorrectBaseline()
    {
        var (_, after, _) = JjRookCaptureTests.Frames();
        var game = JjRookCaptureTests.BeforeGame();
        var tracker = new ExternalBoardTracker(after, game);
        var uncertain = new SkinRecognition(game.CurrentFen(), [new Square(1, 3)], .11, null);
        Assert.False(ExternalInputVerification.Check(tracker, game, after, uncertain).Recognized);
        Assert.False(ExternalInputVerification.Check(tracker, game, after, null).Recognized);
    }

    [Fact]
    public async Task PreInputEvidenceReclassifiesChangedFramesAndIsBoundToGameHistory()
    {
        var (before, after, horse) = JjRookCaptureTests.Frames();
        var game = JjRookCaptureTests.BeforeGame();
        var read = BoardSkin.Learn("JJ", before, game).Recognize(after, game.RedToMove);
        Assert.True(read.Confident);
        await using var refresh = new ExternalRecognitionRefresh(default);
        refresh.Seed("before-capture", null, after, read);
        Assert.Same(read, refresh.Current("before-capture", null, after));
        Assert.Null(refresh.Current("another-history", null, after));
        Assert.Null(refresh.Current("before-capture", "c2e3", after));
        var moved = refresh.Current("before-capture", null, horse);
        Assert.NotNull(moved);
        Assert.NotEqual(read.Fen.Split(' ')[0], moved.Fen.Split(' ')[0]);
        var match = ExternalSynchronizationSession.Match(after, new ExternalBoardTracker(before, game), game, null, null,
            refresh.Current("before-capture", null, after));
        Assert.Equal(new[] { "a6b6" }, match.Moves);
    }
}

public partial class ExternalSessionTests
{
    [Fact]
    public async Task CorruptedUnchangedBaselineCannotSendPreparedMoveBeforeMissedPairIsReconciled()
    {
        await WithPreflightAsync(false, true, async fixture =>
        {
            await fixture.ConnectAsync();
            var window = fixture.Window;
            await WaitPreflightAsync(() => Get<object?>(window, "_externalPreparedDecision") is not null, window);
            Assert.Equal(1, fixture.Model!.Count);
            // Fault injection: two moves escaped tracking and their new pixels were
            // accidentally stored against the old game. This must not self-validate.
            fixture.Desktop.Advance("e3e4");
            fixture.Desktop.Advance("h9g7");
            var fresh = await fixture.Desktop.CaptureAsync(fixture.Desktop.Target, default);
            var observation = BoardObservation.Read(fresh, new(40, 40, 440, 490, false));
            var game = Get<XiangqiGame>(window, "_game");
            var wrongTracker = new ExternalBoardTracker(observation, game);
            Assert.Empty(wrongTracker.Match(observation, game).Moves);
            Set(window, "_externalTracker", wrongTracker);
            var history = Get<ExternalHistoryStore>(window, "_externalHistory");

            Click(window, "ExternalStartButton");
            await fixture.Desktop.FirstInput.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Click(window, "ExternalStopButton");
            await Get<Task>(window, "_externalTask");
            await history.FlushAsync();
            var events = await ExternalHistoryStore.ReadEventsAsync(history.EventsPath);
            Assert.Contains(events, entry => (entry.Kind == "blocked" || entry.Kind == "observation") && entry.ObservedFen != null);
            var captureConfirm = Assert.Single(events, entry => entry.Kind == "confirmed" && entry.Move == "h9g7");
            var input = Assert.Single(events, entry => entry.Kind == "sent");
            Assert.True(captureConfirm.Sequence < input.Sequence, "No native input is allowed before both missed plies are confirmed.");
            Assert.Equal(new[] { "e3e4", "h9g7" }, game.History.Take(2).Select(move => move.Uci));
            Assert.Equal(2, fixture.Model.Count);
            Assert.Equal(1, fixture.Desktop.InputCount);
            Assert.Equal(fixture.Model.Requests.Last().Move, fixture.Desktop.LastInput);
        });
    }
}

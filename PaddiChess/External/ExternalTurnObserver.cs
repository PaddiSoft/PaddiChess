using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

public sealed record ExternalTurnMove(bool MoverRed, string Uci, BoardMatch Match);

/// <summary>Waits for a verified external move when a middle-game screenshot has no turn information.</summary>
public sealed class ExternalTurnObserver
{
    private readonly XiangqiGame _red = new() { ExternalAdjudication = true };
    private readonly XiangqiGame _black = new() { ExternalAdjudication = true };
    private readonly ExternalBoardTracker _redTracker;
    private readonly ExternalBoardTracker _blackTracker;
    private readonly ExternalMoveConfirmation _confirmation = new();
    private bool? _candidateRed;
    public bool HasCandidate => _confirmation.HasCandidate;

    public ExternalTurnObserver(BoardObservation baseline, string fen)
    {
        var fields = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2) throw new FormatException("缺少行棋方");
        fields[1] = "w"; _red.LoadFen(string.Join(' ', fields));
        fields[1] = "b"; _black.LoadFen(string.Join(' ', fields));
        _redTracker = new ExternalBoardTracker(baseline, _red);
        _blackTracker = new ExternalBoardTracker(baseline, _black);
    }

    public ExternalTurnMove? Observe(BoardObservation frame, TimeSpan now, IEnumerable<BoardSkin> skins)
    {
        BoardMatch Match(ExternalBoardTracker tracker, XiangqiGame game)
        {
            var pixel = tracker.Match(frame, game);
            if (pixel.Recognized) return pixel;
            pixel = tracker.Match(frame, game, recoverMissedPair: true);
            if (pixel.Recognized) return pixel;
            var recognized = tracker.RecognizeChanges(frame, game, skins);
            return recognized == null ? pixel : tracker.MatchRecognizedPosition(recognized, game, frame: frame);
        }
        var red = Match(_redTracker, _red);
        var black = Match(_blackTracker, _black);
        var redMoved = red.Recognized && red.Moves.Count is 1 or 2;
        var blackMoved = black.Recognized && black.Moves.Count is 1 or 2;
        if (redMoved == blackMoved) { _confirmation.Reset(); _candidateRed = null; return null; }
        var moverRed = redMoved;
        var match = moverRed ? red : black;
        if (_candidateRed != moverRed) _confirmation.Reset();
        _candidateRed = moverRed;
        return _confirmation.Observe(match, frame, now)
            ? new ExternalTurnMove(moverRed, match.Moves[0], match) : null;
    }
}

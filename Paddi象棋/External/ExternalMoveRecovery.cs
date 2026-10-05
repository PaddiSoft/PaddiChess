using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

/// <summary>
/// A bounded checkpoint for correcting an opponent move whose animation was mistaken
/// for a destination. Only the last opponent destination may change; confirmed moves
/// by the controlled side, earlier history and the move's origin are preserved.
/// </summary>
public sealed class ExternalMoveRecovery
{
    private readonly XiangqiGame _before = new();
    private readonly ExternalBoardTracker _tracker;
    private readonly string[] _recorded;
    private readonly string[] _prefix;
    private readonly string? _requiredFirstMove;
    private readonly bool _eligible;
    public int StartPly => _prefix.Length;
    public ExternalBoardTracker Tracker => _tracker;

    public ExternalMoveRecovery(XiangqiGame before, BoardObservation frame,
        IReadOnlyList<string> confirmedMoves, bool controlledRed)
    {
        _prefix = before.AppliedMoves.Select(move => move.Uci).ToArray();
        _recorded = confirmedMoves.ToArray();
        _before.ExternalAdjudication = before.ExternalAdjudication;
        _before.LoadFen(before.StartFen);
        foreach (var move in _prefix)
            if (!_before.TryMoveUci(move, out _)) throw new InvalidOperationException("接管校验点的棋谱无效。");
        _tracker = new(frame, _before);
        if (_recorded.Length is < 1 or > 2) return;
        var lastRed = _recorded.Length == 1 ? _before.RedToMove : !_before.RedToMove;
        _eligible = lastRed != controlledRed;
        if (_recorded.Length == 2) _requiredFirstMove = _recorded[0];
    }

    public BoardMatch Match(XiangqiGame current, BoardObservation frame, IEnumerable<BoardSkin> skins,
        SkinRecognition? recognized = null)
    {
        var missing = new BoardMatch(false, [], 1, "没有可校正的近期落点");
        if (!_eligible || current.StartFen != _before.StartFen || current.Ply != StartPly + _recorded.Length ||
            !current.AppliedMoves.Select(move => move.Uci).SequenceEqual(_prefix.Concat(_recorded))) return missing;
        // Resolve from the confirmed position before the move, never from the incorrect
        // board. This also reconstructs a piece captured at the true destination.
        var match = recognized is { Confident: true }
            ? _tracker.MatchRecognizedPosition(recognized, _before, _requiredFirstMove, frame)
            : _tracker.Match(frame, _before, _requiredFirstMove);
        if (!match.Recognized && recognized?.Confident != true)
        {
            var read = _tracker.RecognizeChanges(frame, _before, skins);
            if (read != null) match = _tracker.MatchRecognizedPosition(read, _before, _requiredFirstMove, frame);
        }
        if (!match.Recognized || match.Moves.Count != _recorded.Length ||
            match.Moves.SequenceEqual(_recorded)) return missing;
        for (int i = 0; i < _recorded.Length - 1; i++)
            if (match.Moves[i] != _recorded[i]) return missing;
        if (match.Moves[^1][..2] != _recorded[^1][..2]) return missing;
        return match with { Message = "已核验对方最终落点，可自动修正动画途经格" };
    }
}

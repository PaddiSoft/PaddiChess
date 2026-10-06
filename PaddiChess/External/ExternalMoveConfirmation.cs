using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

/// <summary>
/// Legal geometry alone cannot distinguish a rook's destination from a square it
/// crosses during animation. Confirm the same move and settled affected cells in
/// separate captures before changing the game or the tracker's visual reference.
/// </summary>
public sealed class ExternalMoveConfirmation
{
    public static readonly TimeSpan MinimumSeparation = TimeSpan.FromMilliseconds(75);
    public static readonly TimeSpan SubmittedMoveSeparation = TimeSpan.FromMilliseconds(16);
    private string? _candidate;
    private BoardObservation? _frame;
    private TimeSpan _since;
    public bool HasCandidate => _candidate != null;

    public void Reset() { _candidate = null; _frame = null; }

    public bool Observe(BoardMatch match, BoardObservation frame, TimeSpan now, string? submittedMove = null)
    {
        if (!match.Recognized || match.Moves.Count == 0) { Reset(); return false; }
        var candidate = string.Join(' ', match.Moves);
        if (_candidate != candidate || _frame == null || !AffectedCellsSettled(_frame, frame, match.Moves))
        {
            _candidate = candidate; _frame = frame; _since = now;
            return false;
        }
        // A submitted endpoint is known before any animation starts. It still needs
        // two settled observations, but cannot be confused with a shorter rook move.
        // An opponent reply (including a two-ply match) retains the longer guard.
        var separation = match.Moves.Count == 1 && match.Moves[0] == submittedMove
            ? SubmittedMoveSeparation : MinimumSeparation;
        return now - _since >= separation;
    }

    private static bool AffectedCellsSettled(BoardObservation before, BoardObservation after, IReadOnlyList<string> moves)
    {
        if (before.Width != after.Width || before.Height != after.Height) return false;
        var empty = new Dictionary<int, bool>();
        foreach (var move in moves)
        {
            if (move.Length != 4 || !Square.TryParseUci(move[..2], out var from) || !Square.TryParseUci(move[2..], out var to)) return false;
            empty[from.Rank * 9 + from.File] = true;
            empty[to.Rank * 9 + to.File] = false;
        }
        foreach (var (index, vacant) in empty)
        {
            var difference = vacant ? BoardObservation.EmptyDistance(before.Cells[index], after.Cells[index])
                : BoardObservation.Distance(before.Cells[index], after.Cells[index]);
            if (!vacant && difference > .012)
                difference = BoardObservation.GlyphMotionDistance(before.Cells[index], after.Cells[index]);
            if (difference > .012) return false;
        }
        return true;
    }
}

using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

/// <summary>
/// A local search result or a single recognized frame cannot end an external game.
/// Require a known turn and repeated confirmations of the same geometrically mated
/// board before releasing takeover. Controller repetition limits are not mate.
/// </summary>
public sealed class ExternalGameCompletion
{
    public static readonly TimeSpan ConfirmationTime = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan ConfirmedMoveTime = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan MaximumObservationGap = TimeSpan.FromSeconds(1);
    private string? _position;
    private TimeSpan _since;
    private TimeSpan _last;
    private int _observations;

    public void Reset() { _position = null; _observations = 0; }

    public GameResult? Observe(XiangqiGame game, TimeSpan now, bool positionConfirmed,
        bool turnKnown, bool inputPending, bool finalMoveConfirmed = false)
    {
        var result = game.Result;
        if (!positionConfirmed || !turnKnown || inputPending ||
            result is not (GameResult.RedWins or GameResult.BlackWins) || !game.SideInCheck)
        { Reset(); return null; }

        // Recognition requires both generals. Do not interpret a missing/misread
        // general, an explicit draw or a non-check stalemate as confirmed checkmate.
        int redKings = 0, blackKings = 0;
        foreach (var piece in game.Board)
        { if (piece == 'K') redKings++; else if (piece == 'k') blackKings++; }
        if (redKings != 1 || blackKings != 1)
        { Reset(); return null; }

        var fields = game.CurrentFen().Split(' ');
        var position = fields[0] + " " + fields[1];
        if (_position != position || now < _last || now - _last > MaximumObservationGap)
        { _position = position; _since = now; _observations = 1; }
        else _observations++;
        _last = now;
        // A newly accepted checking move already passed the move-animation gate.
        // Confirm promptly before the target draws its result overlay. An imported
        // terminal FEN has no such evidence and keeps the longer confirmation.
        var duration = finalMoveConfirmed && game.LastMove is { IsCheck: true }
            ? ConfirmedMoveTime : ConfirmationTime;
        return _observations >= 3 && now - _since >= duration ? result : null;
    }
}

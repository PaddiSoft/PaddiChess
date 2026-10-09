using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Sessions;

/// <summary>Retry only a live submission whose unchanged board was independently verified twice.</summary>
public sealed class ExternalInputRetry
{
    public const int MaximumRetries = 2;
    private TimeSpan _sentAt, _firstUnchanged;
    private int _unchangedFrames;
    public int Retries { get; private set; }
    public void Submitted(TimeSpan now, bool retry = false)
    {
        _sentAt = now; _unchangedFrames = 0;
        if (retry) Retries++; else Retries = 0;
    }
    public void Invalidate() => _unchangedFrames = 0;
    public static bool HasSelectionEvidence(BoardObservation before, BoardObservation current, Square source)
    {
        if (before.Width != current.Width || before.Height != current.Height) return false;
        int index = source.Rank * 9 + source.File;
        // A static/old screenshot is not proof of input failure. Require a visible
        // source-selection response, with other cells unchanged. The caller also
        // independently verifies all identities and limits the retry to destination.
        if (BoardObservation.Distance(before.Cells[index], current.Cells[index]) < .012) return false;
        for (int i = 0; i < 90; i++)
            if (i != index && BoardObservation.Distance(before.Cells[i], current.Cells[i]) > .004) return false;
        return true;
    }
    public bool ObserveUnchanged(TimeSpan now)
    {
        if (_unchangedFrames++ == 0) _firstUnchanged = now;
        return Retries < MaximumRetries && now - _sentAt >= TimeSpan.FromMilliseconds(1500) &&
            _unchangedFrames >= 2 && now - _firstUnchanged >= TimeSpan.FromMilliseconds(120);
    }
}

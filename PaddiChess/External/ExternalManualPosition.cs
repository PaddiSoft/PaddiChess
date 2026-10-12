using PaddiXiangqi.Core;

namespace PaddiXiangqi.External;

/// <summary>User-confirmed labels bound to the reviewed pixels, target and grid.</summary>
public sealed class ExternalManualPosition
{
    private readonly ExternalWindow _target;
    private readonly BoardCalibration _geometry;
    private readonly BoardObservation _source;
    private readonly char[] _pieces;

    public ExternalManualPosition(ExternalFrame frame, BoardCalibration geometry, string fen)
    {
        var game = new XiangqiGame(); game.LoadFen(fen); PositionSetup.Validate(game.Board);
        _target = frame.Window; _geometry = geometry;
        _pieces = game.Board.Cast<char>().ToArray();
        _source = BoardObservation.Read(frame, geometry);
        // Include lifted glyph samples too: matching only an empty core must
        // not hide a selected/animated piece above the intersection.
        _source.PrepareLiftedCells(Enumerable.Range(0, 90));
    }

    public SkinRecognition Apply(ExternalFrame frame, BoardCalibration geometry, SkinRecognition read)
    {
        if (frame.Window.Id != _target.Id || frame.Window.Pid != _target.Pid || geometry != _geometry ||
            read.DetectedRedAtTop is { } direction && direction != geometry.RedAtTop) return read;
        var latest = BoardObservation.Read(frame, geometry);
        if (latest.Width != _source.Width || latest.Height != _source.Height) return read;
        var observed = new XiangqiGame(); observed.LoadFen(read.Fen);
        var pieces = observed.Board.Cast<char>().ToArray();
        var uncertain = read.Uncertain.Select(s => s.Rank * 9 + s.File).ToHashSet();
        var candidates = Enumerable.Range(0, 90).Where(i =>
            (uncertain.Contains(i) || pieces[i] != _pieces[i]) &&
            latest.Cells[i].AsSpan().SequenceEqual(_source.Cells[i])).ToArray();
        latest.PrepareLiftedCells(candidates);
        var changed = false;
        foreach (var i in candidates)
        {
            if (latest.LiftedCell(i).Where((cell, variant) =>
                !cell.AsSpan().SequenceEqual(_source.LiftedCell(i)[variant])).Any()) continue;
            pieces[i] = _pieces[i]; uncertain.Remove(i); changed = true;
        }
        if (!changed) return read;
        // Recheck the resulting whole board. A correction cannot silently make
        // six pawns, duplicate kings, or hide a different square's uncertainty.
        var confidence = Enumerable.Range(0, 90).Select(i => uncertain.Contains(i) ? 0d : 1d).ToArray();
        return new LocalBoardClassifier.Prediction(pieces, confidence, geometry.RedAtTop)
            .ToRecognition(observed.RedToMove, false) with
            { Error = read.Error, DetectedRedAtTop = read.DetectedRedAtTop };
    }
}

using PaddiXiangqi.Sessions;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private ExternalMoveRecovery? _externalMoveRecovery;

    private async Task<(ExternalFrame Frame, BoardCalibration Geometry)> ReadSettledExternalFrameAsync(
        ExternalFrame first, BoardCalibration geometry, CancellationToken ct)
    {
        if (_externalDesktop == null) return (first, geometry);
        var previous = await Task.Run(() => BoardObservation.Read(first, geometry), ct);
        var frame = first;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(ExternalMoveConfirmation.MinimumSeparation, ct);
            var next = await _externalDesktop.CaptureAsync(frame.Window, ct);
            if (!ExternalCaptureGeometry.TryRebaseCalibration(frame, next, geometry, out var nextGeometry))
                throw new InvalidOperationException("同步期间窗口尺寸变化，请重新定位。");
            var observation = await Task.Run(() => BoardObservation.Read(next, nextGeometry), ct);
            if (observation.StableWith(previous)) return (next, nextGeometry);
            previous = observation; frame = next; geometry = nextGeometry;
        }
        throw new InvalidOperationException("棋子仍在移动，尚未采用移动中的局面。请待棋子落定后同步。");
    }

    private async Task CorrectExternalMatchAsync(ExternalMoveRecovery checkpoint, BoardMatch match,
        BoardObservation observation, LlmConnectionSettings? model)
    {
        var beforeFen = _game.CurrentFen();
        await RecordExternalHistoryAsync("correction", "准备修正对方落点", candidates: match.Moves, beforeFen: beforeFen);
        _game.GoToPly(checkpoint.StartPly);
        PruneFutureAnnotations(checkpoint.StartPly);
        _externalTracker = checkpoint.Tracker;
        _externalMoveRecovery = null;
        _moveListDirty = true;
        // Once correction starts, finish replacing the mistaken branch and its visual
        // baseline as one transaction. Stop still prevents any subsequent mouse input.
        await AcceptExternalMatchAsync(match, observation, model, CancellationToken.None);
        await RecordExternalHistoryAsync("corrected", match.Message, candidates: match.Moves, beforeFen: beforeFen);
    }
    // A screenshot confirms moves, including both plies when the opponent replies before
    // the next frame. The FEN supplied by a recognizer does not determine whose turn it is.
    private BoardMatch MatchExternalObservation(BoardObservation observation, string? pending,
        SkinRecognition? recognized = null)
        => ExternalSynchronizationSession.Match(observation, _externalTracker!, _game, pending, _externalSessionSkin, recognized);

    private async Task AcceptExternalMatchAsync(BoardMatch match, BoardObservation observation,
        LlmConnectionSettings? model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (match.Moves.Count > 0)
        {
            var controlledRed = ExternalSideBox.SelectedIndex == 0;
            _externalMoveRecovery = await Task.Run(() => new ExternalMoveRecovery(_game,
                _externalTracker!.ConfirmedFrame, match.Moves, controlledRed), ct);
        }
        ct.ThrowIfCancellationRequested();
        // Once a confirmed sequence starts committing, finish every ply and its
        // baseline even if Stop arrives while a history write is awaiting storage.
        foreach (var uci in match.Moves)
        {
            var beforeFen = _game.CurrentFen();
            SaveCurrentAnnotation(_game.Ply);
            if (!_game.TryMoveUci(uci, out var move)) throw new InvalidOperationException("局面不同步，接管已停止。");
            CommitMove(move);
            QueueExternalScore();
            if (_externalPendingMove == uci)
            {
                if (_externalPendingThought is { } thought)
                {
                    var red = char.IsUpper(move.Piece);
                    RecordLlmThought(_game.Ply, red, model?.Model ?? LlmControllerLabel(red), thought,
                        move.Notation, model?.ReasoningEffort ?? ReasoningValue(LlmControls(red).Reasoning.SelectedIndex));
                }
                _externalPendingMove = null;
                _externalPendingThought = null;
            }
            await RecordExternalHistoryAsync("confirmed", match.Message, move: uci, beforeFen: beforeFen);
        }
        if (match.Moves.Count > 0)
        {
            _externalTurnKnown = true;
            _externalRequestedTurn = null;
            ExternalFenBox.Text = _game.CurrentFen();
        }
        SetExternalTurn(_externalTurnKnown ? _game.RedToMove : null);
        // Finish updating the visual reference even if Stop arrives just after the
        // confirmed game advanced. Resume must never pair a new game with an old baseline.
        if (match.Moves.Count > 0 || match.Message == "整盘棋子一致")
            await Task.Run(() => _externalTracker!.Accept(observation, _game));
        ct.ThrowIfCancellationRequested();
    }
}

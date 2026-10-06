using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Sessions;

public sealed record SynchronizationUpdate(BoardMatch Match, bool Confirmed,
    string? Status = null, ExternalMoveRecovery? Correction = null, string? ObservedFen = null);

/// <summary>
/// Serial observation state for one external connection. Does not dispatch UI work,
/// send input, or mutate the game. The host commits a confirmed update before reading
/// the next frame, so recognition can run off the UI thread without racing game edits.
/// </summary>
public sealed class ExternalSynchronizationSession
{
    private BoardObservation? _previous;
    private readonly ExternalMoveConfirmation _moves = new();
    private readonly ExternalMoveConfirmation _corrections = new();
    private readonly ExternalGameCompletion _completion = new();
    private string? _confirmedMateFen;
    private BoardObservation? _identityFrame;
    private BoardSkin? _identitySkin;
    private SkinRecognition? _identityRead;
    public bool HasCandidate => _moves.HasCandidate || _corrections.HasCandidate;
    public TimeSpan? UnrecognizedSince { get; private set; }
    public int CorrectionCount { get; private set; }

    public void RequireFreshObservation(TimeSpan now)
    { _previous = null; UnrecognizedSince ??= now; _completion.Reset(); }
    public void ResetCompletion() => _completion.Reset();

    public SynchronizationUpdate Observe(BoardObservation observation, ExternalBoardTracker tracker,
        XiangqiGame game, string? pending, bool inputActive, BoardSkin? sessionSkin,
        ExternalMoveRecovery? recovery, TimeSpan now, SkinRecognition? recognized = null)
    {
        var skins = Skins(sessionSkin);
        var settled = _previous != null && observation.SettledWith(_previous);
        // Establish independent identities before any input is armed. Later move
        // confirmation can reuse exactly identical glyph samples from this read.
        if (_identityFrame == null && sessionSkin != null && recognized?.Confident != true)
            recognized = ReadCompletePosition(sessionSkin, observation, game.RedToMove);
        var match = MatchCore(observation, tracker, game, pending, sessionSkin, recognized,
            out var checkedRecognition, ReadCompletePosition);
        if (match.Recognized && match.Moves.Count == 0 && !settled)
        {
            if (_previous == null) match = new(false, [], match.Error, "等待首帧核验");
            else
            {
                var pieces = tracker.RecognizeChanges(observation, game, skins);
                var same = pieces == null ? null : tracker.MatchRecognizedPosition(pieces, game, pending, observation);
                match = same is { Recognized: true, Moves.Count: 0 }
                    ? same : new(false, [], match.Error, "等待画面稳定");
            }
        }
        _previous = observation;
        ExternalMoveRecovery? correction = null;
        if (!match.Recognized)
        {
            _completion.Reset(); _moves.Reset();
            var corrected = recovery != null && pending == null && !inputActive
                ? recovery.Match(game, observation, skins, checkedRecognition) : null;
            if (corrected is { Recognized: true })
            {
                if (!_corrections.Observe(corrected, observation, now))
                    return new(match, false, "正在确认对方的最终落点，确认后自动修正…", ObservedFen: checkedRecognition?.Fen);
                correction = recovery; match = corrected; CorrectionCount++;
            }
            else
            {
                _corrections.Reset(); UnrecognizedSince ??= now;
                return new(match, false, "正在核验棋子落点并自动重试；连接和棋谱已保留。", ObservedFen: checkedRecognition?.Fen);
            }
        }
        else
        {
            _corrections.Reset();
            if (match.Moves.Count > 0 && !_moves.Observe(match, observation, now, pending))
            {
                _completion.Reset();
                return new(match, false, "正在确认最终落点…", ObservedFen: checkedRecognition?.Fen);
            }
        }
        _moves.Reset(); _corrections.Reset(); UnrecognizedSince = null;
        return new(match, true, Correction: correction, ObservedFen: checkedRecognition?.Fen);
    }

    public void OnCommitted(XiangqiGame game, int priorPly)
    {
        if (game.Ply > priorPly && game.LastMove is { IsCheck: true } &&
            game.Result is GameResult.RedWins or GameResult.BlackWins)
            _confirmedMateFen = game.CurrentFen();
        else if (game.Result == GameResult.Ongoing) _confirmedMateFen = null;
    }

    public GameResult? CheckCompletion(XiangqiGame game, TimeSpan now, bool turnKnown, bool inputPending) =>
        _completion.Observe(game, now, positionConfirmed: true, turnKnown, inputPending,
            finalMoveConfirmed: _confirmedMateFen != null && _confirmedMateFen == game.CurrentFen());

    public static BoardMatch Match(BoardObservation observation, ExternalBoardTracker tracker,
        XiangqiGame game, string? pending, BoardSkin? sessionSkin, SkinRecognition? recognized = null)
        => MatchCore(observation, tracker, game, pending, sessionSkin, recognized, out _);

    private static BoardMatch MatchCore(BoardObservation observation, ExternalBoardTracker tracker,
        XiangqiGame game, string? pending, BoardSkin? sessionSkin, SkinRecognition? recognized,
        out SkinRecognition? checkedRecognition,
        Func<BoardSkin, BoardObservation, bool, SkinRecognition>? completeReader = null)
    {
        checkedRecognition = recognized;
        // The caller binds a background full read to these exact frame samples and
        // history. Its confident identities must also constrain an otherwise plausible
        // pixel match; a conflict cannot be hidden by the quick unchanged-board path.
        if (recognized is { Confident: true })
            return tracker.MatchRecognizedPosition(recognized, game, pending, observation);
        var match = tracker.Match(observation, game, pending);
        if (!match.Recognized && pending == null)
            match = tracker.Match(observation, game, null, recoverMissedPair: true);
        if (match is { Recognized: true, Moves.Count: > 0 } && sessionSkin != null)
        {
            // Re-identify all 90 cells before advancing the visual baseline. This
            // check must not borrow identities from the candidate's assumed board.
            var complete = completeReader?.Invoke(sessionSkin, observation, game.RedToMove)
                ?? sessionSkin.Recognize(observation, game.RedToMove);
            checkedRecognition = complete;
            if (complete.Confident)
            {
                return tracker.MatchRecognizedPosition(complete, game, pending, observation);
            }
        }
        if (match.Recognized) return match;
        recognized ??= tracker.RecognizeChanges(observation, game, Skins(sessionSkin));
        checkedRecognition = recognized;
        return recognized == null ? match : tracker.MatchRecognizedPosition(recognized, game, pending, observation);
    }

    private SkinRecognition ReadCompletePosition(BoardSkin skin, BoardObservation frame, bool redToMove)
    {
        // This cache contains independently classified identities, never game.Board.
        var complete = ReferenceEquals(skin, _identitySkin) && _identityRead is { Confident: true } && _identityFrame != null
            ? skin.RecognizeFromPreviousRead(frame, redToMove, _identityFrame, _identityRead)
            : skin.Recognize(frame, redToMove);
        _identityFrame = frame; _identitySkin = skin; _identityRead = complete;
        return complete;
    }

    private static IEnumerable<BoardSkin> Skins(BoardSkin? skin) => skin == null
        ? BuiltInBoardSkins.All : new[] { skin }.Concat(BuiltInBoardSkins.All);
}

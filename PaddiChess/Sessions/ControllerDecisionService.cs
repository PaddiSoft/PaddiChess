using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Sessions;

/// <summary>All position and controller inputs are copied before search starts.</summary>
public sealed record ControllerDecisionRequest(string Fen, string StartFen, string History,
    IReadOnlySet<string> LegalMoves, EngineSettings Settings, bool Bundled,
    LlmConnectionSettings? Model = null);

public sealed record ControllerDecision(string Fen, string Move, LlmMoveResult? Thought,
    SearchResult? Search, NativeRuleMoves? Rules);

/// <summary>
/// Executes a frozen controller request without accessing controls or the live game.
/// Its host owns request cancellation and checks position/configuration identity before
/// displaying progress or committing a result. No model response can bypass native rules.
/// </summary>
public sealed class ControllerDecisionService(PikafishRulesClient rules, LlmChessClient models)
{
    public async Task<ControllerDecision> DecideAsync(ControllerDecisionRequest request,
        PikafishClient engine, Action<EngineInfo>? engineProgress, IProgress<LlmProgress>? modelProgress,
        Action<NativeRuleMoves>? rulesReady, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NativeRuleMoves? allowed = null;
        if (request.Model != null || (request.Bundled && request.Settings.Level < 12))
        {
            allowed = await rules.GetAllowedMovesAsync(request.StartFen, request.History, request.Fen, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (allowed.Allowed.Count == 0)
                throw new InvalidOperationException("皮卡鱼规则校验后没有可选着法，接管已暂停。");
            rulesReady?.Invoke(allowed);
        }
        if (request.Model is { } model)
        {
            var thought = await models.ChooseMoveAsync(request.Fen, allowed!.Allowed, model, ct, modelProgress).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(request.Fen, thought.Move, thought, null, allowed);
        }
        var result = await engine.SearchAsync(request.StartFen, request.History, request.Settings, engineProgress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var move = EngineMoveSelector.Select(result, request.Settings.Level, true, request.Bundled,
            request.LegalMoves, allowed?.Allowed);
        return new(request.Fen, move, null, result, allowed);
    }
}

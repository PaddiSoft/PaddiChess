using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PaddiXiangqi.Core;

using static PaddiXiangqi.Engine.LlmCompletionProtocol;
using static PaddiXiangqi.Engine.LlmCompletionTransport;

namespace PaddiXiangqi.Engine;

/// <summary>Connection details for an OpenAI-compatible chat-completions service.</summary>
public sealed record LlmConnectionSettings(string BaseUrl, string ApiKey, string Model,
    int TimeoutSeconds = 45, double? Temperature = null, bool StreamResponses = false,
    string? ReasoningEffort = null);

/// <summary>
/// Sent means the named parameter was included in the successful request, not that the
/// provider proved it used that level internally. A null Sent value means automatic mode.
/// </summary>
public sealed record LlmReasoningEffortStatus(string? Requested, string? Sent, bool FellBack,
    string? FallbackReason = null);

/// <summary>
/// VisibleThinking contains only text explicitly returned by the provider in a reasoning or
/// summary field. It is null when the provider does not expose such text.
/// </summary>
public sealed record LlmMoveResult(string Move, string? Explanation, string? VisibleThinking = null,
    LlmReasoningEffortStatus? ReasoningEffortStatus = null);

/// <summary>Move, OfferDraw and DeclineDraw carry a legal move. The caller decides when offers are allowed.</summary>
public enum LlmTurnActionKind { Move, OfferDraw, AcceptDraw, DeclineDraw }

public sealed record LlmTurnActionResult(LlmTurnActionKind Kind, string? Move, string? Explanation,
    string? VisibleThinking = null, LlmReasoningEffortStatus? ReasoningEffortStatus = null);

public enum LlmProgressStage { Preparing, Requesting, Reading, Validating, Retrying, Completed }

public sealed record LlmProgress(LlmProgressStage Stage, string Message, int Attempt,
    string? VisibleThinking = null, LlmReasoningEffortStatus? ReasoningEffortStatus = null);

/// <summary>Validates candidate moves and owns decision retries; HTTP and wire parsing are separate.</summary>
public sealed class LlmChessClient
{
    private readonly LlmCompletionTransport _transport;
    public LlmChessClient(HttpClient? httpClient = null) => _transport = new(httpClient);

    public async Task<LlmMoveResult> ChooseMoveAsync(string currentFen, IReadOnlyList<string> legalUciMoves,
        LlmConnectionSettings settings, CancellationToken cancellationToken = default,
        IProgress<LlmProgress>? progress = null)
    {
        var result = await ChooseActionAsync(currentFen, legalUciMoves, settings, TurnRequestMode.MoveOnly,
            null, cancellationToken, progress).ConfigureAwait(false);
        return new LlmMoveResult(result.Move!, result.Explanation, result.VisibleThinking,
            result.ReasoningEffortStatus);
    }

    /// <summary>
    /// Lets the side to move make a legal move, optionally with a draw offer. Set allowDrawOffer=false
    /// while an offer is pending or during an offer cooldown to prevent a repeated-offer loop.
    /// Apply the offered move before asking the opponent to reply on the resulting position.
    /// </summary>
    public Task<LlmTurnActionResult> ChooseTurnActionAsync(string currentFen,
        IReadOnlyList<string> legalUciMoves, LlmConnectionSettings settings, bool allowDrawOffer,
        CancellationToken cancellationToken = default, IProgress<LlmProgress>? progress = null) =>
        ChooseActionAsync(currentFen, legalUciMoves, settings,
            allowDrawOffer ? TurnRequestMode.MoveOrOfferDraw : TurnRequestMode.MoveOnly,
            null, cancellationToken, progress);

    /// <summary>
    /// Asks the opponent to accept a draw or decline it and make a legal move in the same reply.
    /// The caller must provide the opponent's FEN and legal moves after applying the offered move.
    /// A malformed or illegal reply is retried once, then rejected without making a move.
    /// </summary>
    public Task<LlmTurnActionResult> RespondToDrawOfferAsync(string currentFen,
        IReadOnlyList<string> legalUciMoves, LlmConnectionSettings settings,
        string? offerExplanation = null, CancellationToken cancellationToken = default,
        IProgress<LlmProgress>? progress = null) =>
        ChooseActionAsync(currentFen, legalUciMoves, settings, TurnRequestMode.AnswerDrawOffer,
            offerExplanation, cancellationToken, progress);

    private async Task<LlmTurnActionResult> ChooseActionAsync(string currentFen,
        IReadOnlyList<string> legalUciMoves, LlmConnectionSettings settings, TurnRequestMode mode,
        string? offerExplanation, CancellationToken cancellationToken, IProgress<LlmProgress>? progress)
    {
        progress?.Report(new LlmProgress(LlmProgressStage.Preparing, "正在核对棋局和合法着法…", 0));
        ArgumentNullException.ThrowIfNull(legalUciMoves);
        if (string.IsNullOrWhiteSpace(currentFen)) throw new ArgumentException("当前棋局 FEN 不能为空。", nameof(currentFen));
        if (legalUciMoves.Count == 0) throw new InvalidOperationException("当前局面没有合法着法，无法请求大模型走棋。");

        var legal = ValidateLegalMoves(currentFen, legalUciMoves);
        var endpoint = Endpoint(settings, "chat/completions");
        ValidateSettings(settings, requireModel: true);

        var useTools = true;
        var useStreaming = settings.StreamResponses;
        var requestedEffort = NormalizeReasoningEffort(settings.ReasoningEffort);
        // The effort belongs to the player. A provider rejection must be surfaced instead of
        // silently changing the request to automatic mode, including during shape retries.
        LlmReasoningEffortStatus EffortStatus() => new(requestedEffort, requestedEffort, false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompletionMove answer;
            while (true)
            {
                try
                {
                    answer = await _transport.CompleteAsync(endpoint, currentFen, legal.Keys.ToArray(), settings,
                        useTools, useStreaming, requestedEffort, attempt > 0, attempt + 1, progress,
                        EffortStatus(), mode, offerExplanation, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (RequestShapeUnsupportedException) when (useStreaming && useTools)
                {
                    useTools = false;
                    progress?.Report(new LlmProgress(LlmProgressStage.Retrying,
                        "正在尝试流式普通回答格式…", attempt + 1));
                }
                catch (RequestShapeUnsupportedException) when (useStreaming)
                {
                    useStreaming = false;
                    useTools = true;
                    progress?.Report(new LlmProgress(LlmProgressStage.Retrying,
                        "接口未接受流式响应，正在改用普通请求…", attempt + 1));
                }
                catch (RequestShapeUnsupportedException) when (useTools)
                {
                    // Some compatible services implement chat completions without function calling.
                    useTools = false;
                    progress?.Report(new LlmProgress(LlmProgressStage.Retrying,
                        "接口未接受工具调用，正在改用普通回答格式…", attempt + 1));
                }
            }

            if (ValidateAction(answer, mode, legal, out var kind, out var canonical))
            {
                var explanation = SanitizeVisibleText(answer.Explanation, settings.ApiKey, 300);
                var thinking = SanitizeVisibleText(answer.VisibleThinking, settings.ApiKey, 5000);
                progress?.Report(new LlmProgress(LlmProgressStage.Completed,
                    kind switch
                    {
                        LlmTurnActionKind.OfferDraw => "模型已提出和棋。",
                        LlmTurnActionKind.AcceptDraw => "模型已接受和棋。",
                        LlmTurnActionKind.DeclineDraw => "模型已拒绝和棋并选择合法着法。",
                        _ => "模型已选择合法着法。"
                    }, attempt + 1, thinking, EffortStatus()));
                return new LlmTurnActionResult(kind, canonical, explanation, thinking, EffortStatus());
            }
            if (attempt == 0) progress?.Report(new LlmProgress(LlmProgressStage.Retrying,
                "模型未提交有效决定，正在要求重新选择…", attempt + 1));
        }
        throw new InvalidOperationException(mode == TurnRequestMode.MoveOnly
            ? "大模型连续两次未返回合法着法，已停止走棋。请检查模型设置或换用其他模型。"
            : "大模型连续两次未返回有效的走棋或和棋决定，已停止走棋。请检查模型设置或换用其他模型。");
    }

    private static Dictionary<string, string> ValidateLegalMoves(string currentFen,
        IReadOnlyList<string> legalUciMoves)
    {
        var legal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var position = new XiangqiGame();
        position.LoadFen(currentFen);
        var actualLegal = position.AllLegalMoves().Select(move => move.Uci)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var move in legalUciMoves)
        {
            if (!UciMoveRegex.IsMatch(move) || move.Length != 4)
                throw new ArgumentException("合法着法列表包含无效的 UCI 坐标。", nameof(legalUciMoves));
            if (!actualLegal.Contains(move))
                throw new ArgumentException("着法列表包含当前局面不允许的走法。", nameof(legalUciMoves));
            legal[move] = move;
        }
        return legal;
    }

    private static bool ValidateAction(CompletionMove answer, TurnRequestMode mode,
        IReadOnlyDictionary<string, string> legal, out LlmTurnActionKind kind, out string? canonical)
    {
        kind = LlmTurnActionKind.Move;
        canonical = null;
        var action = answer.Action ?? (answer.Move is null ? null : "move");
        if (action == "move" && mode != TurnRequestMode.AnswerDrawOffer
            && answer.Move is { } proposed && legal.TryGetValue(proposed, out var move))
        {
            canonical = move;
            return true;
        }
        if (action == "offer_draw" && mode == TurnRequestMode.MoveOrOfferDraw
            && answer.Move is { } offered && legal.TryGetValue(offered, out move))
        {
            kind = LlmTurnActionKind.OfferDraw;
            canonical = move;
            return true;
        }
        if (action == "accept_draw" && mode == TurnRequestMode.AnswerDrawOffer && answer.Move is null)
        {
            kind = LlmTurnActionKind.AcceptDraw;
            return true;
        }
        if (action == "decline_draw" && mode == TurnRequestMode.AnswerDrawOffer
            && answer.Move is { } reply && legal.TryGetValue(reply, out move))
        {
            kind = LlmTurnActionKind.DeclineDraw;
            canonical = move;
            return true;
        }
        return false;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(LlmConnectionSettings settings, CancellationToken cancellationToken = default) =>
        _transport.ListModelsAsync(settings, cancellationToken);
}

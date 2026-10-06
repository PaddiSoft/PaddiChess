using System.Threading.Channels;
using Avalonia.Threading;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private sealed record LlmAnalysisRequest(long Generation, int Ply, string Fen, string StartFen,
        string UciMoves, bool RedToMove, string EnginePath, string EvalFilePath, EngineSettings Settings,
        IReadOnlyList<string> AllowedRootMoves, IReadOnlyDictionary<string, string> RuleOptions);

    private sealed record LlmEvaluation(string Fen, bool RedToMove, EngineInfo Info,
        IReadOnlyList<EngineInfo> Candidates);

    private readonly Dictionary<int, List<LlmThoughtRecord>> _llmThoughtHistory = [];
    private readonly Dictionary<int, LlmEvaluation> _llmEvaluationHistory = [];
    private readonly HashSet<string> _queuedLlmAnalysis = [];
    private readonly List<Task> _retiredLlmAnalysisWorkers = [];
    private Channel<LlmAnalysisRequest>? _llmAnalysisQueue;
    private CancellationTokenSource? _llmAnalysisCancellation;
    private Task? _llmAnalysisTask;
    private long _llmAnalysisGeneration;

    /// <summary>
    /// Scores a snapshot of the current position on a separate, single-threaded Pikafish
    /// process. Model requests and the main engine's move search never wait for this work.
    /// </summary>
    private void QueueLlmPositionAnalysis()
    {
        if (_closing || _setupBoard is not null || (!_redLlm && !_blackLlm) ||
            (!_autoAnalyze && !_analysisMode) || _game.Result != GameResult.Ongoing) return;
        QueueLlmAnalysisSnapshot(_game, _analysisMode
            ? Math.Clamp((int)(AnalysisLinesBox.Value ?? 3), 1, 5) : 1);
    }

    private void RefreshLlmAnalysisParticipation()
    {
        if (_closing || (!_redLlm && !_blackLlm) || (!_autoAnalyze && !_analysisMode))
            StopLlmInsightWorker();
        else
            QueueLlmHistoricalAnalysis();
    }

    /// <summary>When analysis is enabled halfway through a match, fill earlier chart points.</summary>
    private void QueueLlmHistoricalAnalysis()
    {
        QueueLlmPositionAnalysis();
        if (_game.Ply == 0 || (!_redLlm && !_blackLlm) ||
            (!_autoAnalyze && !_analysisMode)) return;

        var replay = new XiangqiGame { ExternalAdjudication = _game.ExternalAdjudication };
        replay.LoadFen(_game.StartFen);
        var positions = new List<(string Fen, string Moves, int Ply, bool RedToMove, string[] Allowed)>();
        positions.Add((replay.CurrentFen(), replay.UciMoveList, 0, replay.RedToMove,
            replay.AllLegalMoves().Select(move => move.Uci).ToArray()));
        for (var i = 0; i < _game.Ply - 1; i++)
        {
            if (!replay.TryMoveUci(_game.History[i].Uci, out _)) break;
            positions.Add((replay.CurrentFen(), replay.UciMoveList, replay.Ply, replay.RedToMove,
                replay.AllLegalMoves().Select(move => move.Uci).ToArray()));
        }
        // Recent moves appear on screen first; score them before older positions.
        foreach (var position in positions.AsEnumerable().Reverse())
            QueueLlmAnalysisSnapshot(position.Ply, position.Fen, _game.StartFen,
                position.Moves, position.RedToMove, 1, position.Allowed);
    }

    private void QueueLlmAnalysisSnapshot(XiangqiGame position, int lines) =>
        QueueLlmAnalysisSnapshot(position.Ply, position.CurrentFen(), position.StartFen,
            position.UciMoveList, position.RedToMove, lines,
            position.AllLegalMoves().Select(move => move.Uci).ToArray());

    private void QueueLlmAnalysisSnapshot(int ply, string fen, string startFen,
        string moves, bool redToMove, int lines, IReadOnlyList<string> allowedRootMoves)
    {
        if (_closing || _setupBoard is not null) return;

        var key = $"{ply}:{fen}:{lines}";
        if (!_queuedLlmAnalysis.Add(key)) return;

        var configured = ReadEngineSettings();
        // Keep the score pass bounded so two hosted models can continue playing while
        // local analysis runs. It still uses the user's engine binary and eval file.
        var settings = new EngineSettings(20, 1, Math.Clamp(configured.HashMb, 16, 64), 1,
            Math.Clamp(configured.Depth, 8, 24)) { MultiPvOverride = lines };
        EnsureLlmAnalysisWorker();
        var request = new LlmAnalysisRequest(_llmAnalysisGeneration, ply, fen, startFen,
            moves, redToMove, _engine.OverridePath ?? "", _engine.OverrideEvalPath ?? "", settings, allowedRootMoves,
            new Dictionary<string, string>(_engine.OverrideRuleOptions, StringComparer.OrdinalIgnoreCase));
        _llmAnalysisQueue?.Writer.TryWrite(request);
        if (_game.Ply == ply)
            EngineStatusText.Text = $"{DefaultEngine.Name} 正在后台评分第 {ply} 手…";
    }

    private void EnsureLlmAnalysisWorker()
    {
        if (_llmAnalysisQueue is not null) return;
        var cancellation = new CancellationTokenSource();
        var queue = Channel.CreateUnbounded<LlmAnalysisRequest>(new UnboundedChannelOptions
        { SingleReader = true, SingleWriter = true });
        _llmAnalysisCancellation = cancellation;
        _llmAnalysisQueue = queue;
        _llmAnalysisTask = Task.Run(() => RunLlmAnalysisWorkerAsync(queue.Reader, cancellation));
    }

    private async Task RunLlmAnalysisWorkerAsync(ChannelReader<LlmAnalysisRequest> reader,
        CancellationTokenSource lifetime)
    {
        PikafishClient? engine = null;
        string? enginePath = null;
        string? evalFilePath = null;
        try
        {
            await foreach (var request in reader.ReadAllAsync(lifetime.Token))
            {
                if (request.Generation != Interlocked.Read(ref _llmAnalysisGeneration)) continue;
                try
                {
                    if (engine is null || enginePath != request.EnginePath || evalFilePath != request.EvalFilePath ||
                        !engine.OverrideRuleOptions.OrderBy(item => item.Key).SequenceEqual(request.RuleOptions.OrderBy(item => item.Key)))
                    {
                        if (engine is not null) await engine.DisposeAsync();
                        engine = new PikafishClient { OverridePath = request.EnginePath, OverrideEvalPath = request.EvalFilePath,
                            OverrideRuleOptions = request.RuleOptions };
                        enginePath = request.EnginePath;
                        evalFilePath = request.EvalFilePath;
                    }

                    EngineInfo? latest = null;
                    var result = await engine.SearchAsync(request.StartFen, request.UciMoves,
                        request.Settings, info =>
                        {
                            if (info.MultiPv == 1 && (latest is null || info.Depth >= latest.Depth))
                                latest = info;
                        }, lifetime.Token, request.AllowedRootMoves);
                    var mainInfo = result.Candidates.FirstOrDefault(info => info.MultiPv == 1) ?? latest;
                    if (mainInfo is null) continue;
                    var engineName = engine.EngineName;
                    Dispatcher.UIThread.Post(() =>
                        ApplyLlmAnalysis(request, mainInfo, result.Candidates, engineName),
                        DispatcherPriority.Background);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // An unavailable custom engine must not stop either hosted model.
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (request.Generation != _llmAnalysisGeneration || _closing) return;
                        _queuedLlmAnalysis.Remove($"{request.Ply}:{request.Fen}:{request.Settings.MultiPv}");
                        if (_game.Ply == request.Ply && _game.CurrentFen() == request.Fen)
                            EngineStatusText.Text = $"后台评分失败：{ex.Message}";
                    }, DispatcherPriority.Background);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            if (engine is not null) await engine.DisposeAsync();
            lifetime.Dispose();
        }
    }

    private void ApplyLlmAnalysis(LlmAnalysisRequest request, EngineInfo info,
        IReadOnlyList<EngineInfo> candidates, string engineName)
    {
        if (_closing || request.Generation != _llmAnalysisGeneration) return;
        _llmEvaluationHistory[request.Ply] = new LlmEvaluation(request.Fen,
            request.RedToMove, info, candidates);
        UpdateScore(request.Ply, request.RedToMove, info);
        if (_game.Ply != request.Ply || _game.CurrentFen() != request.Fen || _busy) return;
        ShowEngineInfo(info);
        if (ShowGuidance) ShowVariations(candidates);
        EngineStatusText.Text = $"{engineName} · 第 {request.Ply} 手评分完成";
    }

    /// <summary>Save only text the model service actually returned, keyed by the resulting ply.</summary>
    private void RecordLlmThought(int resultingPly, bool red, string model,
        LlmMoveResult reply, string notation, string requestedEffort)
        => RecordLlmThought(resultingPly, red, model, reply.Move, reply.Explanation,
            reply.VisibleThinking, reply.ReasoningEffortStatus, notation, requestedEffort, "走棋");

    private void RecordLlmThought(int resultingPly, bool red, string model,
        LlmTurnActionResult reply, string notation, string requestedEffort)
        => RecordLlmThought(resultingPly, red, model, reply.Move ?? "", reply.Explanation,
            reply.VisibleThinking, reply.ReasoningEffortStatus, notation, requestedEffort,
            reply.Kind switch
            {
                LlmTurnActionKind.OfferDraw => "提出和棋",
                LlmTurnActionKind.AcceptDraw => "接受和棋",
                LlmTurnActionKind.DeclineDraw => "拒绝和棋并走棋",
                _ => "走棋"
            });

    private void RecordLlmThought(int resultingPly, bool red, string model, string uciMove,
        string? explanation, string? visibleThinking, LlmReasoningEffortStatus? status,
        string notation, string requestedEffort, string action)
    {
        if (!_llmThoughtHistory.TryGetValue(resultingPly, out var entries))
        {
            entries = [];
            _llmThoughtHistory[resultingPly] = entries;
        }
        entries.Add(new LlmThoughtRecord
        {
            Ply = resultingPly,
            Side = red ? "红方" : "黑方",
            Model = model,
            MoveNotation = notation,
            UciMove = uciMove,
            Action = action,
            RequestedReasoningEffort = requestedEffort,
            SentReasoningEffort = status?.Sent,
            ReasoningFellBack = status?.FellBack == true,
            ReasoningFallbackReason = status?.FallbackReason,
            VisibleThinking = visibleThinking,
            Explanation = explanation,
            RecordedAt = DateTimeOffset.UtcNow
        });
        if (_game.Ply == resultingPly) ShowLlmInsightsForPly(resultingPly);
    }

    private void ShowLlmInsightsForPly(int ply)
    {
        if (_llmThoughtHistory.TryGetValue(ply, out var entries) && entries.Count > 0)
        {
            LlmThinkingStageText.Text = $"第 {ply} 手 · {entries.Count} 条模型记录";
            LlmThinkingText.Text = string.Join("\n\n────────\n\n", entries.Select(FormatLlmThought));
        }
        else
        {
            LlmThinkingStageText.Text = ply == 0 ? "开始局面" : $"第 {ply} 手";
            LlmThinkingText.Text = ply == 0 ? "从棋谱中选择模型走过的一手，可查看当时返回的思考或选招说明。"
                : "这一手没有大模型思考记录。";
        }

        if (_llmEvaluationHistory.TryGetValue(ply, out var evaluation) &&
            _game.CurrentFen() == evaluation.Fen)
        {
            ShowEngineInfo(evaluation.Info);
            if (ShowGuidance) ShowVariations(evaluation.Candidates);
            EngineStatusText.Text = $"皮卡鱼 · 第 {ply} 手评分";
        }
        else if (ply > 0 && _scores.TryGetValue(ply, out var score))
        {
            ScoreText.Text = _scoreLabels.GetValueOrDefault(ply) ?? AnalysisFormatter.ScoreForRed(score);
        }
    }

    private static string FormatLlmThought(LlmThoughtRecord thought)
    {
        var effort = thought.RequestedReasoningEffort switch
        {
            "low" => "低", "medium" => "中", "high" => "高",
            "auto" or "" => "自动", var value => value
        };
        var sent = thought.ReasoningFellBack
            ? $"接口未接受，已回退：{thought.ReasoningFallbackReason ?? "原因未知"}"
            : thought.SentReasoningEffort is null ? "由模型决定" : $"接口已接收 {thought.SentReasoningEffort}";
        var visible = string.IsNullOrWhiteSpace(thought.VisibleThinking)
            ? "模型服务未提供可见思考内容" : thought.VisibleThinking;
        var move = thought.UciMove.Length == 4
            ? $"{thought.MoveNotation}（{thought.UciMove}）" : thought.MoveNotation;
        return $"{thought.Side} · {thought.Model} · {thought.Action}\n" +
               $"着法 · {move}\n" +
               $"思考等级 · {effort}；{sent}\n" +
               $"模型返回的思考 · {visible}\n" +
               $"选招说明 · {thought.Explanation ?? "—"}\n" +
               (thought.Action == "接受和棋" ? "和棋决定 · 客户端已确认" : "合法校验 · 客户端已通过");
    }

    private Dictionary<int, List<LlmThoughtRecord>> ExportLlmThoughts() =>
        _llmThoughtHistory.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(CloneThought).ToList());

    private void ImportLlmThoughts(IReadOnlyDictionary<int, List<LlmThoughtRecord>>? thoughts)
    {
        _llmThoughtHistory.Clear();
        if (thoughts is not null)
        {
            foreach (var (ply, entries) in thoughts)
            {
                if (ply < 1 || ply > _game.TotalPly || entries is null) continue;
                _llmThoughtHistory[ply] = entries.Where(entry => entry is not null)
                    .Select(CloneThought).ToList();
            }
        }
        ShowLlmInsightsForPly(_game.Ply);
    }

    private static LlmThoughtRecord CloneThought(LlmThoughtRecord thought) => new()
    {
        Ply = thought.Ply,
        Side = thought.Side,
        Model = thought.Model,
        MoveNotation = thought.MoveNotation,
        UciMove = thought.UciMove,
        Action = thought.Action,
        RequestedReasoningEffort = thought.RequestedReasoningEffort,
        SentReasoningEffort = thought.SentReasoningEffort,
        ReasoningFellBack = thought.ReasoningFellBack,
        ReasoningFallbackReason = thought.ReasoningFallbackReason,
        VisibleThinking = thought.VisibleThinking,
        Explanation = thought.Explanation,
        RecordedAt = thought.RecordedAt
    };

    private void PruneLlmInsightsAfter(int ply)
    {
        foreach (var key in _llmThoughtHistory.Keys.Where(key => key > ply).ToArray())
            _llmThoughtHistory.Remove(key);
        foreach (var key in _llmEvaluationHistory.Keys.Where(key => key > ply).ToArray())
            _llmEvaluationHistory.Remove(key);
        StopLlmInsightWorker();
    }

    private void ResetLlmInsights()
    {
        StopLlmInsightWorker();
        _llmThoughtHistory.Clear();
        _llmEvaluationHistory.Clear();
    }

    private void StopLlmInsightWorker()
    {
        Interlocked.Increment(ref _llmAnalysisGeneration);
        _llmAnalysisQueue?.Writer.TryComplete();
        _llmAnalysisCancellation?.Cancel();
        if (_llmAnalysisTask is not null) _retiredLlmAnalysisWorkers.Add(_llmAnalysisTask);
        _llmAnalysisQueue = null;
        _llmAnalysisCancellation = null;
        _llmAnalysisTask = null;
        _queuedLlmAnalysis.Clear();
    }

    private async Task DisposeLlmInsightsAsync()
    {
        StopLlmInsightWorker();
        foreach (var task in _retiredLlmAnalysisWorkers)
            try { await task; } catch { }
        _retiredLlmAnalysisWorkers.Clear();
    }
}

using PaddiXiangqi.Sessions;
using System.Threading;
using Avalonia.Threading;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private sealed record ExternalDecision(string Fen, string? Move, LlmMoveResult? Thought,
        Exception? Error = null, bool Cancelled = false)
    {
        public string? RuleStartFen { get; init; }
        public string? RuleHistory { get; init; }
        public IReadOnlyList<string>? RuleAllowedMoves { get; init; }
    }

    private async Task<ExternalDecision> CalculateExternalDecisionAsync(PikafishClient engine,
        bool red, LlmConnectionSettings? model, CancellationToken ct)
    {
        // Copy all inputs before yielding. The observation loop can change the game while
        // this request runs; neither a stale result nor its progress may update the new board.
        var fen = _game.CurrentFen();
        var startFen = _game.StartFen;
        var moves = _game.UciMoveList;
        var ply = _game.Ply;
        var legal = _game.AllControllerLegalMoves().Select(move => move.Uci).ToHashSet(StringComparer.Ordinal);
        var configuration = _externalActiveConfiguration;
        var settings = configuration?.EngineSettings ?? ReadPlayingEngineSettings();
        var request = new ControllerDecisionRequest(fen, startFen, moves, legal, settings, DefaultEngine.IsBundled, model);
        var decisions = new ControllerDecisionService(_modelRules, _llmClient);
        bool Current() => !ct.IsCancellationRequested && (_externalObserving || _externalRunning) &&
            configuration == _externalActiveConfiguration &&
            _game.Ply == ply && _game.CurrentFen() == fen && _game.UciMoveList == moves;
        try
        {
            if (model is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (!Current()) return new(fen, null, null, Cancelled: true);
                ExternalStatusText.Text = $"{model.Model} 思考中 · 等级 {model.ReasoningEffort ?? "自动"}";
                ResetLlmThinking();
                _llmThinkingRequest = $"外部接管 · {(red ? "红" : "黑")}方 · {model.Model} · 正在校验允许着法";
                _llmThinkingValidation = "正在进行皮卡鱼原生规则校验";
                _llmThinkingSummary = "—"; _llmThinkingEffort = model.ReasoningEffort ?? "自动";
                RefreshThinkingPanelVisibility(); UpdateLlmThinking();
                var progress = new Progress<LlmProgress>(report =>
                {
                    if (!Current()) return;
                    _llmThinkingStage = report.Message;
                    if (!string.IsNullOrWhiteSpace(report.VisibleThinking)) _llmThinkingSummary = report.VisibleThinking;
                    UpdateLlmThinking();
                });
                IProgress<NativeRuleMoves> rulesProgress = new Progress<NativeRuleMoves>(rules =>
                {
                    if (!Current()) return;
                    _llmThinkingRequest = $"外部接管 · {(red ? "红" : "黑")}方 · {model.Model} · {rules.Allowed.Count} 个引擎允许着法";
                    _llmThinkingValidation = $"皮卡鱼原生规则 · 已排除 {rules.Excluded.Count} 个违规着法";
                    UpdateLlmThinking();
                });
                var decision = await decisions.DecideAsync(request, engine, null, progress, rulesProgress.Report, ct);
                var thought = decision.Thought!;
                if (Current())
                {
                    _llmThinkingSummary = thought.VisibleThinking ?? thought.Explanation ?? "服务未返回说明";
                    _llmThinkingCandidate = thought.Move; _llmThinkingValidation = "皮卡鱼原生规则通过，等待外部落子确认";
                    _llmThinkingStage = _externalRunning ? "等待落子" : "已预计算，等待开始接管";
                    UpdateLlmThinking();
                }
                return new(fen, thought.Move, thought)
                { RuleStartFen = startFen, RuleHistory = moves, RuleAllowedMoves = decision.Rules!.Allowed };
            }

            // Optional opponent-turn scoring yields CPU before a playing search starts.
            // Draining it runs in this task; capture and GUI updates continue throughout.
            StopLlmInsightWorker();
            var retired = _retiredLlmAnalysisWorkers.ToArray();
            foreach (var worker in retired) await worker.WaitAsync(ct);
            _retiredLlmAnalysisWorkers.RemoveAll(task => task.IsCompleted);
            ct.ThrowIfCancellationRequested();
            ExternalStatusText.Text = DefaultEngine.Name + " 正在计算 · " + PlayingBudgetDescription(settings);
            EngineInfo? latest = null;
            void Flush()
            {
                var info = Interlocked.Exchange(ref latest, null);
                if (info is null || !Current()) return;
                ShowEngineInfo(info);
                if (ExternalScoreModeBox.SelectedIndex != 2) UpdateScore(ply, red, info);
            }
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Flush());
            try
            {
                if (!Current()) return new(fen, null, null, Cancelled: true);
                var decision = await decisions.DecideAsync(request, engine,
                    info => { if (info.MultiPv == 1) Interlocked.Exchange(ref latest, info); }, null, null, ct);
                var result = decision.Search!;
                Flush();
                if (Current())
                {
                    ShowVariations(result.Candidates);
                    EngineStatusText.Text = $"{engine.EngineName} · 搜索完成";
                }
                return new(fen, decision.Move, null) { RuleStartFen = startFen, RuleHistory = moves };
            }
            finally { timer.Stop(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return new(fen, null, null, Cancelled: true); }
        catch (Exception ex) { return new(fen, null, null, ex); }
    }
}

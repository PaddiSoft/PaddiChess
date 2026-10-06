using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private readonly LlmChessClient _llmClient = new();
    private readonly PikafishRulesClient _modelRules = new();
    private CancellationTokenSource? _llmCancellation;
    private Task? _llmTask;
    private bool _llmBusy;
    private bool _redLlm;
    private bool _blackLlm;

    private void ShowLlmConfigurationStatus(bool red) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closing && MainTabs.SelectedIndex == 2) LlmControls(red).Status.BringIntoView();
    });

    private sealed record PendingLlmDrawOffer(int Ply, string Fen, bool OfferingRed, string? Explanation);
    private PendingLlmDrawOffer? _pendingLlmDrawOffer;
    private int _lastLlmDrawOfferPly = -8;
    private string _llmThinkingStage = "等待执棋";
    private string _llmThinkingRequest = "—";
    private string _llmThinkingEffort = "—";
    private string _llmThinkingSummary = "—";
    private string _llmThinkingCandidate = "—";
    private string _llmThinkingValidation = "—";
    private string _llmThinkingFinal = "—";

    private bool IsLlmTurn => _game.RedToMove ? _redLlm : _blackLlm;

    private (ComboBox ModelList, ComboBox Reasoning, TextBlock Status, Button TestButton) LlmControls(bool red) => red
        ? (RedActiveModelList, RedLlmReasoningBox, RedLlmConfigStatusText, RedLlmTestButton)
        : (BlackActiveModelList, BlackLlmReasoningBox, BlackLlmConfigStatusText, BlackLlmTestButton);

    private void InitializeLlmControls()
    {
        RedLlmCheck.IsChecked = false;
        BlackLlmCheck.IsChecked = false;
        RedLlmReasoningBox.SelectedIndex = ReasoningIndex(_preferences.RedLlmReasoning);
        BlackLlmReasoningBox.SelectedIndex = ReasoningIndex(_preferences.BlackLlmReasoning);
        RedLlmCheck.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) Controller_Changed(RedLlmCheck);
        };
        BlackLlmCheck.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) Controller_Changed(BlackLlmCheck);
        };
        RedActiveModelList.SelectionChanged += (_, _) => OnActiveLlmModelChanged(true);
        BlackActiveModelList.SelectionChanged += (_, _) => OnActiveLlmModelChanged(false);
        RedLlmReasoningBox.SelectionChanged += (_, _) =>
        {
            if (_ready) SaveSettings();
        };
        BlackLlmReasoningBox.SelectionChanged += (_, _) =>
        {
            if (_ready) SaveSettings();
        };
        InitializeLlmProfileControls();
    }

    private static int ReasoningIndex(string? value) => value switch
    {
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        _ => 0
    };

    private static string ReasoningValue(int index) => index switch
    {
        1 => "low",
        2 => "medium",
        3 => "high",
        _ => "auto"
    };

    private static string ReasoningLabel(int index) => index switch
    {
        1 => "低",
        2 => "中",
        3 => "高",
        _ => "自动"
    };

    private static string DescribeReasoningEffort(LlmReasoningEffortStatus? status)
    {
        if (status is null || status.Requested is null) return "自动 · 由模型决定";
        var requested = status.Requested switch
        {
            "low" => "低", "medium" => "中", "high" => "高", _ => status.Requested
        };
        return $"已请求{requested} · 实际执行取决于模型服务";
    }

    private string LlmControllerLabel(bool red)
    {
        var choice = SelectedLlmChoice(red);
        if (choice is null) return "大模型";
        var label = choice.ModelId;
        return label.Length <= 22 ? label : label[..19] + "…";
    }

    private void SaveLlmPreferences()
    {
        SaveEditingLlmProfile();
        var red = SelectedLlmChoice(true);
        var black = SelectedLlmChoice(false);
        _preferences.RedLlmServiceId = red?.ProfileId ?? "";
        _preferences.BlackLlmServiceId = black?.ProfileId ?? "";
        _preferences.RedLlmModel = red?.ModelId ?? "";
        _preferences.BlackLlmModel = black?.ModelId ?? "";
        _preferences.RedLlmBaseUrl = _preferences.LlmServices.FirstOrDefault(profile => profile.Id == red?.ProfileId)?.BaseUrl ?? "";
        _preferences.BlackLlmBaseUrl = _preferences.LlmServices.FirstOrDefault(profile => profile.Id == black?.ProfileId)?.BaseUrl ?? "";
        _preferences.RedLlmReasoning = ReasoningValue(RedLlmReasoningBox.SelectedIndex);
        _preferences.BlackLlmReasoning = ReasoningValue(BlackLlmReasoningBox.SelectedIndex);
    }

    private LlmConnectionSettings ReadLlmSettings(bool red)
    {
        var side = red ? "红方" : "黑方";
        var choice = SelectedLlmChoice(red)
            ?? throw new InvalidOperationException($"请先在上方为{side}选择模型；模型需先在大模型页启用。");
        var profile = _preferences.LlmServices.FirstOrDefault(item => item.Id == choice.ProfileId)
            ?? throw new InvalidOperationException($"找不到{side}所选模型的 API 服务。");
        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
            throw new InvalidOperationException($"请填写 {profile.Name} 的 API 地址。");
        var key = _llmProfileKeys.GetValueOrDefault(profile.Id, "");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"请填写 {profile.Name} 的 API Key。密钥只保存在本次运行的内存中。");
        var effort = ReasoningValue(LlmControls(red).Reasoning.SelectedIndex);
        return new LlmConnectionSettings(profile.BaseUrl, key, choice.ModelId, StreamResponses: true,
            ReasoningEffort: effort == "auto" ? null : effort);
    }

    private void Controller_Changed(ToggleButton changed)
    {
        if (!_ready) return;
        var enablingLlm = (changed == RedLlmCheck || changed == BlackLlmCheck) && changed.IsChecked == true;
        if (enablingLlm)
        {
            var red = changed == RedLlmCheck;
            try { _ = ReadLlmSettings(red); }
            catch (Exception ex)
            {
                _ready = false;
                changed.IsChecked = false;
                _ready = true;
                LlmControls(red).Status.Text = ex.Message;
                AiStatusText.Text = "请先选择模型并配置服务连接";
                MainTabs.SelectedIndex = 2;
                ShowLlmConfigurationStatus(red);
                return;
            }
        }

        _ready = false;
        try
        {
            if (changed == RedEngineCheck && changed.IsChecked == true) RedLlmCheck.IsChecked = false;
            if (changed == BlackEngineCheck && changed.IsChecked == true) BlackLlmCheck.IsChecked = false;
            if (changed == RedLlmCheck && changed.IsChecked == true) RedEngineCheck.IsChecked = false;
            if (changed == BlackLlmCheck && changed.IsChecked == true) BlackEngineCheck.IsChecked = false;
        }
        finally { _ready = true; }

        CancelSearch();
        _redEngine = RedEngineCheck.IsChecked == true;
        _blackEngine = BlackEngineCheck.IsChecked == true;
        _redLlm = RedLlmCheck.IsChecked == true;
        _blackLlm = BlackLlmCheck.IsChecked == true;
        if (!_redLlm || !_blackLlm) _pendingLlmDrawOffer = null;
        _enginePaused = false;
        AiStatusText.Text = _redLlm && _blackLlm ? "双模型对弈已开启"
            : _redLlm || _blackLlm ? "大模型已接管，等待行棋" : "大模型未接管棋局";
        if (!_redLlm && !_blackLlm) ResetLlmThinking();
        RefreshGuidanceVisibility();
        RefreshLlmAnalysisParticipation();
        SaveSettings();
        RefreshUi();
        MaybeStartSearch();
    }

    private async void RedLlmTest_Click(object? sender, RoutedEventArgs e) => await TestLlmAsync(true);
    private async void BlackLlmTest_Click(object? sender, RoutedEventArgs e) => await TestLlmAsync(false);

    private async Task TestLlmAsync(bool red)
    {
        var controls = LlmControls(red);
        controls.TestButton.IsEnabled = false;
        controls.Status.Text = "正在测试所选模型的合法走棋…";
        try
        {
            var settings = ReadLlmSettings(red);
            var testGame = new XiangqiGame();
            if (!red && !testGame.TryMoveUci("b2e2", out _))
                throw new InvalidOperationException("无法生成黑方测试局面。");
            var rules = await _modelRules.GetAllowedMovesAsync(testGame.StartFen, testGame.UciMoveList, testGame.CurrentFen());
            var legal = rules.Allowed;
            var reply = await _llmClient.ChooseMoveAsync(testGame.CurrentFen(), legal, settings);
            if (!testGame.TryMoveUci(reply.Move, out var move))
                throw new InvalidOperationException("模型返回的着法未通过客户端规则校验。");
            controls.Status.Text = $"测试成功：{settings.Model} 选择 {move.Notation}。" +
                $" 思考等级：{DescribeReasoningEffort(reply.ReasoningEffortStatus)}。";
            SaveSettings();
        }
        catch (Exception ex) { controls.Status.Text = ex.Message; }
        finally { controls.TestButton.IsEnabled = true; }
    }

    private async Task RunLlmMoveAsync()
    {
        CancelSearch();
        var generation = _searchGeneration;
        var ply = _game.Ply;
        var oldTotal = _game.TotalPly;
        var red = _game.RedToMove;
        if (_pendingLlmDrawOffer is { } stale &&
            (stale.Ply != ply || stale.Fen != _game.CurrentFen() || stale.OfferingRed == red))
            _pendingLlmDrawOffer = null;
        var pendingOffer = _pendingLlmDrawOffer;
        LlmConnectionSettings settings;
        try { settings = ReadLlmSettings(red); }
        catch (Exception ex)
        {
            _enginePaused = true;
            AiStatusText.Text = "大模型配置不完整";
            LlmControls(red).Status.Text = ex.Message;
            MainTabs.SelectedIndex = 2;
            ShowLlmConfigurationStatus(red);
            RefreshUi();
            return;
        }

        var startFen = _game.StartFen;
        var history = _game.UciMoveList;
        var currentFen = _game.CurrentFen();
        var cancellation = new CancellationTokenSource();
        _llmCancellation = cancellation;
        _llmBusy = true;
        AiStatusText.Text = pendingOffer is null
            ? $"{settings.Model} 正在选择着法…"
            : $"{settings.Model} 正在决定是否接受和棋…";
        ResetLlmThinking();
        _llmThinkingStage = pendingOffer is null ? "准备请求" : "响应和棋";
        _llmThinkingRequest = $"{(red ? "红方" : "黑方")} · {settings.Model} · " +
            $"思考等级 {ReasoningLabel(LlmControls(red).Reasoning.SelectedIndex)} · 正在校验引擎规则" +
            (pendingOffer is null ? "" : " · 对方已提和");
        _llmThinkingEffort = settings.ReasoningEffort is null ? "自动 · 由模型决定" : "正在请求指定等级";
        UpdateLlmThinking();
        RefreshUi();
        QueueLlmPositionAnalysis();
        var moved = false;
        var failed = false;
        try
        {
            var rules = await _modelRules.GetAllowedMovesAsync(startFen, history, currentFen, cancellation.Token);
            if (generation != _searchGeneration || ply != _game.Ply || history != _game.UciMoveList) return;
            var legal = rules.Allowed;
            if (legal.Count == 0)
                throw new InvalidOperationException("皮卡鱼规则校验后没有可选着法，模型执棋已暂停。");
            _llmThinkingRequest = $"{(red ? "红方" : "黑方")} · {settings.Model} · " +
                $"思考等级 {ReasoningLabel(LlmControls(red).Reasoning.SelectedIndex)} · {legal.Count} 个引擎允许着法";
            _llmThinkingValidation = $"皮卡鱼原生规则 · 已排除 {rules.Excluded.Count} 个违规着法";
            UpdateLlmThinking();
            var progress = new Progress<LlmProgress>(report =>
            {
                if (!_llmBusy || generation != _searchGeneration || ply != _game.Ply) return;
                _llmThinkingStage = report.Stage switch
                {
                    LlmProgressStage.Preparing => "核对棋局",
                    LlmProgressStage.Requesting => "等待模型",
                    LlmProgressStage.Reading => "读取响应",
                    LlmProgressStage.Validating => "校验着法",
                    LlmProgressStage.Retrying => "重新选招",
                    _ => "选择完成"
                };
                if (!string.IsNullOrWhiteSpace(report.VisibleThinking))
                    _llmThinkingSummary = report.VisibleThinking;
                if (report.ReasoningEffortStatus is not null)
                    _llmThinkingEffort = DescribeReasoningEffort(report.ReasoningEffortStatus);
                if (report.Stage == LlmProgressStage.Retrying)
                    _llmThinkingValidation = report.Message;
                else if (report.Stage == LlmProgressStage.Validating)
                    _llmThinkingValidation = report.Message;
                UpdateLlmThinking();
            });
            var allowDrawOffer = pendingOffer is null && _redLlm && _blackLlm &&
                ply >= 2 && ply - _lastLlmDrawOfferPly >= 8;
            var reply = pendingOffer is null
                ? await _llmClient.ChooseTurnActionAsync(currentFen, legal, settings,
                    allowDrawOffer, cancellation.Token, progress)
                : await _llmClient.RespondToDrawOfferAsync(currentFen, legal, settings,
                    pendingOffer.Explanation, cancellation.Token, progress);
            if (generation != _searchGeneration || ply != _game.Ply) return;
            _llmThinkingCandidate = reply.Move ?? (reply.Kind == LlmTurnActionKind.AcceptDraw ? "接受和棋" : "—");
            if (!string.IsNullOrWhiteSpace(reply.VisibleThinking))
                _llmThinkingSummary = reply.VisibleThinking;
            else if (!string.IsNullOrWhiteSpace(reply.Explanation))
                _llmThinkingSummary = $"选招说明：{reply.Explanation}";
            _llmThinkingEffort = DescribeReasoningEffort(reply.ReasoningEffortStatus);
            UpdateLlmThinking();
            if (reply.Kind == LlmTurnActionKind.AcceptDraw)
            {
                if (pendingOffer is null || !_game.DeclareDraw())
                    throw new InvalidOperationException("当前棋局无法确认和棋。");
                _pendingLlmDrawOffer = null;
                RecordLlmThought(ply, red, settings.Model, reply, "同意和棋",
                    ReasoningValue(LlmControls(red).Reasoning.SelectedIndex));
                _llmThinkingStage = "已接受和棋";
                _llmThinkingFinal = "双方同意和棋";
                UpdateLlmThinking();
                AiStatusText.Text = $"{settings.Model} 接受和棋";
                BoardFooter.Text = "双方模型同意和棋，对局结束";
                return;
            }
            if (reply.Move is null)
                throw new InvalidOperationException("模型未返回走棋坐标。");
            if (!legal.Contains(reply.Move) || history != _game.UciMoveList)
                throw new InvalidOperationException("模型着法未通过皮卡鱼规则校验，未执行落子。");
            SaveCurrentAnnotation(ply);
            if (!_game.TryMoveUci(reply.Move, out var move))
                throw new InvalidOperationException("模型返回的着法未通过当前棋局规则校验，未执行落子。");
            _llmThinkingValidation = "皮卡鱼原生规则与基本走法校验通过";
            _llmThinkingFinal = $"{move.Notation}（{move.Uci}）";
            _llmThinkingStage = "已落子";
            UpdateLlmThinking();
            if (ply < oldTotal) PruneFutureAnnotations(ply);
            if (reply.Kind == LlmTurnActionKind.OfferDraw)
            {
                _lastLlmDrawOfferPly = ply + 1;
                _pendingLlmDrawOffer = new PendingLlmDrawOffer(ply + 1, _game.CurrentFen(),
                    red, reply.Explanation);
            }
            else if (pendingOffer is not null) _pendingLlmDrawOffer = null;
            _llmBusy = false;
            CommitMove(move);
            RecordLlmThought(_game.Ply, red, settings.Model, reply, move.Notation,
                ReasoningValue(LlmControls(red).Reasoning.SelectedIndex));
            QueueLlmPositionAnalysis();
            AiStatusText.Text = reply.Kind switch
            {
                LlmTurnActionKind.OfferDraw => $"{settings.Model} 已走 {move.Notation} 并提出和棋",
                LlmTurnActionKind.DeclineDraw => $"{settings.Model} 拒绝和棋，已走 {move.Notation}",
                _ => $"{(red ? "红方" : "黑方")} {settings.Model} 已走 {move.Notation}"
            };
            BoardFooter.Text = string.IsNullOrWhiteSpace(reply.Explanation)
                ? $"大模型已走 {move.Notation} · 着法通过规则校验" +
                  (reply.Kind == LlmTurnActionKind.OfferDraw ? " · 正在请对方决定是否和棋" : "")
                : $"大模型已走 {move.Notation} · {reply.Explanation}";
            moved = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                failed = true;
                _enginePaused = true;
                AiStatusText.Text = "模型走棋失败，已暂停";
                LlmControls(red).Status.Text = ex.Message;
                StatusText.Text = ex.Message;
                _llmThinkingStage = "已暂停";
                _llmThinkingValidation = ex.Message;
                UpdateLlmThinking();
            }
        }
        finally
        {
            cancellation.Dispose();
            if (generation == _searchGeneration)
            {
                _llmCancellation = null;
                _llmBusy = false;
                RefreshUi(preserveError: failed);
                if (moved) MaybeStartSearch();
            }
        }
    }

    private void ResetLlmThinking()
    {
        _llmThinkingStage = "等待执棋";
        _llmThinkingRequest = "—";
        _llmThinkingEffort = "—";
        _llmThinkingSummary = "—";
        _llmThinkingCandidate = "—";
        _llmThinkingValidation = "—";
        _llmThinkingFinal = "—";
        UpdateLlmThinking();
    }

    private void UpdateLlmThinking()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateLlmThinking);
            return;
        }
        LlmThinkingStageText.Text = _llmThinkingStage;
        LlmThinkingText.Text = $"请求 · {_llmThinkingRequest}\n" +
                               $"思考等级 · {_llmThinkingEffort}\n" +
                               $"接口返回的思考或说明 · {_llmThinkingSummary}\n" +
                               $"候选落子 · {_llmThinkingCandidate}\n" +
                               $"合法校验 · {_llmThinkingValidation}\n" +
                               $"最终着法 · {_llmThinkingFinal}";
    }
}

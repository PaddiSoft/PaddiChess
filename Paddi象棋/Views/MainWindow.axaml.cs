using System.Text.Json;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;
using PaddiXiangqi.ViewModels;
using System.Collections.ObjectModel;

namespace PaddiXiangqi.Views;

public partial class MainWindow : Window
{
    private readonly XiangqiGame _game = new();
    private readonly AppPreferences _preferences = AppPreferences.Load();
    private readonly PreferencesWriter _settingsWriter = new(AppPreferences.FilePath);
    private readonly SoundEffects _sound = new();
    private readonly Dictionary<int, string> _notes = [];
    private readonly Dictionary<int, double> _scores = new() { [0] = 0 };
    private readonly Dictionary<int, string> _scoreLabels = new();
    private PikafishClient _engine = new();
    private CancellationTokenSource? _searchCancellation;
    private Task? _searchTask;
    private long _searchGeneration;
    private bool _ready;
    private bool _busy;
    private bool _searchPlaysMove;
    private bool _redEngine;
    private bool _blackEngine;
    private bool _autoAnalyze;
    private bool _analysisMode;
    private bool _enginePaused;
    private bool _benchmarkRunning;
    private bool _closing;
    private CancellationTokenSource? _benchmarkCancellation;
    private EngineBenchmarkResult? _previousBenchmark;
    private char[,]? _setupBoard;
    private EngineInfo? _lastEngineInfo;
    private Square? _selected;
    private int _shownPly = -1;
    private int _shownTotalPly = -1;
    private bool _moveListDirty = true;
    private readonly Dictionary<(string Pv, int MaxMoves), string> _formattedVariationCache = [];
    private long _formattedVariationGeneration = -1;
    private readonly Dictionary<int, EngineInfo> _variationChoices = [];
    private readonly Dictionary<int, VariationViewModel> _variationRows = [];
    private readonly ObservableCollection<VariationViewModel> _variationItems = [];
    private int _selectedVariation = 1;

    private bool IsEngineTurn => _game.RedToMove ? _redEngine : _blackEngine;
    private bool ShowGuidance => _analysisMode || _redEngine || _blackEngine || (_externalLinked && ExternalControllerBox.SelectedIndex == 0);

    public MainWindow()
    {
        InitializeComponent();
        _history = new(NavigateToPly);
        MoveScroll.DataContext = _history;
        VariationList.ItemsSource = _variationItems;
        // Opening the app must start a local two-player game, even if the last session used self-play.
        _redEngine = false;
        _blackEngine = false;
        _autoAnalyze = _preferences.AutoAnalyze;
        _sound.Enabled = _preferences.SoundEnabled;
        InitializeEnginePluginControls();
        RedEngineCheck.IsChecked = _redEngine;
        BlackEngineCheck.IsChecked = _blackEngine;
        InitializeLlmControls();
        AnalysisModeCheck.IsChecked = false;
        AutoAnalyzeCheck.IsChecked = _autoAnalyze;
        LevelSlider.Value = Math.Clamp(_preferences.Level, 0, 20);
        ThreadsBox.Value = Math.Clamp(_preferences.Threads, 1, 32);
        HashBox.Maximum = EngineSettings.MaxHashMb;
        HashBox.Value = Math.Clamp(_preferences.HashMb, 16, EngineSettings.MaxHashMb);
        AnalysisLinesBox.Value = Math.Clamp(_preferences.AnalysisLines, 1, 5);
        ThinkBox.Value = Math.Clamp(_preferences.ThinkSeconds, 1, 120);
        DepthBox.Value = Math.Clamp(_preferences.MaxDepth, 1, 255);
        DepthLimitCheck.IsChecked = _preferences.UseDepthLimit;
        InitializeEnginePhaseControls();
        ShowCoordinatesCheck.IsChecked = _preferences.ShowCoordinates;
        AnimationMsBox.Value = Math.Clamp(_preferences.AnimationDurationMs, 0, 1000);
        RecordTitleBox.Text = "";
        Board.Game = _game;
        Board.Flipped = _preferences.Flipped;
        Board.ShowCoordinates = _preferences.ShowCoordinates;
        Board.AnimationDurationMs = Math.Clamp(_preferences.AnimationDurationMs, 0, 1000);
        RefreshMotionPreference();
        Board.SquareClicked += OnSquareClicked;
        Chart.PlySelected += NavigateToPly;
        RedEngineCheck.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) Controller_Changed(RedEngineCheck);
        };
        BlackEngineCheck.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) Controller_Changed(BlackEngineCheck);
        };
        AnalysisModeCheck.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) AnalysisMode_Changed();
        };
        AutoAnalyzeCheck.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) AutoAnalyze_Changed(null, new RoutedEventArgs());
        };
        LevelSlider.ValueChanged += (_, _) =>
        {
            if (_ready) { SaveSettings(); RefreshLevelLabel(); RefreshEngineSettingsSummary(); }
        };
        foreach (var setting in new[] { ThreadsBox, HashBox, AnalysisLinesBox, ThinkBox, DepthBox })
        {
            setting.ValueChanged += (_, _) =>
            {
                if (!_ready) return;
                RefreshEngineSettingsSummary();
                SaveSettings();
            };
        }
        ShowCoordinatesCheck.PropertyChanged += (_, args) =>
        {
            if (!_ready || args.Property != ToggleButton.IsCheckedProperty) return;
            Board.ShowCoordinates = ShowCoordinatesCheck.IsChecked == true;
            Board.Refresh();
            SaveSettings();
        };
        AnimationMsBox.ValueChanged += (_, _) =>
        {
            if (!_ready) return;
            Board.AnimationDurationMs = (int)(AnimationMsBox.Value ?? 230);
            RefreshMotionPreference();
            SaveSettings();
        };
        Closing += OnClosing;
        Closed += OnClosed;
        SizeChanged += (_, _) => RefreshWorkspaceDensity();
        _ready = true;
        RefreshGuidanceVisibility();
        RefreshLevelLabel();
        RefreshEngineSettingsSummary();
        UpdateAnnotationEditor();
        InitializeExternalControls();
        RefreshUi();
        MaybeStartSearch();
    }

    private void AnalysisMode_Changed()
    {
        if (_externalLinked) { _analysisMode = AnalysisModeCheck.IsChecked == true; RefreshGuidanceVisibility(); QueueExternalScore(); return; }
        if (!_ready || _setupBoard is not null) return;
        _analysisMode = AnalysisModeCheck.IsChecked == true;
        RefreshGuidanceVisibility();
        RefreshLlmAnalysisParticipation();
        if (_analysisMode && _lastEngineInfo is not null) ShowEngineInfo(_lastEngineInfo);
        if (!_analysisMode && !_redEngine && !_blackEngine)
        {
            Board.AnalysisArrows = [];
            Board.HintFrom = Board.HintTo = null;
            Board.Refresh();
        }
        if (_llmBusy) { RefreshUi(); return; }
        if (IsLlmTurn || (IsEngineTurn && !_enginePaused))
        { RefreshUi(); MaybeStartSearch(); return; }
        if (!_busy || !_searchPlaysMove)
        {
            CancelSearch();
            if (_analysisMode && _game.Result == GameResult.Ongoing)
                _searchTask = RunSearchAsync(playMove: false, explicitAnalysis: true);
            else MaybeStartSearch();
        }
        RefreshUi();
    }

    private void RefreshGuidanceVisibility()
    {
        var visible = ShowGuidance && _setupBoard is null && _game.Result == GameResult.Ongoing;
        BestMovePanel.IsVisible = visible;
        PvPanel.IsVisible = visible;
        VariationPanel.IsVisible = visible;
        if (!visible)
        {
            Board.AnalysisArrows = [];
            Board.HintFrom = Board.HintTo = null;
            Board.Refresh();
        }
    }

    private void AutoAnalyze_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _autoAnalyze = AutoAnalyzeCheck.IsChecked == true;
        if (_externalLinked)
        {
            // External scoring has its own selector; this preference controls local games.
            QueueExternalScore();
            SaveSettings(); return;
        }
        if (!_autoAnalyze && !_analysisMode && _busy && !_searchPlaysMove)
        {
            CancelSearch();
            Board.AnalysisArrows = [];
        }
        SaveSettings();
        RefreshLlmAnalysisParticipation();
        RefreshUi();
        MaybeStartSearch();
    }

    private void RefreshLevelLabel()
    {
        var level = (int)Math.Round(LevelSlider.Value);
        LevelText.Text = level.ToString();
        LevelHintText.Text = level switch
        {
            <= 3 => "入门 · 更短搜索，更多变化",
            <= 7 => "休闲 · 会考虑几步战术",
            <= 12 => "均衡 · 适合日常对弈",
            <= 17 => "高手 · 搜索更深",
            <= 19 => "大师 · 更长的搜索时间",
            _ => "最强 · 使用下方搜索预算"
        };
    }

    private void RefreshEngineSettingsSummary()
    {
        var settings = ReadPlayingEngineSettings();
        var phases = ReadEnginePhases();
        var analysisLines = Math.Clamp((int)(AnalysisLinesBox.Value ?? 3), 1, 5);
        EffectiveSettingsText.Text =
            $"{DefaultEngine.Name}：{settings.Threads} 线程 · {settings.HashMb} MB · {settings.MultiPv} 路 · " +
            PlayingBudgetDescription(settings) + "\n" +
            $"分析模式：{analysisLines} 路候选。调整参数后从下一次搜索生效。";
        PhaseTimeControls.IsEnabled = phases.Enabled && settings.Level == 20;
        DepthLimitCheck.IsEnabled = settings.Level == 20;
        DepthBox.IsEnabled = settings.Level == 20 && DepthLimitCheck.IsChecked == true;
        CurrentPhaseText.Text = $"当前局面：{EnginePhaseSettings.DisplayName(phases.Phase(_game))} · 子力 {EnginePhaseSettings.MaterialPoints(_game)}/16 · 第 {_game.FullmoveNumber} 回合" +
            (phases.Enabled ? settings.Level == 20 ? " · 已启用阶段预算" : " · 需 20 级才应用阶段预算" : " · 使用统一预算");
        ThreadBudgetHintText.Text = settings.Threads > Environment.ProcessorCount
            ? $"本机 {Environment.ProcessorCount} 个逻辑核心；当前 {settings.Threads} 线程超过核心数，可能增加调度开销。建议比较当前参数测速。"
            : $"本机 {Environment.ProcessorCount} 个逻辑核心。接管与录屏也需要 CPU；流畅优先可留出 1–2 核。";
    }

    private EngineSettings ReadEngineSettings()
    {
        return new EngineSettings((int)Math.Round(LevelSlider.Value), (int)(ThreadsBox.Value ?? 1),
            (int)(HashBox.Value ?? 128), (int)(ThinkBox.Value ?? 12), (int)(DepthBox.Value ?? 30))
        { UseDepthLimit = DepthLimitCheck.IsChecked == true };
    }

    private void SaveSettings()
    {
        if (!_ready) return;
        _preferences.RedEngine = RedEngineCheck.IsChecked == true;
        _preferences.BlackEngine = BlackEngineCheck.IsChecked == true;
        _preferences.AutoAnalyze = AutoAnalyzeCheck.IsChecked == true;
        _preferences.Flipped = Board.Flipped;
        _preferences.SoundEnabled = _sound.Enabled;
        _preferences.ShowCoordinates = ShowCoordinatesCheck.IsChecked == true;
        _preferences.AnimationDurationMs = (int)(AnimationMsBox.Value ?? 230);
        _preferences.ExternalSyncSpeed = Math.Clamp(ExternalSpeedBox.SelectedIndex, 0, 2);
        _preferences.ExternalInputDelivery = Math.Clamp(ExternalDeliveryBox.SelectedIndex, 0, 2);
        _preferences.ExternalScoreMode = Math.Clamp(ExternalScoreModeBox.SelectedIndex, 0, 2);
        _preferences.Level = (int)Math.Round(LevelSlider.Value);
        _preferences.Threads = (int)(ThreadsBox.Value ?? 1);
        _preferences.HashMb = (int)(HashBox.Value ?? 128);
        _preferences.AnalysisLines = (int)(AnalysisLinesBox.Value ?? 3);
        _preferences.ThinkSeconds = (int)(ThinkBox.Value ?? 12);
        _preferences.MaxDepth = (int)(DepthBox.Value ?? 30);
        _preferences.UseDepthLimit = DepthLimitCheck.IsChecked == true;
        _preferences.EnginePhases = ReadEnginePhases();
        _preferences.EnginePath = DefaultEngine.ExecutablePath;
        SaveLlmPreferences();
        _settingsWriter.Schedule(JsonSerializer.Serialize(_preferences));
        RefreshExternalControllerConfiguration();
    }

    private void OnSquareClicked(Square square)
    {
        if (_externalLinked || _externalObserving || _externalCalibrating) return;
        if (_setupBoard is not null) { HandleSetupClick(square); return; }
        if (_game.Result != GameResult.Ongoing || _llmBusy || (_busy && _searchPlaysMove)) return;
        if ((IsEngineTurn || IsLlmTurn) && !_enginePaused) return;
        var piece = _game.Board[square.Rank, square.File];
        if (_selected is { } selected)
        {
            if (selected == square) { ClearSelection(); return; }
            var branchPly = _game.Ply;
            var oldTotal = _game.TotalPly;
            if (_game.TryMove(selected, square, out var move))
            {
                SaveCurrentAnnotation(branchPly);
                CancelSearch();
                if (branchPly < oldTotal) PruneFutureAnnotations(branchPly);
                CommitMove(move);
                MaybeStartSearch();
                return;
            }
        }
        if (piece != '\0' && XiangqiGame.IsRed(piece) == _game.RedToMove)
        {
            _selected = square;
            Board.Selected = square;
            Board.LegalMoves = _game.LegalMovesFrom(square);
            Board.Refresh();
            BoardSubtitle.Text = $"已选 {XiangqiGame.DisplayBoardPiece(piece)} · 选择蓝点走棋";
        }
        else ClearSelection();
    }

    private void PruneFutureAnnotations(int fromPly)
    {
        foreach (var key in _notes.Keys.Where(key => key > fromPly).ToArray()) _notes.Remove(key);
        foreach (var key in _scores.Keys.Where(key => key > fromPly).ToArray()) _scores.Remove(key);
        foreach (var key in _scoreLabels.Keys.Where(key => key > fromPly).ToArray()) _scoreLabels.Remove(key);
        PruneLlmInsightsAfter(fromPly);
        if (_pendingLlmDrawOffer?.Ply > fromPly) _pendingLlmDrawOffer = null;
        if (_lastLlmDrawOfferPly > fromPly) _lastLlmDrawOfferPly = -8;
    }

    private void CommitMove(ChessMove move)
    {
        if (_pendingLlmDrawOffer is { } offer &&
            (_game.Result != GameResult.Ongoing || offer.Ply != _game.Ply || offer.Fen != _game.CurrentFen()))
            _pendingLlmDrawOffer = null;
        ClearSelection();
        ResetAnalysisDisplay();
        Board.Animate(move);
        var result = _game.Result;
        var sound = (result is GameResult.RedWins or GameResult.BlackWins) &&
            (_game.SideInCheck || char.ToUpperInvariant(move.Captured) == 'K') ? GameSound.Mate
            : move.IsCheck ? GameSound.Check : move.Captured != '\0' ? GameSound.Capture : GameSound.Move;
        _sound.Play(sound);
        UpdateAnnotationEditor();
        RefreshUi();
        Dispatcher.UIThread.Post(() => MoveScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void ClearSelection()
    {
        _selected = null;
        Board.Selected = null;
        Board.LegalMoves = [];
        BoardSubtitle.Text = "点击棋子，再点击目标位置走棋";
        Board.Refresh();
    }

    private void ResetAnalysisDisplay()
    {
        TerminalAnalysisPanel.IsVisible = false;
        _formattedVariationCache.Clear();
        _lastEngineInfo = null;
        ResetVariationChoices();
        Board.HintFrom = Board.HintTo = null;
        Board.AnalysisArrows = [];
        BestMoveText.Text = PvText.Text = VariationText.Text = "—";
        ScoreText.Text = DepthText.Text = NodesText.Text = NpsText.Text = "—";
        WdlText.Text = "胜和负概率待分析";
        RefreshGuidanceVisibility();
    }

    private void CancelSearch()
    {
        _searchGeneration++;
        try { _searchCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        try { _llmCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        _searchCancellation = null;
        _llmCancellation = null;
        _busy = false;
        _llmBusy = false;
        _searchPlaysMove = false;
    }

    private void MaybeStartSearch()
    {
        if (_externalLinked || _externalObserving || _externalCalibrating || _closing || _setupBoard is not null || _busy || _llmBusy || _benchmarkRunning || _enginePluginWork || _game.Result != GameResult.Ongoing) return;
        if (IsEngineTurn && !_enginePaused) _searchTask = RunSearchAsync(playMove: true);
        else if (IsLlmTurn && !_enginePaused) _llmTask = RunLlmMoveAsync();
        else if (IsLlmTurn) QueueLlmPositionAnalysis();
        else if (_autoAnalyze || _analysisMode) _searchTask = RunSearchAsync(playMove: false, explicitAnalysis: _analysisMode);
    }

    private async Task RunSearchAsync(bool playMove, bool explicitAnalysis = false)
    {
        CancelSearch();
        var generation = _searchGeneration;
        var searchPly = _game.Ply;
        var oldTotal = _game.TotalPly;
        var redToMove = _game.RedToMove;
        EngineInfo? latestInfo = null;
        var variations = new ConcurrentDictionary<int, EngineInfo>();
        var infoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            var info = Interlocked.Exchange(ref latestInfo, null);
            if (info is null || generation != _searchGeneration) return;
            ShowEngineInfo(info);
            ShowVariations(variations.Values);
            UpdateScore(searchPly, redToMove, info);
        });
        infoTimer.Start();
        var tokenSource = new CancellationTokenSource();
        _searchCancellation = tokenSource;
        ResetVariationChoices();
        _busy = true;
        _searchPlaysMove = playMove;
        EngineStatusText.Text = playMove ? "皮卡鱼正在思考…" : "正在分析当前局面…";
        BestMoveText.Text = "正在搜索";
        DepthText.Text = NodesText.Text = NpsText.Text = ScoreText.Text = PvText.Text = "—";
        WdlText.Text = "胜和负概率待分析";
        VariationText.Text = "—";
        RefreshUi();
        var moveCommitted = false;
        try
        {
            var configured = ReadEngineSettings();
            var lineCount = Math.Clamp((int)(AnalysisLinesBox.Value ?? 3), 1, 5);
            var settings = playMove ? ReadPlayingEngineSettings()
                : explicitAnalysis ? configured with { MultiPvOverride = lineCount }
                : new EngineSettings(3, 1, configured.HashMb, 1, 15)
                { MultiPvOverride = ShowGuidance ? lineCount : 1 };
            var legalMoves = _game.AllLegalMoves()
                .Select(move => move.Uci).ToArray();
            if (playMove && legalMoves.Length == 0)
                throw new InvalidOperationException("当前没有合法着法，自动走棋已暂停；可检查棋谱或手动处理。");
            // Low levels may choose a non-best MultiPV move. Apply native rules to
            // that random selection too, without changing the engine's root search.
            var nativeChoices = playMove && settings.Level < 12 && DefaultEngine.IsBundled
                ? await _modelRules.GetAllowedMovesAsync(_game.StartFen, _game.UciMoveList,
                    _game.CurrentFen(), tokenSource.Token) : null;
            if (generation != _searchGeneration) return;
            var result = await _engine.SearchAsync(_game.StartFen, _game.UciMoveList, settings,
                info =>
                {
                    variations[info.MultiPv] = info;
                    if (info.MultiPv == 1) Interlocked.Exchange(ref latestInfo, info);
                }, tokenSource.Token);
            if (generation != _searchGeneration) return;
            if (Interlocked.Exchange(ref latestInfo, null) is { } finalInfo)
            {
                ShowEngineInfo(finalInfo);
                UpdateScore(searchPly, redToMove, finalInfo);
            }
            ShowVariations(result.Candidates);
            var chosen = ChooseMove(result, settings.Level, playMove, nativeChoices?.Allowed);
            if (chosen.Length != 4) throw new InvalidOperationException("引擎没有返回可用着法。");
            EngineStatusText.Text = $"{_engine.EngineName} · 搜索完成";
            if (ShowGuidance) BestMoveText.Text = DescribeUci(chosen);
            if (playMove)
            {
                if (!legalMoves.Contains(chosen, StringComparer.Ordinal))
                    throw new InvalidOperationException($"引擎返回的着法不符合当前局面的基本走法：{chosen}，未执行落子。");
                SaveCurrentAnnotation(searchPly);
                if (!_game.TryMoveUci(chosen, out var move))
                    throw new InvalidOperationException($"引擎返回了当前局面中的非法着法：{chosen}");
                if (searchPly < oldTotal) PruneFutureAnnotations(searchPly);
                _busy = false;
                CommitMove(move);
                moveCommitted = true;
            }
            else if (ShowGuidance && Square.TryParseUci(chosen[..2], out var from) &&
                     Square.TryParseUci(chosen[2..], out var to))
            {
                Board.HintFrom = from;
                Board.HintTo = to;
                Board.Refresh();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                EngineStatusText.Text = "引擎连接失败";
                StatusText.Text = ex.Message;
                BestMoveText.Text = ex.Message;
                MainTabs.SelectedIndex = 0;
            }
        }
        finally
        {
            infoTimer.Stop();
            tokenSource.Dispose();
            if (generation == _searchGeneration)
            {
                _searchCancellation = null;
                _busy = false;
                _searchPlaysMove = false;
                RefreshUi(preserveError: EngineStatusText.Text == "引擎连接失败");
                if (moveCommitted) MaybeStartSearch();
            }
        }
    }

    private void UpdateScore(int ply, bool redToMove, EngineInfo info)
    {
        double? score = info.Centipawns;
        if (info.Mate is int mate) score = mate >= 0 ? 1200 : -1200;
        if (score is null) return;
        var redScore = redToMove ? score.Value : -score.Value;
        var label = AnalysisFormatter.ScoreForPlayers(info, redToMove);
        if (_scores.TryGetValue(ply, out var previous) && previous == redScore && _scoreLabels.GetValueOrDefault(ply) == label) return;
        _scores[ply] = redScore;
        _scoreLabels[ply] = label;
        Chart.Refresh();
    }

    private string ChooseMove(SearchResult result, int level, bool playMove, IReadOnlyList<string>? nativeAllowed = null)
        => EngineMoveSelector.Select(result, level, playMove, DefaultEngine.IsBundled,
            _game.AllLegalMoves().Select(move => move.Uci).ToHashSet(StringComparer.Ordinal), nativeAllowed);

    private bool IsLegalUci(string uci) =>
        uci is { Length: 4 } && Square.TryParseUci(uci[..2], out var from) && Square.TryParseUci(uci[2..], out var to)
        && _game.LegalMovesFrom(from).Any(m => m.To == to);

    private void ShowEngineInfo(EngineInfo info)
    {
        var fen = _game.CurrentFen();
        var candidates = _llmEvaluationHistory.TryGetValue(_game.Ply, out var saved) && saved.Fen == fen
            ? saved.Candidates : new[] { info };
        _llmEvaluationHistory[_game.Ply] = new(fen, _game.RedToMove, info, candidates);
        _lastEngineInfo = info;
        DepthText.Text = info.Depth.ToString();
        NodesText.Text = info.Nodes.ToString("N0");
        NpsText.Text = info.Nps?.ToString("N0") ?? "—";
        ScoreText.Text = AnalysisFormatter.ScoreForPlayers(info, _game.RedToMove);
        if (info.Wins is int wins && info.Draws is int draws && info.Losses is int losses)
        {
            var redWins = _game.RedToMove ? wins : losses;
            var blackWins = _game.RedToMove ? losses : wins;
            WdlText.Text = $"红胜 {redWins / 10.0:0.0}% · 和 {draws / 10.0:0.0}% · 黑胜 {blackWins / 10.0:0.0}%";
        }
        if (!ShowGuidance || _setupBoard is not null) return;
        if (_selectedVariation == 1) ApplyVariationPreview(info);
    }

    private void ApplyVariationPreview(EngineInfo info)
    {
        if (!ShowGuidance || _setupBoard is not null) return;
        PvText.Text = FormatVariationCached(info.Pv, 10);
        if (info.FirstMove.Length == 4) BestMoveText.Text = DescribeUci(info.FirstMove);
        var arrows = new List<BoardArrow>();
        foreach (var uci in info.Pv.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4))
        {
            if (uci.Length != 4 || !Square.TryParseUci(uci[..2], out var from) ||
                !Square.TryParseUci(uci[2..], out var to)) continue;
            arrows.Add(new BoardArrow(from, to, arrows.Count % 2 == 1, arrows.Count));
        }
        if (!Board.AnalysisArrows.SequenceEqual(arrows))
        {
            Board.AnalysisArrows = arrows;
            Board.Refresh();
        }
    }

    private void ShowVariations(IEnumerable<EngineInfo> infos)
    {
        if (!ShowGuidance || _setupBoard is not null) return;
        var lines = infos.Where(info => info.FirstMove.Length == 4).OrderBy(info => info.MultiPv)
            .Take(5).ToArray();
        if (_llmEvaluationHistory.TryGetValue(_game.Ply, out var saved) && saved.Fen == _game.CurrentFen())
            _llmEvaluationHistory[_game.Ply] = saved with { Candidates = lines };
        VariationText.IsVisible = lines.Length == 0;
        foreach (var info in lines)
        {
            _variationChoices[info.MultiPv] = info;
            if (!_variationRows.TryGetValue(info.MultiPv, out var row))
            {
                row = new VariationViewModel(info.MultiPv, SelectVariation);
                _variationRows[info.MultiPv] = row;
                _variationItems.Add(row);
            }
            var header = $"{info.MultiPv}. {AnalysisFormatter.ScoreForPlayers(info, _game.RedToMove)} · 深度 {info.Depth}";
            var line = FormatVariationCached(info.Pv, 4);
            row.Header = header;
            row.Moves = line;
        }
        UpdateVariationSelection();
        if (_variationChoices.TryGetValue(_selectedVariation, out var selected))
            ApplyVariationPreview(selected);
    }

    private void SelectVariation(int multiPv)
    {
        if (!_variationChoices.TryGetValue(multiPv, out var info)) return;
        _selectedVariation = multiPv;
        UpdateVariationSelection();
        ApplyVariationPreview(info);
    }

    private void UpdateVariationSelection()
    {
        foreach (var (slot, row) in _variationRows)
        {
            row.IsSelected = slot == _selectedVariation;
        }
    }

    private void ResetVariationChoices()
    {
        _selectedVariation = 1;
        _variationChoices.Clear();
        _variationRows.Clear();
        _variationItems.Clear();
        VariationText.IsVisible = true;
        VariationText.Text = "—";
    }

    private string DescribeUci(string uci)
    {
        return FormatVariationCached(uci, 1);
    }

    private string FormatVariationCached(string pv, int maxMoves)
    {
        if (_formattedVariationGeneration != _searchGeneration)
        {
            _formattedVariationCache.Clear();
            _formattedVariationGeneration = _searchGeneration;
        }
        var key = (pv, maxMoves);
        if (_formattedVariationCache.TryGetValue(key, out var display)) return display;
        // A search may emit many distinct PVs; keep the UI cache bounded.
        if (_formattedVariationCache.Count >= 128) _formattedVariationCache.Clear();
        display = AnalysisFormatter.Variation(_game, pv, maxMoves);
        _formattedVariationCache[key] = display;
        return display;
    }

    private void RefreshUi(bool preserveError = false)
    {
        RefreshEnginePluginControls();
        RefreshEngineSettingsSummary();
        Chart.Scores = _scores;
        Chart.ScoreLabels = _scoreLabels;
        Chart.CurrentPly = _game.Ply;
        Chart.MaxPly = _game.TotalPly;
        Chart.Refresh();
        ModeBadge.Text = (_redEngine || _redLlm, _blackEngine || _blackLlm) switch
        {
            (false, false) => "双方对战",
            (true, false) => _redLlm ? "大模型执红" : "引擎执红",
            (false, true) => _blackLlm ? "大模型执黑" : "引擎执黑",
            _ when _redLlm && _blackLlm => "双模型对弈",
            _ => "自动对弈"
        };
        TurnBadge.Text = _game.Result switch
        {
            GameResult.RedWins => "红方获胜",
            GameResult.BlackWins => "黑方获胜",
            GameResult.Draw => "和棋",
            _ => _game.RedToMove ? "红方走棋" : "黑方走棋"
        };
        if (!preserveError)
        {
            StatusText.Text = _game.Result switch
            {
                GameResult.RedWins => "对局结束 · 红方获胜",
                GameResult.BlackWins => "对局结束 · 黑方获胜",
                GameResult.Draw => "双方同意和棋 · 对局结束",
                _ when _llmBusy => "大模型正在选择着法，请稍候…",
                _ when _busy && _searchPlaysMove => $"{DefaultEngine.Name} 正在思考，请稍候…",
                _ when _game.SideInCheck => (_game.RedToMove ? "红方" : "黑方") + "被将军！",
                _ when _enginePaused && (IsEngineTurn || IsLlmTurn) => "自动走棋已暂停，可手动走棋或继续",
                _ when _busy => "正在分析 · 双方仍可走棋",
                _ => (_game.RedToMove ? "红方" : "黑方") + "走棋"
            };
        }
        SoundButton.Content = _sound.Enabled ? "♪  音效开" : "♪  音效关";
        PauseButton.IsVisible = _redEngine || _blackEngine || _redLlm || _blackLlm;
        PauseButton.Content = _enginePaused ? "▶  继续" : "⏸  暂停";
        UndoButton.IsEnabled = _game.CanUndo;
        RedoButton.IsEnabled = _game.CanRedo;
        FirstMoveButton.IsEnabled = _setupBoard is null && _game.Ply > 0;
        PrevMoveButton.IsEnabled = _setupBoard is null && _game.CanUndo;
        NextMoveButton.IsEnabled = _setupBoard is null && _game.CanRedo;
        LastMoveButton.IsEnabled = _setupBoard is null && _game.Ply < _game.TotalPly;
        HintButton.IsEnabled = !_llmBusy && !(_busy && _searchPlaysMove) && _game.Result == GameResult.Ongoing;
        AnalyzeButton.IsEnabled = HintButton.IsEnabled;
        MoveCountText.Text = _game.Ply == _game.TotalPly
            ? $"{_game.Ply} 手" : $"{_game.Ply} / {_game.TotalPly} 手";
        if (_moveListDirty || _shownPly != _game.Ply || _shownTotalPly != _game.TotalPly)
        {
            RefreshMoveList();
            _shownPly = _game.Ply;
            _shownTotalPly = _game.TotalPly;
            _moveListDirty = false;
        }
        NewButton.IsEnabled = true;
        RedActiveModelList.IsEnabled = BlackActiveModelList.IsEnabled = true;
        RedLlmReasoningBox.IsEnabled = BlackLlmReasoningBox.IsEnabled = true;
        var settingUp = _setupBoard is not null;
        RefreshThinkingPanelVisibility();
        SetupPanel.IsVisible = settingUp;
        MoveScroll.IsVisible = !settingUp;
        RecordEditorPanel.IsVisible = !settingUp && _workspaceScene != WorkspaceScene.External;
        Chart.IsVisible = !settingUp;
        CopyFenButton.IsEnabled = !settingUp;
        ImportFenButton.IsEnabled = !settingUp;
        RedEngineCheck.IsEnabled = !settingUp;
        BlackEngineCheck.IsEnabled = !settingUp;
        RedLlmCheck.IsEnabled = !settingUp;
        BlackLlmCheck.IsEnabled = !settingUp;
        AnalysisModeCheck.IsEnabled = !settingUp;
        NewRecordButton.IsEnabled = !settingUp;
        if (settingUp)
        {
            ModeBadge.Text = "摆棋模式";
            TurnBadge.Text = "正在摆棋";
            StatusText.Text = "选择棋子放到棋盘，再确定初始局面";
            MoveCountText.Text = "摆棋中";
            PauseButton.IsVisible = false;
            UndoButton.IsEnabled = RedoButton.IsEnabled = HintButton.IsEnabled = AnalyzeButton.IsEnabled = false;
        }
        RefreshExternalControls();
        BoardNavigation.IsVisible = _workspaceScene != WorkspaceScene.External || (!_externalObserving && !_externalRunning);
        ShowTerminalAnalysis();
    }

    private void RefreshMoveList() => RefreshMoveListIncrementally();

    private void NavigateToPly(int ply)
    {
        if (_externalRunning || _externalObserving || _externalCalibrating) return;
        if (_setupBoard is not null) return;
        if (ply < 0 || ply > _game.TotalPly) return;
        SaveCurrentAnnotation(_game.Ply);
        CancelSearch();
        _enginePaused = true;
        _game.GoToPly(ply);
        ResetAnalysisDisplay();
        ShowLlmInsightsForPly(ply);
        ClearSelection();
        UpdateAnnotationEditor();
        RefreshUi();
        MaybeStartSearch();
    }

    private void UpdateAnnotationEditor()
    {
        var ply = _game.Ply;
        AnnotationTargetText.Text = ply == 0 ? "开始局面 · 注释"
            : $"第 {ply} 手 {_game.History[ply - 1].Notation} · 注释";
        AnnotationBox.Text = _notes.GetValueOrDefault(ply, "");
    }

    private void SaveCurrentAnnotation(int ply)
    {
        var note = AnnotationBox.Text?.Trim() ?? "";
        if (_notes.GetValueOrDefault(ply, "") != note) _moveListDirty = true;
        if (note.Length == 0) _notes.Remove(ply);
        else _notes[ply] = note;
    }

    private void SaveNote_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrentAnnotation(_game.Ply);
        _moveListDirty = true;
        RefreshUi();
        BoardFooter.Text = "当前着法注释已保存";
    }

    private void NewGame_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalLinked) return;
        MainTabs.SelectedIndex = 0;
        CancelSearch();
        ExitSetupMode();
        _game.ExternalAdjudication = false;
        _game.NewGame();
        _engine.RequestNewGame();
        _notes.Clear();
        _moveListDirty = true;
        _scores.Clear(); _scoreLabels.Clear();
        _scores[0] = 0;
        ResetLlmInsights();
        _pendingLlmDrawOffer = null;
        _lastLlmDrawOfferPly = -8;
        _enginePaused = false;
        ResetLlmThinking();
        RecordTitleBox.Text = "";
        ResetAnalysisDisplay();
        ClearSelection();
        EngineStatusText.Text = "引擎待命";
        UpdateAnnotationEditor();
        RefreshUi();
        MaybeStartSearch();
    }

    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        if (!_game.CanUndo) return;
        NavigateToPly(_game.Ply - 1);
    }

    private void FirstMove_Click(object? sender, RoutedEventArgs e) => NavigateToPly(0);
    private void PrevMove_Click(object? sender, RoutedEventArgs e) => NavigateToPly(_game.Ply - 1);
    private void NextMove_Click(object? sender, RoutedEventArgs e) => NavigateToPly(_game.Ply + 1);
    private void LastMove_Click(object? sender, RoutedEventArgs e) => NavigateToPly(_game.TotalPly);

    private void Redo_Click(object? sender, RoutedEventArgs e)
    {
        if (!_game.CanRedo) return;
        NavigateToPly(_game.Ply + 1);
    }

    private void Flip_Click(object? sender, RoutedEventArgs e)
    {
        Board.Flipped = !Board.Flipped;
        Board.Refresh();
        SaveSettings();
    }

    private void Pause_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalLinked) { _externalCancellation?.Cancel(); return; }
        _enginePaused = !_enginePaused;
        if (_enginePaused)
        {
            CancelSearch();
            if (_redLlm || _blackLlm)
            {
                _llmThinkingStage = "已暂停";
                _llmThinkingValidation = "自动走棋已暂停";
                UpdateLlmThinking();
            }
        }
        RefreshUi();
        MaybeStartSearch();
    }

    private void Sound_Click(object? sender, RoutedEventArgs e)
    {
        _sound.Enabled = !_sound.Enabled;
        SaveSettings();
        RefreshUi();
        if (_sound.Enabled) _sound.Play(GameSound.Move);
    }

    private void Hint_Click(object? sender, RoutedEventArgs e)
    {
        if (_llmBusy || (_busy && _searchPlaysMove) || _game.Result != GameResult.Ongoing) return;
        MainTabs.SelectedIndex = 0;
        if (!_analysisMode)
        {
            AnalysisModeCheck.IsChecked = true;
            return;
        }
        _searchTask = RunSearchAsync(playMove: false, explicitAnalysis: true);
    }

    private void Analyze_Click(object? sender, RoutedEventArgs e) => Hint_Click(sender, e);

    private void ClearHash_Click(object? sender, RoutedEventArgs e)
    {
        CancelSearch();
        _engine.RequestClearHash();
        EngineStatusText.Text = "已请求清空搜索缓存";
        BoardFooter.Text = "引擎缓存将在下一次搜索前清空";
        MaybeStartSearch();
    }

    private async void Benchmark_Click(object? sender, RoutedEventArgs e)
    {
        if (_enginePluginWork) return;
        if (_externalLinked) { ExternalStatusText.Text = "请先断开外部接管再执行此操作。"; return; }
        if (_benchmarkCancellation is { } active)
        {
            active.Cancel();
            return;
        }
        var cancellation = new CancellationTokenSource();
        _benchmarkCancellation = cancellation;
        _benchmarkRunning = true;
        BenchmarkButton.Content = "停止测速";
        ConfiguredBenchmarkButton.Content = "停止测速";
        BenchmarkResultText.Text = "正在准备基准测试…";
        SaveSettings();
        CancelSearch();
        try
        {
            if (_searchTask is not null)
                try { await _searchTask; } catch (OperationCanceledException) { }
            await using var tester = DefaultEngine.CreateClient();
            var progress = new Progress<EngineBenchmarkProgress>(update =>
                BenchmarkResultText.Text = $"正在测试局面 {update.Position} / {update.TotalPositions}…");
            var useConfigured = ReferenceEquals(sender, ConfiguredBenchmarkButton);
            var threads = useConfigured ? (int)(ThreadsBox.Value ?? 1) : 1;
            var hash = useConfigured ? (int)(HashBox.Value ?? 128) : 16;
            var result = await tester.BenchmarkAsync(threads: threads, hashMb: hash, depth: 10,
                cancellationToken: cancellation.Token, progress: progress);
            var display = $"{result.EngineName} · {Path.GetFileName(result.EnginePath)}\n" +
                $"{result.Threads} 线程 · {result.HashMb} MB · 深度 {result.Depth}\n" +
                $"每秒 {result.NodesPerSecond:N0} 节点 · 总节点 {result.Nodes:N0} · {result.SearchTimeMs:N0} ms";
            if (_previousBenchmark is { NodesPerSecond: > 0 } previous)
            {
                var change = (result.NodesPerSecond / (double)previous.NodesPerSecond - 1) * 100;
                display += $"\n上次 {previous.Threads} 线程 / {previous.HashMb} MB：{previous.NodesPerSecond:N0} 节点/秒";
                if (result.Threads == previous.Threads && result.HashMb == previous.HashMb && result.Depth == previous.Depth)
                    display += $" · 本次 {(change >= 0 ? "快" : "慢")}{Math.Abs(change):0.0}%";
                else display += " · 参数不同，NPS 仅作性能参考";
            }
            _previousBenchmark = result;
            BenchmarkResultText.Text = display;
        }
        catch (OperationCanceledException)
        {
            BenchmarkResultText.Text = "测速已停止。";
        }
        catch (Exception ex)
        {
            BenchmarkResultText.Text = $"测速失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_benchmarkCancellation, cancellation)) _benchmarkCancellation = null;
            cancellation.Dispose();
            _benchmarkRunning = false;
            BenchmarkButton.Content = "运行基准测速 · 1 线程";
            ConfiguredBenchmarkButton.Content = "按当前线程 / 置换表测速";
            MaybeStartSearch();
        }
    }


    private async void SaveRecord_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrentAnnotation(_game.Ply);
        var title = string.IsNullOrWhiteSpace(RecordTitleBox.Text) ? "未命名棋谱" : RecordTitleBox.Text.Trim();
        var pendingOffer = _pendingLlmDrawOffer is { } offer &&
            offer.Ply == _game.Ply && offer.Fen == _game.CurrentFen() ? offer : null;
        var record = new GameRecord
        {
            Title = title,
            StartFen = _game.StartFen,
            Moves = _game.History.Select(move => move.Uci).ToList(),
            Notes = new Dictionary<int, string>(_notes),
            Scores = new Dictionary<int, double>(_scores), ScoreLabels = new(_scoreLabels),
            LlmThoughts = ExportLlmThoughts(),
            AgreedDrawPly = _game.AgreedDrawPly,
            ExternalAdjudication = _game.ExternalAdjudication,
            PendingDrawOfferPly = pendingOffer?.Ply,
            PendingDrawOfferingRed = pendingOffer?.OfferingRed,
            PendingDrawExplanation = pendingOffer?.Explanation,
            CurrentPly = _game.Ply
        };
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存 Paddi 象棋棋谱",
                SuggestedFileName = title + ".paddi.json",
                FileTypeChoices = [new FilePickerFileType("Paddi 象棋棋谱") { Patterns = ["*.paddi.json", "*.json"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, record, new JsonSerializerOptions { WriteIndented = true });
            BoardFooter.Text = "棋谱已保存：" + file.Name;
        }
        catch (Exception ex) { BoardFooter.Text = "保存失败：" + ex.Message; }
    }

    private async void LoadRecord_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalLinked) { ExternalStatusText.Text = "请先断开外部接管再执行此操作。"; return; }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入 Paddi 象棋棋谱",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Paddi 象棋棋谱") { Patterns = ["*.paddi.json", "*.json"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            var record = await GameRecordStorage.ReadValidatedAsync(stream, CancellationToken.None);

            CancelSearch();
            _game.ExternalAdjudication = record.ExternalAdjudication;
            _game.LoadFen(record.StartFen);
            foreach (var uci in record.Moves) _game.TryMoveUci(uci, out _);
            if (record.AgreedDrawPly is { } acceptedPly)
            {
                _game.GoToPly(acceptedPly);
                _game.DeclareDraw();
            }
            _game.GoToPly(Math.Clamp(record.CurrentPly, 0, _game.TotalPly));
            ResetLlmInsights();
            ImportLlmThoughts(record.LlmThoughts);
            _pendingLlmDrawOffer = record.PendingDrawOfferPly is { } savedOfferPly &&
                record.PendingDrawOfferingRed is { } offeringRed
                ? new PendingLlmDrawOffer(savedOfferPly, _game.CurrentFen(), offeringRed,
                    record.PendingDrawExplanation) : null;
            _lastLlmDrawOfferPly = _pendingLlmDrawOffer?.Ply ?? -8;
            _notes.Clear();
            _moveListDirty = true;
            foreach (var note in record.Notes.Where(pair => pair.Key >= 0 && pair.Key <= _game.TotalPly))
                _notes[note.Key] = note.Value;
            _scores.Clear(); _scoreLabels.Clear();
            foreach (var score in record.Scores.Where(pair => pair.Key >= 0 && pair.Key <= _game.TotalPly))
                _scores[score.Key] = score.Value;
            if (!_scores.ContainsKey(0)) _scores[0] = 0;
            foreach (var label in record.ScoreLabels.Where(pair => pair.Key >= 0 && pair.Key <= _game.TotalPly)) _scoreLabels[label.Key] = label.Value;
            RecordTitleBox.Text = record.Title;
            _enginePaused = true;
            _engine.RequestNewGame();
            ResetAnalysisDisplay();
            ShowLlmInsightsForPly(_game.Ply);
            ClearSelection();
            UpdateAnnotationEditor();
            RefreshUi();
            BoardFooter.Text = "已导入棋谱：" + files[0].Name;
            MaybeStartSearch();
        }
        catch (Exception ex) { BoardFooter.Text = "导入棋谱失败：" + ex.Message; }
    }

    private async void CopyFen_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_game.CurrentFen());
        BoardFooter.Text = "当前局面 FEN 已复制到剪贴板";
    }

    private async void ImportFen_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalLinked) { ExternalStatusText.Text = "请先断开外部接管再执行此操作。"; return; }
        var fen = await new FenImportWindow().ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(fen)) return;
        try
        {
            var validated = new XiangqiGame();
            validated.LoadFen(fen);
            CancelSearch();
            _game.ExternalAdjudication = false;
            _game.LoadFen(fen);
            _engine.RequestNewGame();
            _notes.Clear();
            _moveListDirty = true;
            _scores.Clear(); _scoreLabels.Clear();
            _scores[0] = 0;
            ResetLlmInsights();
            _pendingLlmDrawOffer = null;
            _lastLlmDrawOfferPly = -8;
            RecordTitleBox.Text = "";
            _enginePaused = false;
            ResetAnalysisDisplay();
            ClearSelection();
            UpdateAnnotationEditor();
            RefreshUi();
            MaybeStartSearch();
        }
        catch (Exception ex) { BoardFooter.Text = "导入失败：" + ex.Message; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Source is TextBox) return;
        if (_setupBoard is not null)
        {
            if (e.Key == Key.Escape)
            {
                if (_setupEditor?.SelectedSquare is not null) RestorePickedPiece();
                else SetupCancel_Click(this, new RoutedEventArgs());
            }
            return;
        }
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (e.Key == Key.Z) Undo_Click(this, new RoutedEventArgs());
            else if (e.Key == Key.Y) Redo_Click(this, new RoutedEventArgs());
            else if (e.Key == Key.N) NewGame_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape) ClearSelection();
        else if (e.Key == Key.F) Flip_Click(this, new RoutedEventArgs());
    }
}

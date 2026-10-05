using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PaddiXiangqi.Engine;
using PaddiXiangqi.ViewModels;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private bool _enginePluginWork;
    private CancellationTokenSource? _enginePluginCancellation;
    private readonly Dictionary<UciEngineOption, EngineRuleOptionViewModel> _ruleEditors = new();
    private string? _ruleEditorPath;
    private Dictionary<string, string> _unprobedRuleOptions = new(StringComparer.OrdinalIgnoreCase);
    private EnginePlugin DefaultEngine => _preferences.EnginePlugins.First(plugin => plugin.Id == _preferences.DefaultEngineId);
    private void InitializeEnginePluginControls()
    {
        InitializeWorkspaceViews();
        _engine.OverridePath = DefaultEngine.IsBundled ? null : DefaultEngine.ExecutablePath;
        _engine.OverrideEvalPath = DefaultEngine.IsBundled ? null : DefaultEngine.EvalFilePath;
        _engine.OverrideRuleOptions = new Dictionary<string, string>(DefaultEngine.RuleOptions, StringComparer.OrdinalIgnoreCase);
        EnginePluginList.SelectionChanged += (_, _) => LoadEnginePluginEditor();
        RefreshEnginePluginList(DefaultEngine.Id);
    }
    private void RefreshEnginePluginList(string? selectedId = null)
    {
        var selected = selectedId ?? (EnginePluginList.SelectedItem as EnginePlugin)?.Id ?? DefaultEngine.Id;
        EnginePluginList.ItemsSource = _preferences.EnginePlugins.ToArray();
        EnginePluginList.SelectedItem = _preferences.EnginePlugins.FirstOrDefault(plugin => plugin.Id == selected) ?? DefaultEngine;
        ActiveEngineText.Text = $"当前引擎：{DefaultEngine.Name}";
        DefaultEngineText.Text = $"默认引擎 · {DefaultEngine.Name}";
        LoadEnginePluginEditor();
    }
    private void LoadEnginePluginEditor()
    {
        if (EnginePluginList.SelectedItem is not EnginePlugin plugin) return;
        EnginePluginNameBox.Text = plugin.Name;
        EnginePathBox.Text = plugin.IsBundled ? PikafishClient.BundledEnginePath() : plugin.ExecutablePath;
        EngineEvalBox.Text = plugin.EvalFilePath;
        EnginePluginInfoText.Text = (string.IsNullOrWhiteSpace(plugin.DetectedName) ? "尚未检测 UCI 信息" : plugin.DetectedName) +
            (string.IsNullOrWhiteSpace(plugin.Author) ? "" : $"\n{plugin.Author}");
        RenderEngineRuleOptions(plugin, plugin.DetectedRuleOptions);
        RefreshEnginePluginControls();
    }

    private static string RuleLabel(string value) => value switch
    {
        "AsianRule" => "亚洲规则 · AsianRule", "SkyRule" => "天规 · SkyRule",
        "ChineseRule" => "简易中规 · ChineseRule", "ComputerRule" => "计算机规则 · ComputerRule",
        "YitianRule" => "弈天规则 · YitianRule", "AllowChase" => "允许长捉 · AllowChase",
        "NoJudgement" => "不判循环违规 · NoJudgement", "None" => "正常判和 · None",
        "DrawAsBlackWin" => "和棋算黑胜", "DrawAsRedWin" => "和棋算红胜",
        "DrawRepAsBlackWin" => "重复和棋算黑胜", "DrawRepAsRedWin" => "重复和棋算红胜", _ => value
    };
    private void RenderEngineRuleOptions(EnginePlugin plugin, IEnumerable<UciEngineOption> options, bool discovered = false)
    {
        _ruleEditors.Clear();
        var declarations = options.Where(option => option.IsRuleOption && option.Type != "button").ToArray();
        // Imported manifests have values but no cached declarations. Saving a name
        // or exporting before the first probe must not erase those values.
        _unprobedRuleOptions = !discovered && declarations.Length == 0
            ? new(plugin.RuleOptions, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        _ruleEditorPath = plugin.IsBundled ? PikafishClient.BundledEnginePath() : plugin.ExecutablePath;
        foreach (var option in declarations)
        {
            var value = plugin.RuleOptions.GetValueOrDefault(option.Name, option.DefaultValue);
            _ruleEditors.Add(option, new EngineRuleOptionViewModel(option, value, RuleLabel));
        }
        EngineRuleOptionsPanel.ItemsSource = _ruleEditors.Values.ToArray();
        EngineRuleStatusText.Text = _ruleEditors.Count > 0
            ? "由此引擎声明，保存或设为默认后生效。对弈、分析和接管共用；切换版本会读取各自规则。"
            : !discovered && plugin.DetectedRuleOptions.Count == 0
                ? "点击“检测引擎”读取该版本提供的规则选项。"
                : "此引擎未声明可配置的 UCI 规则选项。执棋时会传入完整棋谱，规则能力取决于此引擎的实现。";
    }

    private Dictionary<string, string> ReadRuleOptions(string path)
    {
        if (path != _ruleEditorPath) return new(StringComparer.OrdinalIgnoreCase);
        if (_ruleEditors.Count == 0) return new(_unprobedRuleOptions, StringComparer.OrdinalIgnoreCase);
        return _ruleEditors.ToDictionary(item => item.Key.Name, item => item.Value.ReadValue(), StringComparer.OrdinalIgnoreCase);
    }
    private void RefreshEnginePluginControls()
    {
        var selected = EnginePluginList.SelectedItem as EnginePlugin;
        var idle = !_enginePluginWork && !_externalObserving && !_externalRunning && !_externalCalibrating && !_benchmarkRunning && !_closing;
        var editable = idle && selected is { IsBundled: false };
        EnginePluginNameBox.IsEnabled = EnginePathBox.IsEnabled = EngineEvalBox.IsEnabled = editable;
        EnginePluginSaveButton.IsEnabled = idle && selected is not null;
        EnginePluginDeleteButton.IsEnabled = EnginePluginExportButton.IsEnabled = editable;
        EngineBrowseButton.IsEnabled = EngineEvalBrowseButton.IsEnabled = editable;
        EnginePluginDefaultButton.IsEnabled = idle && selected is not null;
        EnginePluginProbeButton.IsEnabled = idle && selected is not null;
        EnginePluginAddButton.IsEnabled = EnginePluginImportButton.IsEnabled = idle;
        EnginePluginList.IsEnabled = !_enginePluginWork;
        EngineRuleOptionsPanel.IsEnabled = idle;
    }
    private EnginePlugin ReadEnginePluginEditor(bool includeRules = true)
    {
        var selected = (EnginePlugin)EnginePluginList.SelectedItem!;
        if (selected.IsBundled)
        {
            var bundled = EnginePlugin.Bundled();
            bundled.RuleOptions = includeRules ? ReadRuleOptions(PikafishClient.BundledEnginePath())
                : new(selected.RuleOptions, StringComparer.OrdinalIgnoreCase);
            bundled.DetectedRuleOptions = _ruleEditors.Count > 0 ? _ruleEditors.Keys.ToList() : selected.DetectedRuleOptions.ToList();
            return bundled;
        }
        var path = EnginePathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("请选择存在的引擎程序。", path);
        var eval = EngineEvalBox.Text?.Trim() ?? "";
        if (eval.Length > 0 && !File.Exists(eval)) throw new FileNotFoundException("请选择存在的权重文件，或留空使用引擎自身默认值。", eval);
        var name = EnginePluginNameBox.Text?.Trim() ?? "";
        return new() { Id = selected.Id, Name = name.Length == 0 ? Path.GetFileNameWithoutExtension(path) : name,
            ExecutablePath = Path.GetFullPath(path), EvalFilePath = eval.Length == 0 ? "" : Path.GetFullPath(eval),
            DetectedName = path == selected.ExecutablePath && eval == selected.EvalFilePath ? selected.DetectedName : "",
            Author = path == selected.ExecutablePath && eval == selected.EvalFilePath ? selected.Author : "",
            RuleOptions = includeRules ? ReadRuleOptions(path) : new(selected.RuleOptions, StringComparer.OrdinalIgnoreCase),
            DetectedRuleOptions = path == _ruleEditorPath ? _ruleEditors.Keys.ToList() : [] };
    }
    private void StoreEnginePlugin(EnginePlugin plugin)
    {
        var index = _preferences.EnginePlugins.FindIndex(item => item.Id == plugin.Id);
        _preferences.EnginePlugins[index] = plugin;
        _preferences.EnginePath = DefaultEngine.ExecutablePath;
        SaveSettings(); RefreshEnginePluginList(plugin.Id);
    }
    private async Task<string?> ChooseEngineFileAsync(string title, string[]? patterns = null)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false,
            FileTypeFilter = patterns is null ? null : [new FilePickerFileType(title) { Patterns = patterns }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    private async void EnginePluginAdd_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ChooseEngineFileAsync("选择 UCI 象棋引擎程序");
            if (path is null || _closing) return;
            var existing = _preferences.EnginePlugins.FirstOrDefault(plugin => !plugin.IsBundled && plugin.ExecutablePath == path);
            var plugin = existing ?? new EnginePlugin { Name = Path.GetFileNameWithoutExtension(path), ExecutablePath = path };
            if (existing is null) _preferences.EnginePlugins.Add(plugin);
            SaveSettings(); RefreshEnginePluginList(plugin.Id);
            EnginePluginStatusText.Text = "已添加。可绑定该版本的权重文件，检测通过后设为默认。";
        }
        catch (Exception ex) { EnginePluginStatusText.Text = ex.Message; }
    }
    private async void EnginePluginImport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ChooseEngineFileAsync("选择引擎插件配置", ["*.paddi-engine.json", "*.json"]);
            if (path is null || _closing) return;
            var plugin = EnginePlugin.Import(path);
            _preferences.EnginePlugins.Add(plugin); SaveSettings(); RefreshEnginePluginList(plugin.Id);
            EnginePluginStatusText.Text = "已导入插件。可检测后设为默认引擎。";
        }
        catch (Exception ex) { EnginePluginStatusText.Text = "导入失败：" + ex.Message; }
    }
    private async void EngineBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var path = await ChooseEngineFileAsync("选择引擎程序");
        if (path is not null && !_closing) EnginePathBox.Text = path;
    }
    private async void EngineEvalBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var path = await ChooseEngineFileAsync("选择该引擎的 NNUE 权重", ["*.nnue", "*"]);
        if (path is not null && !_closing) EngineEvalBox.Text = path;
    }
    private async void EnginePluginSave_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalRunning || _enginePluginWork) return;
        try
        {
            var plugin = ReadEnginePluginEditor();
            if (plugin.Id == DefaultEngine.Id) await ActivateEnginePluginAsync(plugin);
            else { StoreEnginePlugin(plugin); EnginePluginStatusText.Text = "插件配置已保存。"; }
        }
        catch (Exception ex) { EnginePluginStatusText.Text = "保存失败：" + ex.Message; }
    }
    private async void EnginePluginDefault_Click(object? sender, RoutedEventArgs e)
    {
        if (_externalRunning || _enginePluginWork) return;
        try { await ActivateEnginePluginAsync(ReadEnginePluginEditor()); }
        catch (Exception ex) { EnginePluginStatusText.Text = "切换失败，原默认引擎保留：" + ex.Message; }
    }
    private async Task ActivateEnginePluginAsync(EnginePlugin plugin)
    {
        if (_externalObserving || _externalRunning || _externalCalibrating || _benchmarkRunning) throw new InvalidOperationException("请先停止实时同步、接管或测速，再切换引擎；棋盘和棋谱会保留。");
        _enginePluginWork = true; RefreshExternalControls();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _enginePluginCancellation = ct;
        PikafishClient? next = plugin.CreateClient();
        try
        {
            EnginePluginStatusText.Text = "正在验证引擎与权重…";
            var identity = await next.ProbeAsync(ct.Token);
            if (_closing) return;
            CancelSearch();
            if (_searchTask is not null) { try { await _searchTask; } catch (OperationCanceledException) { } }
            await DisposeLlmInsightsAsync();
            if (_externalPlayingEngine is not null)
            { await _externalPlayingEngine.DisposeAsync(); _externalPlayingEngine = null; }
            await _engine.DisposeAsync();
            _engine = next; next = null; _engine.RequestNewGame();
            plugin.DetectedName = identity.Name; plugin.Author = identity.Author;
            plugin.DetectedRuleOptions = identity.Options.Where(option => option.IsRuleOption).ToList();
            _preferences.DefaultEngineId = plugin.Id; StoreEnginePlugin(plugin);
            await _settingsWriter.FlushAsync();
            _llmEvaluationHistory.Clear();
            ResetAnalysisDisplay();
            EngineStatusText.Text = $"{plugin.Name} · 已连接";
            EnginePluginStatusText.Text = $"已设为默认：{plugin.Name}。执棋、分析、接管与测速共用此配置。";
            RefreshUi();
        }
        finally
        {
            if (next is not null) await next.DisposeAsync();
            _enginePluginCancellation = null; _enginePluginWork = false; RefreshEnginePluginControls();
            if (!_closing) { RefreshExternalControls(); UpdateExternalModelLabel(); RefreshLlmAnalysisParticipation(); MaybeStartSearch(); }
        }
    }
    private async void EnginePluginProbe_Click(object? sender, RoutedEventArgs e)
    {
        if (_enginePluginWork || _externalRunning) return;
        _enginePluginWork = true; RefreshEnginePluginControls();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _enginePluginCancellation = ct;
        try
        {
            var plugin = ReadEnginePluginEditor(includeRules: false);
            await using var client = plugin.CreateClient();
            // Discover the current binary even after an update removes a saved option.
            client.OverrideRuleOptions = new Dictionary<string, string>();
            EnginePluginStatusText.Text = "正在读取 UCI 能力并验证合法着法…";
            var identity = await client.ProbeAsync(ct.Token);
            if (_closing) return;
            // A probe does not change the active engine or save unsaved paths.
            EnginePluginInfoText.Text = $"{identity.Name}\n{identity.Author}\n权重：{identity.EvalFile ?? "引擎自身默认 / 内嵌权重"}\n" +
                "UCI 参数：" + string.Join("、", identity.Options.Select(option => option.Name));
            plugin.DetectedName = identity.Name;
            RenderEngineRuleOptions(plugin, identity.Options, discovered: true);
            EnginePluginStatusText.Text = "检测通过：引擎能返回合法的象棋着法。配置可保存或设为默认。";
        }
        catch (Exception ex) { EnginePluginStatusText.Text = "检测失败：" + ex.Message; }
        finally { _enginePluginWork = false; _enginePluginCancellation = null; RefreshEnginePluginControls(); if (!_closing) MaybeStartSearch(); }
    }
    private async void EnginePluginDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (EnginePluginList.SelectedItem is not EnginePlugin { IsBundled: false } plugin || _externalRunning || _enginePluginWork) return;
        try
        {
            if (DefaultEngine.Id == plugin.Id) await ActivateEnginePluginAsync(EnginePlugin.Bundled());
            if (_closing) return;
            _preferences.EnginePlugins.RemoveAll(item => item.Id == plugin.Id); SaveSettings(); RefreshEnginePluginList();
            EnginePluginStatusText.Text = "已从插件列表移除，引擎程序和权重文件保留。";
        }
        catch (Exception ex) { EnginePluginStatusText.Text = "移除失败：" + ex.Message; }
    }
    private async void EnginePluginExport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var plugin = ReadEnginePluginEditor();
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出引擎插件配置",
                SuggestedFileName = "engine.paddi-engine.json", DefaultExtension = "json" });
            if (file?.TryGetLocalPath() is { } path) { plugin.Export(path); EnginePluginStatusText.Text = "插件配置已导出；相对路径以配置文件所在目录为准。"; }
        }
        catch (Exception ex) { EnginePluginStatusText.Text = "导出失败：" + ex.Message; }
    }
    private void EngineManage_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;
}

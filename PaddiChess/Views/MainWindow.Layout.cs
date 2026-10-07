using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private enum WorkspaceScene { Play, External, Configuration }
    private WorkspaceScene _workspaceScene;
    private GridLength[]? _localPaneWidths;
    private GridLength[]? _externalPaneWidths;
    private GridLength[]? _externalReviewWidths;
    private bool _workspaceLayoutInitialized;
    private bool? _compactExternalRows;

    private void PlayWorkspace_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void ExternalWorkspace_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 3;
    private void ConfigurationWorkspace_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;
    private void SettingsWorkspace_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void ModelServicesWorkspace_Click(object? sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;

    private void RefreshWorkspaceScene()
    {
        var scene = MainTabs.SelectedIndex switch
        {
            3 => WorkspaceScene.External,
            1 or 2 or 4 => WorkspaceScene.Configuration,
            _ => WorkspaceScene.Play
        };
        UpdateWorkspaceLayout(scene);
        AnalysisTab.IsVisible = scene == WorkspaceScene.Play;
        ExternalTab.IsVisible = scene == WorkspaceScene.External;
        SettingsTab.IsVisible = ModelServicesTab.IsVisible = EnginePluginsTab.IsVisible = scene == WorkspaceScene.Configuration;
        PlayWorkspaceButton.Classes.Set("active", scene == WorkspaceScene.Play);
        ExternalWorkspaceButton.Classes.Set("active", scene == WorkspaceScene.External);
        SettingsWorkspaceButton.Classes.Set("active", MainTabs.SelectedIndex == 1);
        ModelServicesWorkspaceButton.Classes.Set("active", MainTabs.SelectedIndex == 2);
        ConfigurationWorkspaceButton.Classes.Set("active", MainTabs.SelectedIndex == 4);
        WorkspaceTitleText.Text = MainTabs.SelectedIndex switch
        {
            1 => "棋盘与搜索", 2 => "API 服务与模型", 3 => "外部接管", 4 => "引擎插件", _ => "对弈"
        };
        WorkspaceHintText.Text = scene switch
        {
            WorkspaceScene.External => "实时棋盘 · 棋谱与分析 · 同步和落子控制",
            WorkspaceScene.Configuration => MainTabs.SelectedIndex switch
            {
                1 => "棋盘显示 · 执棋预算 · 分阶段时间",
                2 => "服务连接 · 启用模型 · 双方模型测试",
                _ => "引擎版本 · NNUE 权重 · 默认引擎"
            },
            _ => "棋盘 · 棋谱 · 双方执棋"
        };
        BoardActionsPanel.IsVisible = scene == WorkspaceScene.Play;
        LocalStatusCard.IsVisible = LocalControllerPanel.IsVisible = scene == WorkspaceScene.Play;
        // One analysis view follows the workspace, so live values and variation previews
        // stay intact without creating a second set of score controls or bindings.
        if (scene == WorkspaceScene.External)
        {
            EnsureRecognitionPrepared();
            if (!ReferenceEquals(ExternalReviewHost.Content, RecordPane))
            {
                WorkspaceGrid.Children.Remove(RecordPane);
                ExternalReviewHost.Content = RecordPane;
            }
            if (!ReferenceEquals(ExternalAnalysisHost.Content, AnalysisScroll))
            {
                AnalysisTab.Content = null;
                ExternalAnalysisHost.Content = AnalysisScroll;
            }
            if (!ReferenceEquals(ExternalThinkingHost.Content, LlmThinkingPanel))
            {
                DetailsPane.Children.Remove(LlmThinkingPanel);
                ExternalThinkingHost.Content = LlmThinkingPanel;
            }
        }
        else
        {
            if (!WorkspaceGrid.Children.Contains(RecordPane))
            {
                ExternalReviewHost.Content = null;
                WorkspaceGrid.Children.Add(RecordPane);
            }
            if (!ReferenceEquals(AnalysisTab.Content, AnalysisScroll))
            {
                ExternalAnalysisHost.Content = null;
                AnalysisTab.Content = AnalysisScroll;
            }
            if (!DetailsPane.Children.Contains(LlmThinkingPanel))
            {
                ExternalThinkingHost.Content = null;
                DetailsPane.Children.Add(LlmThinkingPanel);
            }
        }
        RecordEditorPanel.IsVisible = _setupBoard is null && scene == WorkspaceScene.Play;
        RefreshWorkspaceDensity();
        RefreshThinkingPanelVisibility();
        AnimateWorkspaceChange();
    }

    private void RefreshWorkspaceDensity()
    {
        // Keep room for scoring and the fixed takeover controls when thought history grows.
        var compact = ClientSize.Height < 800;
        var compactExternal = compact && _workspaceScene == WorkspaceScene.External;
        WorkspaceContextRow.IsVisible = !compactExternal;
        if (_compactExternalRows != compactExternal)
        {
            ExternalOptionsGrid.RowDefinitions = compactExternal ? new("30,30,30,30") : new("32,32,32,32");
            _compactExternalRows = compactExternal;
        }
        ExternalOptionsGrid.RowSpacing = compactExternal ? 4 : 6;
        ExternalOptionsCard.Padding = new Thickness(10, compactExternal ? 6 : 8);
        ExternalLiveControls.Spacing = compactExternal ? 5 : 8;
        ExternalOperationPanel.RowSpacing = compactExternal ? 6 : 10;
        ExternalWorkspacePanel.Margin = new Thickness(compactExternal ? 8 : 10);
        ExternalWorkspacePanel.RowSpacing = compactExternal ? 8 : 12;
        MainTabs.Padding = new Thickness(compactExternal ? 6 : 8);
        LlmThinkingPanel.Padding = new Thickness(11, compactExternal ? 6 : 9);
        ThinkingLayout.RowSpacing = compactExternal ? 4 : 6;
        ThinkingScroll.MaxHeight = _workspaceScene == WorkspaceScene.External
            ? compact ? 40 : 110 : compact ? 80 : 170;
        ExternalAnalysisPane.MaxHeight = double.PositiveInfinity;
    }

    private void RefreshThinkingPanelVisibility()
    {
        LlmThinkingPanel.IsVisible = _workspaceScene != WorkspaceScene.Configuration && _setupBoard is null &&
            (_redLlm || _blackLlm || _llmThoughtHistory.Count > 0 || (_externalLinked && ExternalControllerBox.SelectedIndex == 1));
        ExternalThinkingHost.IsVisible = _workspaceScene == WorkspaceScene.External && LlmThinkingPanel.IsVisible;
    }

    private void UpdateWorkspaceLayout(WorkspaceScene scene)
    {
        var sceneChanged = !_workspaceLayoutInitialized || _workspaceScene != scene;
        if (_workspaceScene != scene)
        {
            if (_workspaceScene == WorkspaceScene.Play)
                _localPaneWidths = WorkspaceGrid.ColumnDefinitions.Select(column => column.Width).ToArray();
            else if (_workspaceScene == WorkspaceScene.External)
            {
                _externalPaneWidths = WorkspaceGrid.ColumnDefinitions.Select(column => column.Width).ToArray();
                _externalReviewWidths = RecordSplitPanel.ColumnDefinitions.Select(column => column.Width).ToArray();
            }
            WorkspaceGrid.ColumnDefinitions = new(scene switch
            {
                WorkspaceScene.External => "1.07*,5,1.25*,0,0",
                WorkspaceScene.Configuration => "*,0,0,0,0",
                _ => "1.2*,5,240,5,1*"
            });
            var widths = scene == WorkspaceScene.Play ? _localPaneWidths : scene == WorkspaceScene.External ? _externalPaneWidths : null;
            if (widths is { Length: 5 })
                for (var i = 0; i < 5; i++) WorkspaceGrid.ColumnDefinitions[i].Width = widths[i];
            if (scene == WorkspaceScene.External)
            {
                WorkspaceGrid.ColumnDefinitions[0].MinWidth = 500;
                WorkspaceGrid.ColumnDefinitions[2].MinWidth = 580;
            }
            Grid.SetColumn(DetailsPane, scene switch
            {
                WorkspaceScene.Configuration => 0,
                WorkspaceScene.External => 2,
                _ => 4
            });
            Grid.SetColumnSpan(DetailsPane, scene == WorkspaceScene.Configuration ? 5 : 1);
            _workspaceScene = scene;
        }
        BoardPane.IsVisible = BoardPaneSplitter.IsVisible = scene != WorkspaceScene.Configuration;
        RecordPane.IsVisible = scene != WorkspaceScene.Configuration;
        DetailsPaneSplitter.IsVisible = scene == WorkspaceScene.Play;
        RecordPane.MinWidth = scene == WorkspaceScene.External ? 0 : 190;
        RecordPane.Padding = new Thickness(scene == WorkspaceScene.External ? 0 : 12);
        RecordPane.BorderThickness = new Thickness(scene == WorkspaceScene.External ? 0 : 1);
        RecordPane.CornerRadius = new CornerRadius(scene == WorkspaceScene.External ? 0 : 16);
        RecordContentPanel.RowSpacing = scene == WorkspaceScene.External ? 4 : 10;
        RecordContentPanel.Margin = scene == WorkspaceScene.External ? new Thickness(1, 3, 6, 0) : default;
        ExternalAnalysisPane.IsVisible = ExternalRecordAnalysisSplitter.IsVisible = ExternalRecordActions.IsVisible = scene == WorkspaceScene.External;
        // Keep grid definitions and user-adjusted splitter widths through every
        // move/search refresh. Replacing them repeatedly forces layout work.
        if (sceneChanged)
        {
            RecordSplitPanel.RowDefinitions = new(scene == WorkspaceScene.External ? "*" : "*,0,0");
            RecordSplitPanel.ColumnDefinitions = new(scene == WorkspaceScene.External ? "0.72*,8,1*" : "*");
            if (scene == WorkspaceScene.External && _externalReviewWidths is { Length: 3 })
                for (int i = 0; i < 3; i++) RecordSplitPanel.ColumnDefinitions[i].Width = _externalReviewWidths[i];
            _workspaceLayoutInitialized = true;
        }
        Grid.SetRow(ExternalAnalysisPane, scene == WorkspaceScene.External ? 0 : 2);
        Grid.SetColumn(ExternalAnalysisPane, scene == WorkspaceScene.External ? 2 : 0);
        Grid.SetRow(ExternalRecordAnalysisSplitter, scene == WorkspaceScene.External ? 0 : 1);
        Grid.SetColumn(ExternalRecordAnalysisSplitter, scene == WorkspaceScene.External ? 1 : 0);
        ExternalRecordAnalysisSplitter.ResizeDirection = scene == WorkspaceScene.External ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        ExternalRecordAnalysisSplitter.Width = scene == WorkspaceScene.External ? 5 : double.NaN;
        ExternalRecordAnalysisSplitter.Height = scene == WorkspaceScene.External ? double.NaN : 5;
        ExternalRecordAnalysisSplitter.VerticalAlignment = VerticalAlignment.Stretch;
        ToolTip.SetTip(ExternalRecordAnalysisSplitter, scene == WorkspaceScene.External
            ? "拖动调整棋谱与实时分析的宽度" : "拖动调整棋谱与实时分析的高度");
        ExternalAnalysisPane.BorderThickness = scene == WorkspaceScene.External ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        if (scene == WorkspaceScene.External)
        {
            RecordSplitPanel.ColumnDefinitions[0].MinWidth = 190;
            RecordSplitPanel.ColumnDefinitions[2].MinWidth = 240;
        }
        BoardHeadingText.Text = scene == WorkspaceScene.External ? "实时棋盘" : "对局棋盘";
        BoardFlipShortcut.IsVisible = scene == WorkspaceScene.External;
        BoardNavigation.IsVisible = scene != WorkspaceScene.External || (!_externalObserving && !_externalRunning);
        BoardSubtitle.IsVisible = BoardFooter.IsVisible = ImportFenButton.IsVisible = scene == WorkspaceScene.Play;
        BoardPane.Padding = new Thickness(scene == WorkspaceScene.External ? 10 : 12);
        Chart.Height = scene == WorkspaceScene.External ? 50 : 86;
        DetailsPane.RowSpacing = scene == WorkspaceScene.Play ? 8 : 0;
    }
}

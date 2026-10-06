using Avalonia.Controls;

namespace PaddiXiangqi.Views;

// Explicit view access keeps the session controller independent of page placement.
// New per-page state belongs in typed view models; this bridge supports the existing
// controller and automation names while the session workflows are migrated.
public partial class MainWindow
{
    private void InitializeWorkspaceViews()
    {
        WorkspaceView.ComposeNameScopes(this, SettingsPage, ModelServicesPage, EnginePluginsPage);
        SettingsPage.ExternalConfigurationSource = ExternalConfigPanel;
    }

    private void WorkspaceActionRequested(object? sender, WorkspaceActionEventArgs e)
    {
        switch (e.Action)
        {
            case WorkspaceAction.Benchmark: Benchmark_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.BlackLlmTest: BlackLlmTest_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.ClearHash: ClearHash_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EngineBrowse: EngineBrowse_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EngineEvalBrowse: EngineEvalBrowse_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EngineManage: EngineManage_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EnginePluginAdd: EnginePluginAdd_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EnginePluginDefault: EnginePluginDefault_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EnginePluginDelete: EnginePluginDelete_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EnginePluginExport: EnginePluginExport_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EnginePluginImport: EnginePluginImport_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EnginePluginProbe: EnginePluginProbe_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.EnginePluginSave: EnginePluginSave_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.ExternalCalibrate: ExternalCalibrate_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.ExternalEditPosition: ExternalEditPosition_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.ExternalLearnSkin: ExternalLearnSkin_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.ExternalRecognize: ExternalRecognize_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.ExternalStandard: ExternalStandard_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.ExternalUsePosition: ExternalUsePosition_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.LlmManualAdd: LlmManualAdd_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.LlmProfileAdd: LlmProfileAdd_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.LlmProfileDelete: LlmProfileDelete_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.LlmProfileFetch: LlmProfileFetch_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.PlayWorkspace: PlayWorkspace_Click(e.Source, e.OriginalArgs); break;
            case WorkspaceAction.RedLlmTest: RedLlmTest_Click(e.Source, e.OriginalArgs); break;
            default: throw new ArgumentOutOfRangeException(nameof(e.Action));
        }
    }

    private CheckBox ShowCoordinatesCheck => SettingsPage.Control<CheckBox>(nameof(ShowCoordinatesCheck));
    private NumericUpDown AnimationMsBox => SettingsPage.Control<NumericUpDown>(nameof(AnimationMsBox));
    private CheckBox AutoAnalyzeCheck => SettingsPage.Control<CheckBox>(nameof(AutoAnalyzeCheck));
    private TextBlock NativeRulesText => SettingsPage.Control<TextBlock>(nameof(NativeRulesText));
    private Button BenchmarkButton => SettingsPage.Control<Button>(nameof(BenchmarkButton));
    private Button ConfiguredBenchmarkButton => SettingsPage.Control<Button>(nameof(ConfiguredBenchmarkButton));
    private TextBlock BenchmarkResultText => SettingsPage.Control<TextBlock>(nameof(BenchmarkResultText));
    private ScrollViewer ExternalConfigurationScroll => SettingsPage.Control<ScrollViewer>(nameof(ExternalConfigurationScroll));
    private TextBlock ExternalCalibrationText => SettingsPage.Control<TextBlock>(nameof(ExternalCalibrationText));
    private TextBlock ExternalDeliveryHintText => SettingsPage.Control<TextBlock>(nameof(ExternalDeliveryHintText));
    private TextBlock ExternalScoreHintText => SettingsPage.Control<TextBlock>(nameof(ExternalScoreHintText));
    private Image ExternalPreviewImage => SettingsPage.Control<Image>(nameof(ExternalPreviewImage));
    private SafeComboBox ExternalSkinBox => SettingsPage.Control<SafeComboBox>(nameof(ExternalSkinBox));
    private TextBox ExternalSkinNameBox => SettingsPage.Control<TextBox>(nameof(ExternalSkinNameBox));
    private Grid ExternalFenActions => SettingsPage.Control<Grid>(nameof(ExternalFenActions));
    private TextBox ExternalFenBox => SettingsPage.Control<TextBox>(nameof(ExternalFenBox));
    private TextBlock LevelText => SettingsPage.Control<TextBlock>(nameof(LevelText));
    private Slider LevelSlider => SettingsPage.Control<Slider>(nameof(LevelSlider));
    private TextBlock LevelHintText => SettingsPage.Control<TextBlock>(nameof(LevelHintText));
    private TextBlock EffectiveSettingsText => SettingsPage.Control<TextBlock>(nameof(EffectiveSettingsText));
    private NumericUpDown ThreadsBox => SettingsPage.Control<NumericUpDown>(nameof(ThreadsBox));
    private TextBlock ThreadBudgetHintText => SettingsPage.Control<TextBlock>(nameof(ThreadBudgetHintText));
    private NumericUpDown HashBox => SettingsPage.Control<NumericUpDown>(nameof(HashBox));
    private NumericUpDown AnalysisLinesBox => SettingsPage.Control<NumericUpDown>(nameof(AnalysisLinesBox));
    private NumericUpDown ThinkBox => SettingsPage.Control<NumericUpDown>(nameof(ThinkBox));
    private CheckBox DepthLimitCheck => SettingsPage.Control<CheckBox>(nameof(DepthLimitCheck));
    private NumericUpDown DepthBox => SettingsPage.Control<NumericUpDown>(nameof(DepthBox));
    private CheckBox PhaseTimeCheck => SettingsPage.Control<CheckBox>(nameof(PhaseTimeCheck));
    private StackPanel PhaseTimeControls => SettingsPage.Control<StackPanel>(nameof(PhaseTimeControls));
    private NumericUpDown OpeningTimeBox => SettingsPage.Control<NumericUpDown>(nameof(OpeningTimeBox));
    private NumericUpDown MiddleTimeBox => SettingsPage.Control<NumericUpDown>(nameof(MiddleTimeBox));
    private NumericUpDown EndgameTimeBox => SettingsPage.Control<NumericUpDown>(nameof(EndgameTimeBox));
    private NumericUpDown OpeningRoundsBox => SettingsPage.Control<NumericUpDown>(nameof(OpeningRoundsBox));
    private NumericUpDown EndgameAttackersBox => SettingsPage.Control<NumericUpDown>(nameof(EndgameAttackersBox));
    private TextBlock CurrentPhaseText => SettingsPage.Control<TextBlock>(nameof(CurrentPhaseText));
    private ScrollViewer ModelServicesScroll => ModelServicesPage.Control<ScrollViewer>(nameof(ModelServicesScroll));
    private SafeComboBox LlmProfileList => ModelServicesPage.Control<SafeComboBox>(nameof(LlmProfileList));
    private Button LlmProfileAddButton => ModelServicesPage.Control<Button>(nameof(LlmProfileAddButton));
    private Button LlmProfileDeleteButton => ModelServicesPage.Control<Button>(nameof(LlmProfileDeleteButton));
    private TextBox LlmProfileNameBox => ModelServicesPage.Control<TextBox>(nameof(LlmProfileNameBox));
    private TextBox LlmProfileBaseUrlBox => ModelServicesPage.Control<TextBox>(nameof(LlmProfileBaseUrlBox));
    private TextBox LlmProfileApiKeyBox => ModelServicesPage.Control<TextBox>(nameof(LlmProfileApiKeyBox));
    private Button LlmProfileFetchButton => ModelServicesPage.Control<Button>(nameof(LlmProfileFetchButton));
    private TextBlock LlmProfileStatusText => ModelServicesPage.Control<TextBlock>(nameof(LlmProfileStatusText));
    private TextBox LlmModelFilterBox => ModelServicesPage.Control<TextBox>(nameof(LlmModelFilterBox));
    private TextBlock LlmModelsEmptyText => ModelServicesPage.Control<TextBlock>(nameof(LlmModelsEmptyText));
    private ListBox LlmProfileModelsPanel => ModelServicesPage.Control<ListBox>(nameof(LlmProfileModelsPanel));
    private TextBox LlmManualModelBox => ModelServicesPage.Control<TextBox>(nameof(LlmManualModelBox));
    private Button LlmManualAddButton => ModelServicesPage.Control<Button>(nameof(LlmManualAddButton));
    private Grid LlmSideTestsPanel => ModelServicesPage.Control<Grid>(nameof(LlmSideTestsPanel));
    private Button RedLlmTestButton => ModelServicesPage.Control<Button>(nameof(RedLlmTestButton));
    private TextBlock RedLlmConfigStatusText => ModelServicesPage.Control<TextBlock>(nameof(RedLlmConfigStatusText));
    private Button BlackLlmTestButton => ModelServicesPage.Control<Button>(nameof(BlackLlmTestButton));
    private TextBlock BlackLlmConfigStatusText => ModelServicesPage.Control<TextBlock>(nameof(BlackLlmConfigStatusText));
    private TextBlock DefaultEngineText => EnginePluginsPage.Control<TextBlock>(nameof(DefaultEngineText));
    private ListBox EnginePluginList => EnginePluginsPage.Control<ListBox>(nameof(EnginePluginList));
    private Button EnginePluginAddButton => EnginePluginsPage.Control<Button>(nameof(EnginePluginAddButton));
    private Button EnginePluginImportButton => EnginePluginsPage.Control<Button>(nameof(EnginePluginImportButton));
    private TextBox EnginePluginNameBox => EnginePluginsPage.Control<TextBox>(nameof(EnginePluginNameBox));
    private TextBox EnginePathBox => EnginePluginsPage.Control<TextBox>(nameof(EnginePathBox));
    private Button EngineBrowseButton => EnginePluginsPage.Control<Button>(nameof(EngineBrowseButton));
    private TextBox EngineEvalBox => EnginePluginsPage.Control<TextBox>(nameof(EngineEvalBox));
    private Button EngineEvalBrowseButton => EnginePluginsPage.Control<Button>(nameof(EngineEvalBrowseButton));
    private TextBlock EnginePluginInfoText => EnginePluginsPage.Control<TextBlock>(nameof(EnginePluginInfoText));
    private TextBlock EngineRuleStatusText => EnginePluginsPage.Control<TextBlock>(nameof(EngineRuleStatusText));
    private ItemsControl EngineRuleOptionsPanel => EnginePluginsPage.Control<ItemsControl>(nameof(EngineRuleOptionsPanel));
    private Button EnginePluginProbeButton => EnginePluginsPage.Control<Button>(nameof(EnginePluginProbeButton));
    private Button EnginePluginDefaultButton => EnginePluginsPage.Control<Button>(nameof(EnginePluginDefaultButton));
    private Button EnginePluginSaveButton => EnginePluginsPage.Control<Button>(nameof(EnginePluginSaveButton));
    private Button EnginePluginExportButton => EnginePluginsPage.Control<Button>(nameof(EnginePluginExportButton));
    private Button EnginePluginDeleteButton => EnginePluginsPage.Control<Button>(nameof(EnginePluginDeleteButton));
    private TextBlock EnginePluginStatusText => EnginePluginsPage.Control<TextBlock>(nameof(EnginePluginStatusText));
}

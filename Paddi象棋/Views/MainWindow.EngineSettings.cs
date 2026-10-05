using Avalonia.Controls.Primitives;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private void InitializeEnginePhaseControls()
    {
        DepthLimitCheck.PropertyChanged += (_, args) =>
        {
            if (!_ready || args.Property != ToggleButton.IsCheckedProperty) return;
            RefreshEngineSettingsSummary();
            SaveSettings();
        };
        var phases = _preferences.EnginePhases;
        PhaseTimeCheck.IsChecked = phases.Enabled;
        OpeningTimeBox.Value = Math.Clamp(phases.OpeningTimeMs, 200, 120_000) / 1000m;
        MiddleTimeBox.Value = Math.Clamp(phases.MiddleTimeMs, 200, 120_000) / 1000m;
        EndgameTimeBox.Value = Math.Clamp(phases.EndgameTimeMs, 200, 120_000) / 1000m;
        OpeningRoundsBox.Value = Math.Clamp(phases.OpeningFullmoves, 1, 40);
        EndgameAttackersBox.Value = phases.MaterialThreshold;
        PhaseTimeCheck.PropertyChanged += (_, args) =>
        {
            if (!_ready || args.Property != ToggleButton.IsCheckedProperty) return;
            RefreshEngineSettingsSummary();
            SaveSettings();
        };
        foreach (var setting in new[] { OpeningTimeBox, MiddleTimeBox, EndgameTimeBox, OpeningRoundsBox, EndgameAttackersBox })
            setting.ValueChanged += (_, _) =>
            {
                if (!_ready) return;
                RefreshEngineSettingsSummary();
                SaveSettings();
            };
    }

    private EnginePhaseSettings ReadEnginePhases() => new()
    {
        Enabled = PhaseTimeCheck.IsChecked == true,
        OpeningTimeMs = (int)((OpeningTimeBox.Value ?? .8m) * 1000),
        MiddleTimeMs = (int)((MiddleTimeBox.Value ?? 5) * 1000),
        EndgameTimeMs = (int)((EndgameTimeBox.Value ?? 5) * 1000),
        OpeningFullmoves = (int)(OpeningRoundsBox.Value ?? 10),
        EndgameAttackers = (int)(EndgameAttackersBox.Value ?? 6),
        EndgameMaterialPoints = (int)(EndgameAttackersBox.Value ?? 6)
    };

    private EngineSettings ReadPlayingEngineSettings() => ReadEnginePhases().Apply(_game, ReadEngineSettings());

    private string PlayingBudgetDescription(EngineSettings settings)
    {
        var phases = ReadEnginePhases();
        var phase = phases.Enabled && settings.Level == 20 ? EnginePhaseSettings.DisplayName(phases.Phase(_game)) + " · " : "";
        return $"{phase}最长 {settings.MoveTimeMs / 1000.0:0.#} 秒 · " +
            (settings.SearchDepthLimit is int depth ? $"深度上限 {depth}" : "按时间搜索，不附加深度上限");
    }
}

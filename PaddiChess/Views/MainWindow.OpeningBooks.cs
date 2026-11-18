using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Views;

public partial class MainWindow
{
    private OpeningBookStore? _openingBookStore;
    private Task? _openingBookPreparation;
    private CheckBox BookEnabledCheck => SettingsPage.Control<CheckBox>(nameof(BookEnabledCheck));
    private ComboBox BookChoiceBox => SettingsPage.Control<ComboBox>(nameof(BookChoiceBox));
    private ComboBox BookPolicyBox => SettingsPage.Control<ComboBox>(nameof(BookPolicyBox));
    private NumericUpDown BookRoundsBox => SettingsPage.Control<NumericUpDown>(nameof(BookRoundsBox));
    private TextBlock BookStatusText => SettingsPage.Control<TextBlock>(nameof(BookStatusText));
    private Button BookRemoveButton => SettingsPage.Control<Button>(nameof(BookRemoveButton));

    private void InitializeOpeningBooks()
    {
        _openingBookStore = new(AppDataPaths.Current.DataDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Paddi象棋", "Cache", "OpeningBooks"));
        BookEnabledCheck.IsChecked = _preferences.OpeningBookEnabled;
        BookRoundsBox.Value = Math.Clamp(_preferences.OpeningBookMaxFullmoves, 1, 40);
        BookPolicyBox.SelectedIndex = _preferences.OpeningBookRandomBest ? 1 : 0;
        RefreshBookChoices();
        BookChoiceBox.SelectionChanged += (_, _) => BookSettingsChanged();
        BookPolicyBox.SelectionChanged += (_, _) => BookSettingsChanged();
        BookRoundsBox.ValueChanged += (_, _) => BookSettingsChanged();
        BookEnabledCheck.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) BookSettingsChanged(); };
        if (_preferences.OpeningBookEnabled) _openingBookPreparation = PrepareOpeningBookAsync();
    }
    private async Task PrepareOpeningBookAsync()
    {
        try
        {
            await Task.Run(() => _openingBookStore!.PrepareBundledAsync());
            if (!_closing && BookChoiceBox.SelectedItem is OpeningBookProfile { Id: OpeningBookStore.BundledId }) BookStatusText.Text = "内置库已就绪 · 2,688,227 条库招";
        }
        catch (Exception e) { if (!_closing) BookStatusText.Text = "内置库未就绪，将使用引擎：" + e.Message; }
    }
    private void RefreshBookChoices()
    {
        var profiles = new[] { new OpeningBookProfile(OpeningBookStore.BundledId, OpeningBookStore.BundledName, "") }
            .Concat(_preferences.OpeningBooks).ToArray();
        BookChoiceBox.ItemsSource = profiles;
        BookChoiceBox.SelectedItem = profiles.FirstOrDefault(p => p.Id == _preferences.OpeningBookId) ?? profiles[0];
        BookRemoveButton.IsEnabled = BookChoiceBox.SelectedItem is OpeningBookProfile p && p.Id != OpeningBookStore.BundledId;
    }
    private void BookSettingsChanged()
    {
        if (!_ready || BookChoiceBox.SelectedItem is not OpeningBookProfile profile) return;
        _preferences.OpeningBookEnabled = BookEnabledCheck.IsChecked == true;
        if (_preferences.OpeningBookEnabled && _openingBookPreparation is null) _openingBookPreparation = PrepareOpeningBookAsync();
        _preferences.OpeningBookId = profile.Id;
        _preferences.OpeningBookRandomBest = BookPolicyBox.SelectedIndex == 1;
        _preferences.OpeningBookMaxFullmoves = (int)(BookRoundsBox.Value ?? 20);
        BookRemoveButton.IsEnabled = profile.Id != OpeningBookStore.BundledId;
        BookStatusText.Text = _preferences.OpeningBookEnabled ? "当前开局库：" + profile : "已停用开局库，使用引擎搜索";
        CancelSearch(); SaveSettings(); MaybeStartSearch();
    }
    private OpeningBookOptions? ReadOpeningBookOptions()
    {
        if (BookEnabledCheck.IsChecked != true || BookChoiceBox.SelectedItem is not OpeningBookProfile profile || _openingBookStore is null) return null;
        return new(_openingBookStore.Resolve(profile), profile.Name, (int)(BookRoundsBox.Value ?? 20), BookPolicyBox.SelectedIndex == 1);
    }
    private async void ImportOpeningBook()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "导入本地开局库", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("象棋开局库（XQB / OBK / BIN / DAT）") { Patterns = ["*.xqb", "*.obk", "*.bin", "*.dat"] }]
        });
        if (files.Count == 0) return;
        SettingsPage.Control<Button>("BookImportButton").IsEnabled = false;
        var failures = new List<string>(); var imported = 0;
        try
        {
            foreach (var file in files)
            {
                try
                {
                    var path = file.TryGetLocalPath() ?? throw new IOException("请选择本地文件。");
                    BookStatusText.Text = "正在导入并检查：" + file.Name;
                    var profile = await _openingBookStore!.ImportAsync(path);
                    _preferences.OpeningBooks.Add(profile); _preferences.OpeningBookId = profile.Id; imported++;
                }
                catch (Exception e) { failures.Add(file.Name + "：" + e.Message); }
            }
            RefreshBookChoices(); BookSettingsChanged(); SaveSettings();
            BookStatusText.Text = $"已导入 {imported} 个开局库（已复制到本机数据目录）。" + string.Join("；", failures);
        }
        finally { SettingsPage.Control<Button>("BookImportButton").IsEnabled = true; }
    }
    private void RemoveOpeningBook()
    {
        if (BookChoiceBox.SelectedItem is not OpeningBookProfile profile || profile.Id == OpeningBookStore.BundledId) return;
        // Unregister without deleting the user's source file or interrupting a reader.
        _preferences.OpeningBooks.RemoveAll(p => p.Id == profile.Id);
        _preferences.OpeningBookId = OpeningBookStore.BundledId;
        RefreshBookChoices(); BookSettingsChanged();
        BookStatusText.Text = "已移除列表项，原始文件未删除。";
    }
    private void ShowOpeningBookHit(OpeningBookHit hit)
    {
        EngineStatusText.Text = "开局库命中 · " + hit.Name;
        ScoreText.Text = hit.ValueLabel + " " + hit.BookScore;
        DepthText.Text = NodesText.Text = NpsText.Text = "—";
        WdlText.Text = hit.ValueLabel + "用于库内选招，非引擎实时评分";
        BestMoveText.Text = DescribeUci(hit.Move);
        PvText.Text = BestMoveText.Text;
        ResetVariationChoices();
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using PaddiXiangqi.Core;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Views;

public partial class ExternalHistoryWindow : Window
{
    private readonly string _folder;
    private readonly string _recovery;
    private readonly bool _canReplay;
    private bool _closed;
    private bool _replaying;
    private int _refreshVersion;
    private int _selectionVersion;
    private ExternalHistoryEvent? _previewEvent;
    private string _previewSource = "current";

    public ExternalHistoryWindow() : this(Path.Combine(AppDataPaths.Current.DataDirectory, "ExternalHistory"),
        Path.Combine(AppDataPaths.Current.DataDirectory, "Recovery"), false) { }

    public ExternalHistoryWindow(string folder, string recovery, bool canReplay)
    {
        InitializeComponent();
        _folder = folder; _recovery = recovery; _canReplay = canReplay;
        if (!canReplay) HistoryHint.Text += " 当前保持连接，可查看记录；断开后可加载棋谱复盘。";
        Opened += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _closed = true;
    }

    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        try
        {
            HistoryStatus.Text = "正在读取本机记录…";
            var records = await ExternalHistoryStore.ListAsync(_folder, _recovery);
            if (_closed || version != _refreshVersion) return;
            SessionsList.ItemsSource = records;
            HistoryStatus.Text = records.Count == 0 ? "尚无自动保存的接管记录。" : $"最近 {records.Count} 份记录 · 仅保存于本机";
            if (records.Count > 0) SessionsList.SelectedIndex = 0;
        }
        catch (Exception error)
        { if (!_closed && version == _refreshVersion) HistoryStatus.Text = "读取记录失败：" + error.Message; }
    }

    private async void Session_Changed(object? sender, SelectionChangedEventArgs e)
    {
        var version = ++_selectionVersion;
        ReplayButton.IsEnabled = _canReplay && !_replaying && SessionsList.SelectedItem is ExternalHistoryItem;
        EventsList.ItemsSource = null; EventDetails.Text = "";
        SetPreviewEvent(null);
        if (SessionsList.SelectedItem is not ExternalHistoryItem item) return;
        try
        {
            var events = await ExternalHistoryStore.ReadEventsAsync(item.EventsPath);
            if (_closed || version != _selectionVersion) return;
            EventsList.ItemsSource = events;
            if (events.Count > 0) EventsList.SelectedIndex = events.Count - 1;
            else EventDetails.Text = "这份旧恢复记录只包含棋谱，没有当时的识别和发送事件。\n\n文件：" + item.RecordPath;
        }
        catch (Exception error) { if (version == _selectionVersion) EventDetails.Text = "读取事件失败：" + error.Message; }
    }

    private void Event_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (EventsList.SelectedItem is not ExternalHistoryEvent entry) { SetPreviewEvent(null); return; }
        SetPreviewEvent(entry);
        EventDetails.Text = $"{entry.At:yyyy-MM-dd HH:mm:ss.fff} · #{entry.Sequence} · {entry.Label}\n" +
            $"{entry.Message}\n\n程序局面（第 {entry.Ply} 手）：\n{entry.Fen}" +
            (entry.BeforeFen is null ? "" : $"\n\n变化前局面：\n{entry.BeforeFen}") +
            (entry.ObservedFen is null ? "" : $"\n\n图像识别局面：\n{entry.ObservedFen}") +
            (entry.Move is null ? "" : $"\n\n选择 / 发送着法：{entry.Move}") +
            (entry.Candidates.Length == 0 ? "" : $"\n候选 / 确认着法：{string.Join(" ", entry.Candidates)}");
    }

    private string? PreviewFen(string source) => source switch
    {
        "before" => _previewEvent?.BeforeFen,
        "observed" => _previewEvent?.ObservedFen,
        _ => _previewEvent?.Fen
    };

    private void SetPreviewEvent(ExternalHistoryEvent? entry)
    {
        _previewEvent = entry;
        ProgramPositionButton.IsEnabled = !string.IsNullOrWhiteSpace(entry?.Fen);
        BeforePositionButton.IsEnabled = !string.IsNullOrWhiteSpace(entry?.BeforeFen);
        RecognizedPositionButton.IsEnabled = !string.IsNullOrWhiteSpace(entry?.ObservedFen);
        if (entry is not null && string.IsNullOrWhiteSpace(PreviewFen(_previewSource)))
            _previewSource = ProgramPositionButton.IsEnabled ? "current" : BeforePositionButton.IsEnabled ? "before" : "observed";
        ShowEventBoard();
    }

    private void ShowEventBoard()
    {
        EventBoard.Game = null;
        PreviewFlipButton.IsEnabled = false;
        PreviewEmptyText.IsVisible = true;
        ProgramPositionButton.Classes.Set("active", _previewSource == "current" && ProgramPositionButton.IsEnabled);
        BeforePositionButton.Classes.Set("active", _previewSource == "before" && BeforePositionButton.IsEnabled);
        RecognizedPositionButton.Classes.Set("active", _previewSource == "observed" && RecognizedPositionButton.IsEnabled);
        var fen = PreviewFen(_previewSource);
        if (string.IsNullOrWhiteSpace(fen))
        {
            PreviewEmptyText.Text = "选择事件查看当时的棋盘";
            PreviewCaption.Text = "仅查看记录中的局面";
        }
        else
        {
            try
            {
                var game = new XiangqiGame { ExternalAdjudication = true };
                game.LoadFen(fen);
                EventBoard.Game = game;
                PreviewEmptyText.IsVisible = false;
                PreviewFlipButton.IsEnabled = true;
                PreviewCaption.Text = _previewSource == "observed"
                    ? "显示识别到的棋子；行棋方以确认记录为准"
                    : (_previewSource == "before" ? "变化前保留的局面" : "程序采用的局面") +
                        $" · 轮到{(game.RedToMove ? "红" : "黑")}方";
            }
            catch (FormatException error)
            {
                PreviewEmptyText.Text = "这份局面无法绘制：" + error.Message;
                PreviewCaption.Text = "原始识别内容仍保留在左侧事件详情中";
            }
        }
        EventBoard.Refresh();
    }

    private void PreviewSource_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { IsEnabled: true, Tag: string source }) return;
        _previewSource = source;
        ShowEventBoard();
    }

    private void PreviewFlip_Click(object? sender, RoutedEventArgs e)
    {
        EventBoard.Flipped = !EventBoard.Flipped;
        EventBoard.Refresh();
    }

    private async void Replay_Click(object? sender, RoutedEventArgs e)
    {
        if (!_canReplay || _replaying || SessionsList.SelectedItem is not ExternalHistoryItem item) return;
        _replaying = true;
        var version = _selectionVersion;
        ReplayButton.IsEnabled = false;
        try
        {
            await using var stream = ExternalHistoryStore.OpenRecordRead(item.RecordPath);
            var record = await GameRecordStorage.ReadValidatedAsync(stream, CancellationToken.None);
            if (!_closed && version == _selectionVersion) Close(record);
        }
        catch (Exception error) { HistoryStatus.Text = "这份棋谱无法加载：" + error.Message; }
        finally
        {
            _replaying = false;
            if (!_closed) ReplayButton.IsEnabled = _canReplay && SessionsList.SelectedItem is ExternalHistoryItem;
        }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

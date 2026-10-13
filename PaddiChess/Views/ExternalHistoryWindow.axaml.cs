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
    private int _eventPage, _sessionPage;
    private HistoryCleanupPlan? _cleanup;
    private bool _closed;
    private bool _replaying;
    private int _refreshVersion;
    private int _selectionVersion;
    private CancellationTokenSource? _refreshCancellation, _eventsCancellation, _replayCancellation;
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
        Closed += (_, _) =>
        {
            _closed = true;
            _refreshCancellation?.Cancel();
            _eventsCancellation?.Cancel();
            _replayCancellation?.Cancel();
        };
    }

    private async Task RefreshAsync()
    {
        if (_closed) return;
        _refreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        var version = ++_refreshVersion;
        try
        {
            HistoryStatus.Text = "正在读取本机记录…";
            var records = await ExternalHistoryStore.ListAsync(_folder, _recovery, cancellation.Token, _sessionPage);
            if (_closed || version != _refreshVersion) return;
            SessionsList.ItemsSource = records;
            PreviousSessions.IsEnabled = _sessionPage > 0; NextSessions.IsEnabled = records.Count == 500;
            SessionPageText.Text = $"会话 · 第 {_sessionPage + 1} 页";
            HistoryStatus.Text = records.Count == 0 ? "尚无自动保存的接管记录。" : $"最近 {records.Count} 份记录 · 仅保存于本机";
            if (records.Count > 0) SessionsList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        { if (!_closed && version == _refreshVersion) HistoryStatus.Text = "读取记录失败：" + error.Message; }
        finally { if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null; }
    }

    private async void Session_Changed(object? sender, SelectionChangedEventArgs e)
    {
        _eventPage = -1;
        await LoadEventPageAsync();
    }

    private async Task LoadEventPageAsync()
    {
        _eventsCancellation?.Cancel();
        _replayCancellation?.Cancel();
        var version = ++_selectionVersion;
        ReplayButton.IsEnabled = _canReplay && !_replaying && SessionsList.SelectedItem is ExternalHistoryItem;
        EventsList.ItemsSource = null; EventDetails.Text = "";
        SetPreviewEvent(null);
        if (_closed || SessionsList.SelectedItem is not ExternalHistoryItem item) return;
        using var cancellation = new CancellationTokenSource();
        _eventsCancellation = cancellation;
        try
        {
            var page = _eventPage < 0 ? await ExternalHistoryPaging.ReadLastAsync(item.EventsPath, cancellation.Token)
                : await ExternalHistoryPaging.ReadAsync(item.EventsPath, _eventPage, cancellation.Token);
            var events = page.Events;
            if (_closed || version != _selectionVersion) return;
            _eventPage = page.Number;
            EventsList.ItemsSource = events;
            PreviousEvents.IsEnabled = _eventPage > 0; NextEvents.IsEnabled = page.HasNext;
            EventPageText.Text = $"事件 · 第 {_eventPage + 1} 页";
            if (events.Count > 0) EventsList.SelectedIndex = events.Count - 1;
            else EventDetails.Text = "这份旧恢复记录只包含棋谱，没有当时的识别和发送事件。\n\n文件：" + item.RecordPath;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed && version == _selectionVersion) EventDetails.Text = "读取事件失败：" + error.Message; }
        finally { if (ReferenceEquals(_eventsCancellation, cancellation)) _eventsCancellation = null; }
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
        using var cancellation = new CancellationTokenSource();
        _replayCancellation = cancellation;
        ReplayButton.IsEnabled = false;
        try
        {
            await using var stream = ExternalHistoryStore.OpenRecordRead(item.RecordPath);
            var record = await GameRecordStorage.ReadValidatedAsync(stream, cancellation.Token);
            if (!_closed && version == _selectionVersion) Close(record);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed && version == _selectionVersion) HistoryStatus.Text = "这份棋谱无法加载：" + error.Message; }
        finally
        {
            _replaying = false;
            if (ReferenceEquals(_replayCancellation, cancellation)) _replayCancellation = null;
            if (!_closed) ReplayButton.IsEnabled = _canReplay && SessionsList.SelectedItem is ExternalHistoryItem;
        }
    }

    private async void EventPage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string step }) return;
        _eventPage = step == "last" ? -1 : Math.Max(0, _eventPage + int.Parse(step));
        await LoadEventPageAsync();
    }
    private async void SessionPage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string step }) return;
        _sessionPage = Math.Max(0, _sessionPage + int.Parse(step));
        await RefreshAsync();
    }
    private void Retention_Changed(object? sender, SelectionChangedEventArgs e)
    {
        _cleanup = null;
        if (ConfirmCleanup != null) ConfirmCleanup.IsEnabled = false;
    }
    private async void PlanCleanup_Click(object? sender, RoutedEventArgs e)
    {
        var days = RetentionChoice.SelectedIndex switch { 1 => 7, 2 => 30, 3 => 90, _ => 0 };
        var choice = RetentionChoice.SelectedIndex;
        if (days == 0) { HistoryStatus.Text = "保留全部记录；仅点击确认清理才会删除旧记录。"; return; }
        try
        {
            var plan = await ExternalHistoryPaging.PlanCleanupAsync(_folder, days);
            if (_closed || choice != RetentionChoice.SelectedIndex) return;
            _cleanup = plan;
            HistoryStatus.Text = $"待清理 {_cleanup.Directories.Count} 份、约 {_cleanup.Bytes / 1048576d:F1} MB。正在记录的会话会跳过；删除无法撤销。";
            ConfirmCleanup.IsEnabled = _cleanup.Directories.Count > 0;
        }
        catch (Exception error) { if (!_closed) HistoryStatus.Text = "检查失败：" + error.Message; }
    }
    private async void ConfirmCleanup_Click(object? sender, RoutedEventArgs e)
    {
        if (_cleanup is not { } plan) return;
        _cleanup = null; ConfirmCleanup.IsEnabled = false;
        try
        {
            var count = await ExternalHistoryPaging.DeleteAsync(plan);
            if (_closed) return;
            _sessionPage = 0; await RefreshAsync();
            HistoryStatus.Text = $"已清理 {count} 份旧记录；正在记录的会话保留。";
        }
        catch (Exception error) { if (!_closed) HistoryStatus.Text = "清理失败：" + error.Message; }
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

using Avalonia.Controls;
using Avalonia.Threading;
using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Views;

public sealed record ExternalPositionEditorContext(string Fen, string Message, IReadOnlyList<Square>? Uncertain,
    ExternalFrame? Frame, BoardCalibration? Geometry, bool RedAtTop, int TurnIndex, bool StandardOpening = false);
public sealed record ExternalPositionEditResult(string Fen, ExternalFrame? Frame, BoardCalibration? Geometry,
    bool RedAtTop, bool RedToMove, bool GeometryChanged);

public partial class ExternalPositionEditorWindow : Window
{
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenRegistration _cancelRegistration;
    private Func<bool> _canAcceptCurrentFrame = () => true;
    public PositionEditorState Editor { get; }
    // Parameterless construction supports the Avalonia designer/runtime XAML loader.
    public ExternalPositionEditorWindow()
    {
        InitializeComponent();
        _lifetime = new CancellationTokenSource();
        Editor = new(new char[10, 9]);
        InitializeEditing();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    public ExternalPositionEditorWindow(ExternalPositionEditorContext context, IExternalDesktop? desktop,
        Func<ExternalFrame, BoardCalibration, bool, CancellationToken, bool, Task<SkinRecognition?>> recognize,
        CancellationToken cancellation)
    {
        InitializeComponent();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var fen = context.StandardOpening ? XiangqiGame.InitialFen : context.Fen;
        var message = context.Message; var uncertain = context.Uncertain;
        var openingDraft = context.StandardOpening;
        var pieces = new char[10, 9];
        void ReadPieces(string value)
        {
            Array.Clear(pieces);
            var rows = value.Split(' ')[0].Split('/');
            if (rows.Length != 10) return;
            for (var r = 0; r < 10; r++)
            {
                var f = 0;
                foreach (var c in rows[r])
                    if (c is >= '1' and <= '9') f += c - '0';
                    else if (f < 9 && "rnbakcpRNBAKCP".Contains(c)) pieces[r, f++] = c;
            }
        }
        ReadPieces(fen);
        Editor = new(pieces);
        InitializeEditing();
        var frame = context.Frame;
        var geometry = context.Geometry;
        var redAtTop = context.RedAtTop;
        var view = ExternalEditorBoard; view.SetupBoard = pieces; view.Flipped = redAtTop;
        if (uncertain is { Count: > 0 }) view.Selected = uncertain[0];
        var lifetime = _lifetime;
        CancellationTokenSource? work = null;
        var revision = 0;
        var closed = false;
        var picking = false;
        var busy = false;
        _canAcceptCurrentFrame = () => !busy && !picking && (frame == null || geometry != null);
        var changedGeometry = false;
        var help = ExternalEditorStatus;
        help.Text = message + " 如整排错位，请直接在这里重新定位。";
        var locate = ExternalEditorLocate; var manual = ExternalEditorManual;
        var refresh = ExternalEditorRefresh; var flip = ExternalEditorFlip;
        var ocr = ExternalEditorOcr; var turn = ExternalEditorTurn;
        flip.Content = redAtTop ? "朝向：红上黑下 ↕" : "朝向：黑上红下 ↕";
        turn.ItemsSource = new[] { "当前轮到红方", "当前轮到黑方" };
        turn.SelectedIndex = context.StandardOpening ? 0 : context.TurnIndex;
        var preview = frame == null ? null : ExternalEditorCalibration;
        ExternalEditorNoImage.IsVisible = preview == null;
        if (preview != null) { preview.SetImage(frame!.Png); if (geometry != null) preview.Set(geometry); }
        locate.IsEnabled = manual.IsEnabled = refresh.IsEnabled = ocr.IsEnabled = preview != null;
        flip.IsEnabled = true;
        view.SquareClicked += square =>
        {
            if (busy || picking) return;
            Editor.Click(square);
        };
        var cancel = ExternalEditorCancel; var accept = ExternalEditorAccept;
        var dialog = this;
        _cancelRegistration = lifetime.Token.Register(() => Dispatcher.UIThread.Post(() => { if (!closed) dialog.Close(); }));
        void CancelWork()
        {
            work?.Cancel(); work?.Dispose(); work = null; revision++;
            busy = false;
        }
        async Task ReadAgainAsync(bool relocate, bool fetch, bool useOcr = false, bool? desiredDirection = null)
        {
            if (frame == null || preview == null || closed) return;
            CancelWork();
            work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            work.CancelAfter(TimeSpan.FromSeconds(useOcr ? 12 : 15));
            var ct = work.Token; var operation = revision;
            busy = true; accept.IsEnabled = false; ExternalEditorEditBar.IsEnabled = false;
            picking = false; preview.IsPicking = false;
            help.Text = relocate ? "正在刷新截图、重新定位棋盘…" : useOcr ? "正在本机识字；可随时重新定位或取消。" : "正在重新识别当前截图…";
            try
            {
                var next = frame;
                var nextGeometry = geometry;
                var nextDirection = desiredDirection ?? redAtTop;
                var relocating = false;
                if (fetch && desktop != null) next = await desktop.CaptureAsync(frame.Window,ct);
                ct.ThrowIfCancellationRequested();
                if (next.Window.Id != frame.Window.Id || next.Window.Pid != frame.Window.Pid) throw new InvalidOperationException("目标窗口已替换，请重新选择。");
                var reusable = false;
                if (nextGeometry != null &&
                    ExternalCaptureGeometry.TryRebaseCalibration(frame, next, nextGeometry, out var mappedGeometry))
                {
                    // A backing-scale switch only changes screenshot pixels.
                    // Keep the user's corrected grid and board orientation.
                    nextGeometry = mappedGeometry;
                    reusable = true;
                }
                if (relocate || !reusable)
                {
                    var found = await Task.Run(() => BoardLocator.Locate(next),ct);
                    ct.ThrowIfCancellationRequested();
                    if (found == null)
                    {
                        // Keep the latest picture available for two-point picking even when auto-location fails.
                        frame = next; geometry = null; preview.SetImage(next.Png); preview.Reset();
                        throw new InvalidOperationException("没有找到可靠棋盘；点击“两点重新定位”，直接在左侧选两个角。");
                    }
                    nextGeometry = found; relocating = true;
                    var direction = await Task.Run(() => BoardLocator.DetectOrientation(next,found),ct);
                    ct.ThrowIfCancellationRequested();
                    if (desiredDirection == null && direction is { } detected) nextDirection = detected;
                }
                ct.ThrowIfCancellationRequested();
                nextGeometry = nextGeometry! with { RedAtTop = nextDirection };
                var read = useOcr
                    ? await BoardGlyphRecognizer.RecognizeAsync(next,nextGeometry,turn.SelectedIndex == 0,ct,desiredDirection == null)
                    : await recognize(next,nextGeometry,turn.SelectedIndex == 0,ct,desiredDirection == null);
                ct.ThrowIfCancellationRequested();
                if (closed || operation != revision) return;
                if (read == null) throw new InvalidOperationException("未能确认完整局面，可重新识字或直接修正棋子。");
                if (read.DetectedRedAtTop is { } recognizedDirection)
                { nextDirection = recognizedDirection; nextGeometry = nextGeometry with { RedAtTop = nextDirection }; }
                // Publish frame, grid, orientation and pieces together. A cancelled older operation
                // must not overwrite a more recent manual selection or orientation change.
                frame = next; geometry = nextGeometry; redAtTop = nextDirection; changedGeometry |= relocating;
                openingDraft = false;
                preview.SetImage(frame.Png); preview.Set(geometry); view.Flipped = redAtTop;
                flip.Content = redAtTop ? "朝向：红上黑下 ↕" : "朝向：黑上红下 ↕";
                ReadPieces(read.Fen); Editor.ReplaceBoard(pieces);
                view.Selected = read.Uncertain.Count > 0 ? read.Uncertain[0] : null; view.Refresh();
                help.Text = read.Confident ? "全部棋子已识别。可直接采用；无需退出窗口重新同步。"
                    : $"还有 {read.Uncertain.Count} 个位置不确定。{read.Problem} 如果整排错位，请在左侧重新定位。";
            }
            catch (OperationCanceledException) { if (!closed && operation == revision) help.Text = "识别已取消或超时，可重新定位或手动修正。"; }
            catch (Exception ex) { if (!closed && operation == revision) help.Text = ex.Message; }
            finally { if (!closed && operation == revision) { busy = false; RefreshValidation(); ExternalEditorEditBar.IsEnabled = !picking; } }
        }
        locate.Click += async (_,_) => await ReadAgainAsync(true,true);
        refresh.Click += async (_,_) => await ReadAgainAsync(false,true);
        flip.Click += async (_,_) =>
        {
            if (openingDraft || preview == null)
            {
                CancelWork();
                picking = false;
                if (preview != null) preview.IsPicking = false;
                ExternalEditorEditBar.IsEnabled = true;
                redAtTop = !redAtTop;
                if (geometry != null) geometry = geometry with { RedAtTop = redAtTop };
                view.Flipped = redAtTop; view.Refresh();
                flip.Content = redAtTop ? "朝向：红上黑下 ↕" : "朝向：黑上红下 ↕";
                RefreshValidation();
                return;
            }
            await ReadAgainAsync(false,false,desiredDirection:!redAtTop);
        };
        ExternalEditorStandard.Click += (_, _) =>
        {
            CancelWork();
            picking = false;
            if (preview != null) preview.IsPicking = false;
            openingDraft = true;
            ExternalEditorEditBar.IsEnabled = true;
            RefreshValidation();
        };
        ocr.Click += async (_,_) => await ReadAgainAsync(false,true,true);
        manual.Click += (_,_) =>
        {
            if (preview == null) return;
            CancelWork(); picking = true; preview.IsPicking = true; accept.IsEnabled = false; ExternalEditorEditBar.IsEnabled = false;
            preview.Reset(); help.Text = "在左侧原图点击棋盘最左上交叉点中心，再点击最右下交叉点中心；完成后自动重新识别。";
        };
        if (preview != null) preview.Changed += async () =>
        {
            if (!picking || preview.First is not { } first || preview.Last is not { } last) return;
            try
            {
                var next = new BoardCalibration(first.X,first.Y,last.X,last.Y,redAtTop);
                next.Validate(preview.PixelWidth,preview.PixelHeight);
                geometry = next; changedGeometry = true;
                picking = false; preview.IsPicking = false;
                var operation = revision;
                // Detect orientation from the corrected grid, not the previous incorrect crop.
                var direction = await Task.Run(() => BoardLocator.DetectOrientation(frame!,next), lifetime.Token) ?? redAtTop;
                if (closed || operation != revision) return;
                await ReadAgainAsync(false,true,desiredDirection:direction);
            }
            catch (Exception ex) { if (!closed) help.Text = ex.Message; }
        };
        cancel.Click += (_,_) => dialog.Close();
        accept.Click += (_,_) =>
        {
            if (busy || picking) return;
            try
            {
                if (turn.SelectedIndex < 0) throw new InvalidOperationException("请选择当前轮到红方还是黑方；单张中局截图可能无法判断行棋方。");
                var position = PositionSetup.BuildFen(Editor.Board,turn.SelectedIndex == 0);
                if (frame != null && geometry != null) geometry.Validate(preview!.PixelWidth, preview.PixelHeight);
                dialog.Close(new ExternalPositionEditResult(position, frame, geometry, redAtTop,
                    turn.SelectedIndex == 0, changedGeometry));
            }
            catch (Exception ex) { help.Text = ex.Message; }
        };
        dialog.Closed += (_,_) => { closed = true; CancelWork(); _cancelRegistration.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); preview?.Dispose(); };
        RefreshValidation();
    }

    private void InitializeEditing()
    {
        ExternalEditorBoard.SetupBoard = Editor.Board;
        ExternalEditorPalette.Attach(Editor);
        Editor.Changed += (_, _) => RefreshEditing();
        ExternalEditorMove.Click += (_, _) => Editor.SelectMove();
        ExternalEditorErase.Click += (_, _) => Editor.SelectErase();
        ExternalEditorUndo.Click += (_, _) => Editor.Undo();
        ExternalEditorClear.Click += (_, _) => Editor.Clear();
        ExternalEditorStandard.Click += (_, _) =>
        {
            Editor.ReplaceBoard((char[,])new XiangqiGame().Board.Clone());
            ExternalEditorTurn.SelectedIndex = 0;
            ExternalEditorStatus.Text = "已按当前朝向生成标准开局 32 子，红方先行。可继续手动摆棋，完成后采用局面。";
        };
        ExternalEditorTurn.SelectionChanged += (_, _) => RefreshValidation();
        Closed += (_, _) => ExternalEditorPalette.Attach(null);
        RefreshEditing();
    }

    private void RefreshEditing()
    {
        ExternalEditorBoard.SetupBoard = Editor.Board;
        ExternalEditorBoard.Selected = Editor.SelectedSquare;
        ExternalEditorBoard.Refresh();
        ExternalEditorToolHint.Text = PositionEditorText.Hint(Editor);
        ExternalEditorMove.Classes.Set("primary", Editor.Tool == PositionEditorTool.Move);
        ExternalEditorErase.Classes.Set("primary", Editor.Tool == PositionEditorTool.Erase);
        ExternalEditorUndo.IsEnabled = Editor.CanUndo;
        RefreshValidation();
    }

    private void RefreshValidation()
    {
        string? problem = null;
        try { PositionSetup.Validate(Editor.Board); }
        catch (FormatException ex) { problem = ex.Message; }
        if (problem == null && ExternalEditorTurn.SelectedIndex < 0) problem = "请选择当前行棋方。";
        ExternalEditorValidation.Text = problem == null ? "局面校验通过" : $"待修正：{problem}";
        ExternalEditorValidation.Foreground = problem == null ? Avalonia.Media.Brushes.SeaGreen : Avalonia.Media.Brushes.Firebrick;
        ExternalEditorAccept.IsEnabled = problem == null && _canAcceptCurrentFrame();
    }
}

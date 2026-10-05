using PaddiXiangqi.Core;

namespace PaddiXiangqi.ViewModels;

public sealed class PiecePaletteItemViewModel(char piece) : ObservableObject
{
    public char Piece { get; } = piece;
    public string Label => (char.IsUpper(Piece) ? "红" : "黑") + XiangqiGame.DisplayBoardPiece(Piece);
    public int Maximum => PositionSetup.MaximumCount(Piece);
    private int _count;
    private bool _selected;
    public int Count => _count;
    public string CountText => $"{Count}/{Maximum}";
    public bool CanAdd => Count < Maximum;
    public bool Selected => _selected;
    public string Hint => $"{Label} · 已摆 {Count}/{Maximum}" + (CanAdd ? " · 选择后可连续放置" : " · 数量已满，可在棋盘上移动或擦除");
    public void Update(PositionEditorState? editor)
    {
        _count = editor is null ? 0 : PositionSetup.CountPieces(editor.Board, Piece);
        _selected = editor?.Tool == PositionEditorTool.Place && editor.PlacementPiece == Piece;
        Changed(nameof(Count)); Changed(nameof(CountText)); Changed(nameof(CanAdd)); Changed(nameof(Selected)); Changed(nameof(Hint));
    }
}

public sealed class PiecePaletteViewModel
{
    public IReadOnlyList<PiecePaletteItemViewModel> Red { get; } = "RNBAKCP".Select(c => new PiecePaletteItemViewModel(c)).ToArray();
    public IReadOnlyList<PiecePaletteItemViewModel> Black { get; } = "rnbakcp".Select(c => new PiecePaletteItemViewModel(c)).ToArray();
    public void Update(PositionEditorState? editor)
    { foreach (var item in Red.Concat(Black)) item.Update(editor); }
}

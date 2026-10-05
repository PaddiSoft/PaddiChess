using PaddiXiangqi.Core;

namespace PaddiXiangqi.Views;

internal static class PositionEditorText
{
    public static string Hint(PositionEditorState editor)
    {
        if (editor.SelectedSquare is { } square)
            return $"已选中{Label(editor.Board[square.Rank, square.File])} · 点击目标位置移动";
        return editor.Tool switch
        {
            PositionEditorTool.Place => $"放置{Label(editor.PlacementPiece)} · 还可放 {PositionSetup.MaximumCount(editor.PlacementPiece) - PositionSetup.CountPieces(editor.Board, editor.PlacementPiece)} 枚",
            PositionEditorTool.Erase => "擦除工具 · 点击棋盘上的棋子移除",
            _ => "移动工具 · 点击棋子选中，再点击目标位置"
        };
    }
    private static string Label(char piece) => (char.IsUpper(piece) ? "红" : "黑") + XiangqiGame.DisplayBoardPiece(piece);
}

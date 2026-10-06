namespace PaddiXiangqi.Core;

public enum PositionEditorTool { Move, Place, Erase }

/// <summary>Shared editor interaction. Invalid recognized boards remain editable until final validation.</summary>
public sealed class PositionEditorState
{
    private const int UndoLimit = 64;
    private readonly LinkedList<char[,]> _undo = new();
    public char[,] Board { get; private set; }
    public PositionEditorTool Tool { get; private set; }
    public char PlacementPiece { get; private set; }
    public Square? SelectedSquare { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public event EventHandler? Changed;

    public PositionEditorState(char[,] board)
    {
        CheckDimensions(board);
        Board = board;
    }

    public bool CanAdd(char piece) => PositionSetup.MaximumCount(piece) > PositionSetup.CountPieces(Board, piece);

    public bool SelectPiece(char piece)
    {
        if (!CanAdd(piece)) return false;
        Tool = PositionEditorTool.Place;
        PlacementPiece = piece;
        SelectedSquare = null;
        Notify();
        return true;
    }

    public void SelectMove() { ResetSelection(PositionEditorTool.Move); Notify(); }
    public void SelectErase() { ResetSelection(PositionEditorTool.Erase); Notify(); }
    public void Clear()
    {
        if (Board.Cast<char>().Any(piece => piece != '\0')) Remember();
        Array.Clear(Board); ResetSelection(PositionEditorTool.Move); Notify();
    }

    public void Undo()
    {
        if (_undo.Last is not { } last) return;
        Array.Copy(last.Value, Board, Board.Length);
        _undo.RemoveLast(); ResetSelection(PositionEditorTool.Move); Notify();
    }

    public void ReplaceBoard(char[,] board)
    {
        CheckDimensions(board);
        Board = board;
        _undo.Clear();
        ResetSelection(PositionEditorTool.Move);
        Notify();
    }

    public void Click(Square square)
    {
        if (square.File is < 0 or > 8 || square.Rank is < 0 or > 9) return;
        if (Tool == PositionEditorTool.Erase)
        {
            if (Board[square.Rank, square.File] != '\0') Remember();
            Board[square.Rank, square.File] = '\0';
        }
        else if (SelectedSquare is { } from)
        {
            if (from != square)
            {
                Remember();
                // Moving onto an occupied square replaces that piece, never adds a duplicate.
                Board[square.Rank, square.File] = Board[from.Rank, from.File];
                Board[from.Rank, from.File] = '\0';
            }
            ResetSelection(PositionEditorTool.Move);
        }
        else if (Board[square.Rank, square.File] != '\0')
        {
            ResetSelection(PositionEditorTool.Move);
            SelectedSquare = square; // Keep the selected piece visible until its destination is chosen.
        }
        else if (Tool == PositionEditorTool.Place)
        {
            if (CanAdd(PlacementPiece)) { Remember(); Board[square.Rank, square.File] = PlacementPiece; }
            if (!CanAdd(PlacementPiece)) ResetSelection(PositionEditorTool.Move);
        }
        Notify();
    }

    private void ResetSelection(PositionEditorTool tool)
    { Tool = tool; PlacementPiece = '\0'; SelectedSquare = null; }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    private void Remember()
    {
        _undo.AddLast((char[,])Board.Clone());
        if (_undo.Count > UndoLimit) _undo.RemoveFirst();
    }
    private static void CheckDimensions(char[,] board)
    {
        if (board.GetLength(0) != 10 || board.GetLength(1) != 9)
            throw new ArgumentException("棋盘必须是十行九路。", nameof(board));
    }
}

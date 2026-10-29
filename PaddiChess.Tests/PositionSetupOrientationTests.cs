using PaddiXiangqi.Core;

namespace PaddiXiangqi.Tests;

public class PositionSetupOrientationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ManualOrientationProducesCanonicalFenWithoutChangingInput(bool reverse, bool redToMove)
    {
        var board = ReportedBoard();
        if (reverse) board = Reverse(board);
        var before = board.Cast<char>().ToArray();
        var fen = PositionSetup.BuildEditorFen(board, redToMove, out var rotated);
        Assert.Equal(reverse, rotated);
        Assert.Equal(CustomSetupOrientationTests.ReportedPieces + (redToMove ? " w" : " b") + " - - 0 1", fen);
        Assert.Equal(before, board.Cast<char>().ToArray());
        if (reverse) Assert.Throws<FormatException>(() => PositionSetup.BuildFen(board, redToMove));
    }

    [Theory]
    [InlineData("missing-king", false)] [InlineData("missing-king", true)]
    [InlineData("facing-kings", false)] [InlineData("facing-kings", true)]
    [InlineData("off-palace", false)] [InlineData("off-palace", true)]
    [InlineData("extra-rook", false)] [InlineData("extra-rook", true)]
    [InlineData("elephant-across-river", false)] [InlineData("elephant-across-river", true)]
    [InlineData("pawn-behind-start", false)] [InlineData("pawn-behind-start", true)]
    public void OrientationDoesNotAllowIllegalSetupsOrMutateFailedEdits(string invalid, bool reverse)
    {
        var board = ReportedBoard();
        switch (invalid)
        {
            case "missing-king": board[9, 4] = '\0'; break;
            case "facing-kings": board[3, 4] = '\0'; break;
            case "off-palace": board[0, 4] = '\0'; board[3, 3] = 'k'; break;
            case "extra-rook": board[1, 0] = board[1, 1] = 'r'; break;
            case "elephant-across-river": board[6, 2] = 'b'; break;
            case "pawn-behind-start": board[1, 8] = 'p'; break;
        }
        if (reverse) board = Reverse(board);
        var before = board.Cast<char>().ToArray();
        Assert.Throws<FormatException>(() => PositionSetup.BuildEditorFen(board, true, out _));
        Assert.Equal(before, board.Cast<char>().ToArray());
    }

    [Fact]
    public void WrongBoardDimensionsStillProduceTheValidationError()
    {
        var error = Assert.Throws<FormatException>(() => PositionSetup.BuildEditorFen(new char[9, 10], true, out _));
        Assert.Equal("棋盘必须是十行九路。", error.Message);
    }

    private static char[,] ReportedBoard()
    {
        var game = new XiangqiGame();
        game.LoadFen(CustomSetupOrientationTests.ReportedPieces + " w - - 0 1");
        return (char[,])game.Board.Clone();
    }

    private static char[,] Reverse(char[,] board)
    {
        var reversed = new char[10, 9];
        var cells = board.Cast<char>().Reverse().ToArray();
        for (var i = 0; i < cells.Length; i++) reversed[i / 9, i % 9] = cells[i];
        return reversed;
    }
}

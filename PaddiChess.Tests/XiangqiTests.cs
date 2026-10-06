using System.Text.Json;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Tests;

public class XiangqiTests
{
    [Fact]
    public void InitialPosition_HasOnlyLegalMovesForRed()
    {
        var game = new XiangqiGame();
        Assert.True(game.RedToMove);
        Assert.Equal(XiangqiGame.InitialFen, game.CurrentFen());
        Assert.True(game.TryMoveUci("e3e4", out var pawn));
        Assert.Equal("兵五进一", pawn.Notation);
        Assert.False(game.TryMoveUci("e3f3", out _));
        Assert.False(game.RedToMove);
    }

    [Fact]
    public void HorseLeg_BlocksJump()
    {
        var game = new XiangqiGame();
        var moves = game.LegalMovesFrom(new Square(1, 9)).Select(m => m.Uci).ToArray();
        Assert.Contains("b0c2", moves);
        Assert.DoesNotContain("b0d1", moves);
    }

    [Fact]
    public void Cannon_RequiresExactlyOneScreenToCapture()
    {
        var game = new XiangqiGame();
        game.LoadFen("4k4/9/9/r8/9/9/P8/9/9/C2K5 w - - 0 1");
        var moves = game.LegalMovesFrom(new Square(0, 9)).Select(m => m.Uci).ToArray();
        Assert.Contains("a0a6", moves);
        Assert.DoesNotContain("a0a5", moves);
    }

    [Fact]
    public void Elephant_CannotCrossRiver()
    {
        var game = new XiangqiGame();
        game.LoadFen("3k5/9/9/9/9/9/9/9/9/2B1K4 w - - 0 1");
        Assert.True(game.TryMoveUci("c0e2", out _));
        game.LoadFen("3k5/9/9/9/9/6B2/9/9/9/4K4 w - - 0 1");
        Assert.False(game.TryMoveUci("g4e6", out _));
    }

    [Fact]
    public void ExposingFacingKings_IsIllegal()
    {
        var game = new XiangqiGame();
        game.LoadFen("4k4/9/9/9/9/9/9/9/4R4/4K4 w - - 0 1");
        Assert.False(game.TryMoveUci("e1f1", out _));
    }

    [Fact]
    public void Check_RequiresAResponse()
    {
        var game = new XiangqiGame();
        game.LoadFen("2k6/9/9/9/4r4/9/9/9/9/4K4 w - - 0 1");
        Assert.True(game.SideInCheck);
        Assert.False(game.TryMoveUci("e0e1", out _));
        Assert.True(game.TryMoveUci("e0d0", out _));
        Assert.False(game.SideInCheck);
    }

    [Fact]
    public void UndoRedo_KeepUciHistoryConsistent()
    {
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        Assert.True(game.TryMoveUci("h9g7", out _));
        Assert.Equal("h2e2 h9g7", game.UciMoveList);
        Assert.True(game.Undo());
        Assert.Equal("h2e2", game.UciMoveList);
        Assert.True(game.Redo());
        Assert.Equal("h2e2 h9g7", game.UciMoveList);
        Assert.True(game.Undo());
        Assert.True(game.TryMoveUci("b9c7", out _));
        Assert.False(game.CanRedo);
        Assert.Equal("h2e2 b9c7", game.UciMoveList);
    }

    [Fact]
    public void ImportedFen_PreservesMoveCounters()
    {
        var game = new XiangqiGame();
        game.LoadFen("3k5/9/9/9/9/9/9/9/9/4K4 b - - 7 18");
        Assert.Equal("3k5/9/9/9/9/9/9/9/9/4K4 b - - 7 18", game.CurrentFen());
        Assert.True(game.TryMoveUci("d9d8", out _));
        Assert.EndsWith("w - - 8 19", game.CurrentFen());
    }

    [Fact]
    public void JumpToNotation_AndBranchReplacesFutureMoves()
    {
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        Assert.True(game.TryMoveUci("h9g7", out _));
        Assert.True(game.GoToPly(0));
        Assert.Null(game.LastMove);
        Assert.Equal(XiangqiGame.InitialFen, game.CurrentFen());
        Assert.True(game.GoToPly(1));
        Assert.Equal("h2e2", game.LastMove?.Uci);
        Assert.True(game.TryMoveUci("b9c7", out _));
        Assert.Equal(2, game.TotalPly);
        Assert.Equal("h2e2 b9c7", game.UciMoveList);
        Assert.False(game.CanRedo);
    }

    [Fact]
    public void AcceptedDraw_EndsAtRecordedPly_AndClearsWhenPlayBranches()
    {
        var game = new XiangqiGame();
        Assert.True(game.TryMoveUci("h2e2", out _));
        Assert.True(game.TryMoveUci("h9g7", out _));
        Assert.True(game.DeclareDraw());
        Assert.Equal(2, game.AgreedDrawPly);
        Assert.Equal(GameResult.Draw, game.Result);
        Assert.False(game.TryMoveUci("b0c2", out _));

        Assert.True(game.GoToPly(1));
        Assert.Equal(GameResult.Ongoing, game.Result);
        Assert.True(game.GoToPly(2));
        Assert.Equal(GameResult.Draw, game.Result);

        Assert.True(game.GoToPly(1));
        Assert.True(game.TryMoveUci("b9c7", out _));
        Assert.Null(game.AgreedDrawPly);
        Assert.Equal(GameResult.Ongoing, game.Result);
    }

    [Fact]
    public void CustomRecord_RoundTripsMovesAndAnnotations()
    {
        var record = new GameRecord
        {
            Title = "测试棋谱",
            Moves = ["h2e2", "h9g7"],
            Notes = new Dictionary<int, string> { [0] = "开局", [2] = "应马" },
            Scores = new Dictionary<int, double> { [0] = 12, [2] = -45 },
            CurrentPly = 1
        };
        var restored = JsonSerializer.Deserialize<GameRecord>(JsonSerializer.Serialize(record))!;
        var game = new XiangqiGame();
        game.LoadFen(restored.StartFen);
        foreach (var uci in restored.Moves) Assert.True(game.TryMoveUci(uci, out _));
        Assert.True(game.GoToPly(restored.CurrentPly));
        Assert.Equal("h2e2", game.LastMove?.Uci);
        Assert.Equal("应马", restored.Notes[2]);
        Assert.Equal(-45, restored.Scores[2]);
    }

    [Fact]
    public void PositionSetup_CreatesPlayableCustomStartPosition()
    {
        var board = (char[,])new XiangqiGame().Board.Clone();
        board[6, 4] = '\0';
        board[5, 4] = 'P';
        var fen = PositionSetup.BuildFen(board, false);
        var game = new XiangqiGame();
        game.LoadFen(fen);
        Assert.False(game.RedToMove);
        Assert.Equal('P', game.Board[5, 4]);
        Assert.Equal(fen, game.CurrentFen());
    }

    [Fact]
    public void PositionSetup_RejectsMissingKingAndFacingKings()
    {
        var board = new char[10, 9];
        Assert.Throws<FormatException>(() => PositionSetup.BuildFen(board, true));
        board[0, 4] = 'k';
        board[9, 4] = 'K';
        Assert.Throws<FormatException>(() => PositionSetup.BuildFen(board, true));
        board[5, 4] = 'P';
        Assert.Contains("4k4", PositionSetup.BuildFen(board, true));
    }

    [Fact]
    public void FifthCheckIsNotArbitrarilyBannedByTheClient()
    {
        var game = BuildFourCheckPosition();
        Assert.Equal(8, game.Ply);
        Assert.Equal(GameResult.Ongoing, game.Result);

        var from = new Square(4, 2); // e7
        Assert.Equal("e7f7", game.LegalMovesFrom(from).First(move => move.Uci == "e7f7").Uci);
        Assert.Contains("e7d7", game.LegalMovesFrom(from).Select(move => move.Uci));
        Assert.Contains("e7d7", game.AllLegalMoves().Select(move => move.Uci));
        Assert.True(game.TryMoveUci("e7d7", out _));
        Assert.Equal(9, game.Ply);
    }

    [Fact]
    public void CheckingHistoryRemainsNavigableAndBranchable()
    {
        var game = BuildFourCheckPosition();
        Assert.True(game.Undo());
        Assert.True(game.Undo());
        Assert.Equal(6, game.Ply);
        Assert.True(game.Redo());
        Assert.True(game.Redo());
        Assert.Contains("e7d7", game.AllLegalMoves().Select(move => move.Uci));

        Assert.True(game.GoToPly(6));
        Assert.True(game.TryMoveUci("f7f6", out var quietMove));
        Assert.False(quietMove.IsCheck);
        Assert.False(game.CanRedo);
        Assert.True(game.GoToPly(0));
        Assert.True(game.TryMoveUci("c7d7", out var firstCheck));
        Assert.True(firstCheck.IsCheck);

        game.LoadFen("3k5/9/2R6/9/9/4P4/9/9/9/4K4 w - - 0 1");
        Assert.Equal(0, game.Ply);
        Assert.Contains("c7d7", game.AllLegalMoves().Select(move => move.Uci));
    }

    private static XiangqiGame BuildFourCheckPosition()
    {
        var game = new XiangqiGame();
        game.LoadFen("3k5/9/2R6/9/9/4P4/9/9/9/4K4 w - - 0 1");
        var moves = new[]
        {
            "c7d7", "d9e9", "d7e7", "e9f9", "e7f7", "f9e9", "f7e7", "e9d9"
        };
        foreach (var uci in moves)
        {
            Assert.True(game.TryMoveUci(uci, out var move), $"{uci} at ply {game.Ply}");
            if (game.RedToMove == false) Assert.True(move.IsCheck, uci);
        }
        return game;
    }
}

using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using PaddiXiangqi.Sessions;

namespace PaddiXiangqi.Tests;

public class BoardVocabularyTests
{
    [Theory]
    [InlineData("4k4/9/9/9/9/9/9/9/9/3K5 w - - 0 1", 88)]
    [InlineData("rnbak4/9/1c7/p8/9/9/P8/1C7/9/RNBA1K3 w - - 0 1", 76)]
    public void EveryIndependentCellRemainsInTheVocabulary(string fen, int emptyCount)
    {
        var game = new XiangqiGame(); game.LoadFen(fen);
        var frame = Frame(game);
        var skin = BoardSkin.Learn("independent position", frame, game, requireAllPieces: false);
        Assert.Equal(90, skin.Samples.Values.Sum(samples => samples.Count));
        Assert.Equal(emptyCount, skin.Samples['\0'].Count);
        for (var i = 0; i < 90; i++)
            Assert.Contains(skin.Samples[game.Board[i / 9, i % 9]], cell => ReferenceEquals(cell, frame.Cells[i]));
    }

    [Fact]
    public void CompleteMidgameVocabularyCanBeSavedAndLoaded()
    {
        var game = new XiangqiGame();
        game.LoadFen("rnbak4/9/1c7/p8/9/9/P8/1C7/9/RNBA1K3 w - - 0 1");
        var skin = BoardSkin.Learn("complete midgame", Frame(game), game);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            skin.Save(file);
            var restored = BoardSkin.Load(file);
            foreach (var (piece, samples) in skin.Samples)
            {
                Assert.Equal(samples.Count, restored.Samples[piece].Count);
                for (var i = 0; i < samples.Count; i++) Assert.Equal(samples[i], restored.Samples[piece][i]);
            }
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task RetainingEmptySamplesDoesNotAuthorizeAStaleDecisionAfterARookMove()
    {
        var game = new XiangqiGame();
        game.LoadFen("rnbak4/9/1c7/p8/9/9/P8/1C7/9/RNBA1K3 b - - 0 1");
        var source = Frame(game);
        var tracker = new ExternalBoardTracker(source, game);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None);
        refresh.Seed("history", null, source, new(game.CurrentFen(), [], 0, null));
        var after = new XiangqiGame(); after.LoadFen(game.CurrentFen());
        Assert.True(after.TryMoveUci("a9a8", out _));
        var frame = Frame(after);
        var read = refresh.Current("history", null, frame);
        Assert.NotNull(read);
        Assert.True(read.Confident);
        Assert.Equal(after.CurrentFen().Split(' ')[0], read.Fen.Split(' ')[0]);
        var gate = ExternalInputVerification.Check(tracker, game, frame, read);
        Assert.True(gate.Recognized);
        Assert.Equal(new[] { "a9a8" }, gate.Moves); // Host must synchronize before sending its old decision.
    }

    [Fact]
    public async Task SparseVocabularyDoesNotHideAnImpossiblePieceAsEmpty()
    {
        var game = new XiangqiGame();
        game.LoadFen("4k4/3PaP3/9/9/9/9/9/5K3/9/9 w - - 0 1");
        var source = Frame(game);
        await using var refresh = new ExternalRecognitionRefresh(CancellationToken.None);
        refresh.Seed("history", null, source, new(game.CurrentFen(), [], 0, null));
        // Simulate a bad grid/animation placing the advisor on e7: inside the
        // palace but outside its reachable diagonals. Its pixels must remain
        // evidence of a conflict, rather than being excluded before comparison.
        var invalid = new XiangqiGame(); invalid.LoadFen(game.CurrentFen());
        invalid.Board[1, 4] = '\0'; invalid.Board[2, 4] = 'a';
        var read = refresh.Current("history", null, Frame(invalid));
        Assert.NotNull(read);
        Assert.False(read.Confident);
        Assert.Contains(new Square(4, 2), read.Uncertain);
        var observed = new XiangqiGame(); observed.LoadFen(read.Fen);
        Assert.Equal('a', observed.Board[2, 4]);
    }

    private static BoardObservation Frame(XiangqiGame game) =>
        BoardObservation.Read(ExternalBoardTests.Render(game), new(40, 40, 440, 490, false));
}

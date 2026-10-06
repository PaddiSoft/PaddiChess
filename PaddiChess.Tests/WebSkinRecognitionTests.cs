using PaddiXiangqi.Core;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

public class WebSkinRecognitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WebDefaultHasThirtyTwoPiecesAndBlackRooksOnBothOrientations(bool flipped)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", flipped ? "web-default-flipped.png" : "web-default-opening.png");
        var scale = flipped ? 1.6 : 1;
        var cal = new BoardCalibration(32.5 * scale, 32.5 * scale, 462.5 * scale, 516.25 * scale, flipped);
        var observation = BoardObservation.Read(File.ReadAllBytes(path), cal);
        var results = BuiltInBoardSkins.All.Select(skin => skin.Recognize(observation, true)).ToArray();
        var recognized = Assert.Single(results, result => result.Confident);
        var game = new XiangqiGame();
        Assert.Equal(game.CurrentFen(), recognized.Fen);
        game.LoadFen(recognized.Fen);
        Assert.Equal('r', game.Board[0, 0]); Assert.Equal('r', game.Board[0, 8]);
        Assert.Equal(32, game.Board.Cast<char>().Count(p => p != '\0'));
    }

    [Fact]
    public void IncompatibleThemeCannotInventPiecesOnTheEmptyIntersections()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "web-default-opening.png"));
        var observation = BoardObservation.Read(bytes, new(32.5, 32.5, 462.5, 516.25, false));
        var incompatible = BuiltInBoardSkins.All[0].Recognize(observation, true);
        Assert.False(incompatible.Confident);
        Assert.NotEmpty(incompatible.Uncertain);
        Assert.NotNull(incompatible.Problem);
        Assert.DoesNotContain('P', incompatible.Fen.Split('/')[1]);
        Assert.DoesNotContain('p', incompatible.Fen.Split('/')[1]);
    }

    [Fact]
    public void CalibrationDotsInTinyReportedPreviewAreUnresolvedInsteadOfNinetyInventedPieces()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "web-windows-reported.png"));
        var observation = BoardObservation.Read(bytes, new(33, 34, 266, 295, false));
        var skin = BuiltInBoardSkins.All.Single(s => s.Name.Contains("网页"));
        var result = skin.Recognize(observation, true);
        Assert.False(result.Confident);
        Assert.True(result.Fen.Split(' ')[0].Count(char.IsLetter) <= 32);
        Assert.Contains(new Square(0, 0), result.Uncertain);
    }
}

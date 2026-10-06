using PaddiXiangqi.Core;
using PaddiXiangqi.External;
using SkiaSharp;

namespace PaddiXiangqi.Tests;

/// <summary>
/// Real screenshots from the 2026-09-26 report, cropped to the JJ WeChat mini-app.
/// The actual board is red at the top. The Paddi board in the original screenshots
/// had recorded the red rook on an intermediate intersection of its sliding animation.
/// Source/target squares below were independently read from the source dot and piece.
/// </summary>
internal sealed record WeChatMidgameCase(int Number, string ActualFen, string Move, string PrematureMove, char Captured = '\0')
{
    public string Fixture => $"jj-wechat-midgame-{Number}.png";

    public string BeforeFen
    {
        get
        {
            var actual = new XiangqiGame();
            actual.LoadFen(ActualFen);
            var board = (char[,])actual.Board.Clone();
            Square.TryParseUci(Move[..2], out var source);
            Square.TryParseUci(Move[2..], out var target);
            board[source.Rank, source.File] = board[target.Rank, target.File];
            board[target.Rank, target.File] = Captured;
            return PositionSetup.BuildFen(board, true);
        }
    }

    public string PrematureFen
    {
        get
        {
            var game = new XiangqiGame();
            game.LoadFen(BeforeFen);
            if (!game.TryMoveUci(PrematureMove, out _))
                throw new InvalidOperationException($"Fixture {Number}: intermediate move is not legal");
            return game.CurrentFen();
        }
    }

    public (byte[] Png, BoardCalibration Geometry) Read(double scale = .5)
    {
        using var original = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture));
        using var resized = original.Resize(new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var encoded = resized.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        var geometry = BoardLocator.Locate(png) ?? throw new InvalidOperationException($"Could not locate fixture {Number}");
        return (png, geometry with { RedAtTop = true });
    }

    public byte[] ReconstructBefore(byte[] actualPng, BoardCalibration geometry)
    {
        // Synthetic BEFORE image derived from the real AFTER screenshot. Only the
        // rook source/destination are altered; this is not a second real screenshot.
        var actual = new XiangqiGame(); actual.LoadFen(ActualFen);
        Square.TryParseUci(Move[..2], out var source);
        Square.TryParseUci(Move[2..], out var target);
        using var original = SKBitmap.Decode(actualPng);
        using var reconstructed = original.Copy();
        using var canvas = new SKCanvas(reconstructed);
        var size = (float)((geometry.Right - geometry.Left) / 8 * .90);
        SKRect Area(Square square)
        {
            var point = geometry.Point(square);
            return SKRect.Create((float)point.X - size / 2, (float)point.Y - size / 2, size, size);
        }
        var donor = Enumerable.Range(0, 90).Select(i => new Square(i % 9, i / 9))
            .Where(square => square != source && actual.Board[square.Rank, square.File] == Captured)
            .OrderBy(square => Math.Abs(square.Rank - target.Rank) * 4 + Math.Abs(square.File - target.File)).First();
        canvas.DrawBitmap(original, Area(target), Area(source));
        canvas.DrawBitmap(original, Area(donor), Area(target));
        canvas.Flush();
        using var encoded = reconstructed.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}

internal static class WeChatMidgameFixtures
{
    public static readonly IReadOnlyList<WeChatMidgameCase> All =
    [
        new(1, "4ka3/4a4/4b1n2/p3p1C1p/1n5r1/2R1c1P2/P4r1cP/4B1N2/3NA3C/3AK1BR1 b - - 0 1", "c0c4", "c0c1"),
        new(2, "3k1a3/4a4/4b1n2/p4rNPp/1n7/4p4/PR6P/4B4/4Ac2C/3AK1B2 b - - 0 1", "h3b3", "h3c3"),
        new(3, "5a3/3ka4/4b1n2/p4rNPp/9/3np4/P7P/4BA3/1R5cC/3AK1B2 b - - 0 1", "b9b1", "b9b6"),
        new(4, "5a3/3ka4/4b1n2/p4rNPp/9/3np4/P7P/4BA3/1R5cC/3AK1B2 b - - 0 1", "b9b1", "b9b6"),
        new(5, "4ka3/4a4/6R2/p6Pp/9/4p4/P7P/8B/2nr5/3AK1B2 b - - 0 1", "e7g7", "e7f7", 'n')
    ];
}

public class WeChatMidgameRecognitionTests
{
    public static IEnumerable<object[]> Positions => WeChatMidgameFixtures.All.SelectMany(position =>
        new[] { new object[] { position.Number, 1.0 }, new object[] { position.Number, .5 } });

    [Theory]
    [MemberData(nameof(Positions))]
    public void RealWeChatEndPositionsHaveCorrectGridOrientationAndPieceIdentities(int number, double scale)
    {
        var position = WeChatMidgameFixtures.All.Single(p => p.Number == number);
        var (png, geometry) = position.Read(scale);
        Assert.InRange(geometry.Left / scale, 58, 69);
        Assert.InRange(geometry.Top / scale, 458, 480);
        Assert.InRange(geometry.Right / scale, 859, 877);
        Assert.InRange(geometry.Bottom / scale, 1338, 1356);
        Assert.True(BoardLocator.DetectOrientation(png, geometry));
        var observed = BuiltInBoardSkins.All[0].Recognize(BoardObservation.Read(png, geometry), false);
        Assert.Equal(position.ActualFen, observed.Fen);
        Assert.Null(observed.Problem);
        // Native capture uses logical pixels. At that size all five real frames are
        // fully confident, including glowing/selected pieces and the rook capture.
        if (scale == .5) Assert.True(observed.Confident, string.Join(',', observed.Uncertain));
        else Assert.InRange(observed.Uncertain.Count, 0, 2);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ObservedIntermediateAndFinalRookPositionsAreBothLegalSoOneFrameIsInsufficient(int number)
    {
        var position = WeChatMidgameFixtures.All.Single(p => p.Number == number);
        var actual = new XiangqiGame(); actual.LoadFen(position.BeforeFen);
        Assert.True(actual.TryMoveUci(position.Move, out _));
        Assert.Equal(position.ActualFen.Split(' ')[0], actual.CurrentFen().Split(' ')[0]);
        Assert.False(actual.RedToMove);
        var premature = new XiangqiGame(); premature.LoadFen(position.PrematureFen);
        Assert.NotEqual(actual.CurrentFen().Split(' ')[0], premature.CurrentFen().Split(' ')[0]);
        Assert.False(premature.RedToMove);
        // If the premature position is committed, the real red rook continuation
        // cannot be an ordinary black reply. A stable-frame gate must prevent it.
        Square.TryParseUci(position.Move[2..], out var final);
        Assert.NotEqual('R', premature.Board[final.Rank, final.File]);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void RealFinalFrameCorrectsTheRecentOpponentRookDestinationAndRestoresCapturedPieces(int number)
    {
        var position = WeChatMidgameFixtures.All.Single(p => p.Number == number);
        var (png, geometry) = position.Read();
        var before = new XiangqiGame(); before.LoadFen(position.BeforeFen);
        var beforeFrame = BoardObservation.Read(position.ReconstructBefore(png, geometry), geometry);
        var recovery = new ExternalMoveRecovery(before, beforeFrame, [position.PrematureMove], controlledRed: false);
        Assert.True(before.TryMoveUci(position.PrematureMove, out _));
        var corrected = recovery.Match(before, BoardObservation.Read(png, geometry), BuiltInBoardSkins.All);
        Assert.True(corrected.Recognized, corrected.Message);
        Assert.Equal(new[] { position.Move }, corrected.Moves);
        Assert.Equal(0, recovery.StartPly);
        before.GoToPly(recovery.StartPly);
        foreach (var move in corrected.Moves) Assert.True(before.TryMoveUci(move, out _));
        Assert.Equal(position.ActualFen.Split(' ')[0], before.CurrentFen().Split(' ')[0]);
        Assert.False(before.RedToMove);
    }

    [Fact]
    public void RealCaptureCorrectionCannotRewriteOurOwnMoveOrAnUnrelatedGame()
    {
        var position = WeChatMidgameFixtures.All[4];
        var (png, geometry) = position.Read();
        var before = new XiangqiGame(); before.LoadFen(position.BeforeFen);
        var beforeFrame = BoardObservation.Read(position.ReconstructBefore(png, geometry), geometry);
        var actual = BoardObservation.Read(png, geometry);
        var ownMove = new ExternalMoveRecovery(before, beforeFrame, [position.PrematureMove], controlledRed: true);
        var opponentMove = new ExternalMoveRecovery(before, beforeFrame, [position.PrematureMove], controlledRed: false);
        Assert.True(before.TryMoveUci(position.PrematureMove, out _));
        Assert.False(ownMove.Match(before, actual, BuiltInBoardSkins.All).Recognized);
        var unrelated = new XiangqiGame(); unrelated.LoadFen(WeChatMidgameFixtures.All[0].BeforeFen);
        Assert.False(opponentMove.Match(unrelated, actual, BuiltInBoardSkins.All).Recognized);
        var reply = before.AllLegalMoves().First();
        Assert.True(before.TryMoveUci(reply.Uci, out _));
        Assert.False(opponentMove.Match(before, actual, BuiltInBoardSkins.All).Recognized);
    }
}

using System.Diagnostics;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public class OpeningBookTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpeningBooks", "sample.xqb");
    [Theory]
    [InlineData("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w", "CEB7CEF37AC801E079F7DF7DF00002EBAEBAEB02C004653A56939440")]
    [InlineData("rnbakabnr/9/1c2c4/p1p1p1p1p/9/9/P1P1P1P1P/1C2C4/9/RNBAKABNR w", "CEB7CEF37AC801E3C1F7DF7DF00002EBAEBAEB160004653A56939440")]
    [InlineData("1r1akab2/9/b1n1c1c2/p2Rp3p/4n1p2/9/P1P1P1PrP/2N1BCN2/4A4/2BAK1C1R b", "CBCEF360700D7B6D1F8FDF7DF0017482E2F92E5964930013A56888")]
    public void CanonicalKeysMatchIndependentDatabaseVectors(string fen, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(XqbCodec.Encode(fen).Bytes));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualSqliteQueryReturnsCorrectPlayerCoordinates(bool black)
    {
        var fen = XiangqiGame.InitialFen.Replace(" w ", black ? " b " : " w ");
        using var db = new XqbDatabase(Sample); db.Validate();
        var rows = db.Lookup(fen);
        Assert.Contains(rows, r => r.Move == (black ? "b7e7" : "h2e2") && r.Score == 100);
        Assert.DoesNotContain(rows, r => r.Score == 9998 || r.Score == 9997);
    }

    [Fact]
    public async Task BookHitFiltersIllegalEntriesAndDoesNotStartAnEngine()
    {
        await using var engine = new PikafishClient { OverridePath = "missing-engine-must-not-be-started" };
        var result = await engine.SearchAsync(XiangqiGame.InitialFen, "", new(20, 1, 16, 1, 10)
        { OpeningBook = new(Sample, "测试库") }, _ => Assert.Fail("Book scores must not masquerade as engine info"), default);
        Assert.Equal("b2e2", result.BestMove);
        Assert.Equal(100, result.Book!.BookScore);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, engine.ProcessStartCount);
    }
    [Fact]
    public async Task RepeatedHistoryAndMissingBookFallBackWithoutSelectingAMove()
    {
        var options = new OpeningBookOptions(Sample, "test");
        Assert.Null(await OpeningBook.TryChooseAsync(XiangqiGame.InitialFen, "b0c2 b9c7 c2b0 c7b9", options, default));
        Assert.Null(await OpeningBook.TryChooseAsync(XiangqiGame.InitialFen, "", options with { Path = Sample + ".missing" }, default));
        Assert.Null(await OpeningBook.TryChooseAsync(XiangqiGame.InitialFen, "", null, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpeningBook.TryChooseAsync(XiangqiGame.InitialFen, "", options, cancellation.Token));
    }
    [Fact]
    public async Task ImportCopiesAndValidatesWithoutDependingOnTheOriginalPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-book-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OpeningBookStore(folder);
            var profile = await store.ImportAsync(Sample);
            Assert.NotEqual(Sample, store.Resolve(profile));
            using (var db = new XqbDatabase(store.Resolve(profile))) { db.Validate(); Assert.NotEmpty(db.Lookup(XiangqiGame.InitialFen)); }
            var bad = Path.Combine(folder, "broken.xqb"); await File.WriteAllTextAsync(bad, "not sqlite");
            await Assert.ThrowsAnyAsync<Exception>(() => store.ImportAsync(bad));
            Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.xqb"));
            Assert.Throws<FormatException>(() => store.Resolve(new("bad", "bad", "../outside.xqb")));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task BundledLibraryExtractsWithChecksumAndSupportsOpeningSequences()
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-book-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OpeningBookStore(folder); await store.PrepareBundledAsync();
            var modified = File.GetLastWriteTimeUtc(store.BundledPath);
            await store.PrepareBundledAsync(); Assert.Equal(modified, File.GetLastWriteTimeUtc(store.BundledPath));
            var options = new OpeningBookOptions(store.BundledPath, "内置");
            var game = new XiangqiGame(); var watch = Stopwatch.StartNew(); var hits = 0;
            for (var ply = 0; ply < 12; ply++)
            {
                var hit = await OpeningBook.TryChooseAsync(game.StartFen, game.UciMoveList, options, default);
                if (hit is null) break;
                Assert.True(game.TryMoveUci(hit.Move, out _)); hits++;
            }
            output.WriteLine($"Bundled opening: {hits} plies, total {watch.Elapsed.TotalMilliseconds:F1} ms, average {watch.Elapsed.TotalMilliseconds / hits:F1} ms/ply (includes history validation).");
            Assert.True(hits >= 4, $"Only {hits} opening hits");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
            using var db = new XqbDatabase(store.BundledPath);
            var left = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/4C2C1/9/RNBAKABNR b";
            var right = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C2C4/9/RNBAKABNR b";
            var a = db.Lookup(left); var b = db.Lookup(right);
            Assert.NotEmpty(a); Assert.Equal(a.Count, b.Count);
            static string Mirror(string move) => $"{(char)('i' - (move[0] - 'a'))}{move[1]}{(char)('i' - (move[2] - 'a'))}{move[3]}";
            Assert.Equal(a.Select(m => Mirror(m.Move)).Order(), b.Select(m => m.Move).Order());
            Assert.Null(await OpeningBook.TryChooseAsync(game.StartFen, game.UciMoveList, options with { MaxFullmoves = 1 }, default));
        }
        finally { Directory.Delete(folder, true); }
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using PaddiXiangqi.Core;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public class OpeningBookFormatTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpeningBooks", name);
    private static JsonElement[] Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Fixture("formats.json")));
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }
    public static IEnumerable<object[]> Formats => new[] { "binghe.obk", "binghe-sqlite.obk", "binghe-nan-index.obk", "eleeye.dat", "jqxq.bin" }.Select(s => new object[] { s });

    [Fact]
    public void HashesMatchIndependentReferenceVectorsIncludingBlackAndNanBits()
    {
        foreach (var c in Cases())
        {
            var fen = c.GetProperty("fen").GetString()!;
            Assert.Equal(c.GetProperty("binghe").GetString(), BingheBookCodec.Key(fen).ToString("X16"));
            Assert.Equal(c.GetProperty("eleeye").GetString(), ElephantEyeBookCodec.Key(fen).ToString("X8"));
            Assert.Equal(c.GetProperty("jqxq").GetString(), JqxqBookCodec.Key(fen).ToString("X16"));
        }
        Assert.True(double.IsNaN(BitConverter.UInt64BitsToDouble(BingheBookCodec.Key(Cases()[4].GetProperty("fen").GetString()!))));
    }

    [Theory, MemberData(nameof(Formats))]
    public void NativeFilesReturnCorrectMovesForBothColorsAndMirror(string name)
    {
        using var reader = OpeningBookReader.Open(Fixture(name)); reader.Validate(full: true);
        foreach (var (c, index) in Cases().Select((c, i) => (c, i)))
        {
            if (index == 4 && name == "binghe-sqlite.obk") continue;
            var fen = c.GetProperty("fen").GetString()!; var move = c.GetProperty("move").GetString()!;
            Assert.Contains(reader.Lookup(fen), m => m.Move == move && m.Score == 100 + index);
            var fields = fen.Split(' ');
            var mirror = string.Join('/', fields[0].Split('/').Select(row => new string(row.Reverse().ToArray()))) + " " + fields[1];
            var mirroredMove = $"{(char)('i' - (move[0] - 'a'))}{move[1]}{(char)('i' - (move[2] - 'a'))}{move[3]}";
            Assert.Contains(reader.Lookup(mirror), m => m.Move == mirroredMove && m.Score == 100 + index);
        }
        Assert.DoesNotContain(reader.Lookup(XiangqiGame.InitialFen), m => m.Score == 9998);
    }

    [Theory, MemberData(nameof(Formats))]
    public async Task ImportAndPlayActuallyUseEachFormatWithoutStartingEngine(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-formats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OpeningBookStore(folder);
            var profile = await store.ImportAsync(Fixture(name));
            Assert.False(string.IsNullOrWhiteSpace(profile.Format)); Assert.Contains(profile.Format!, profile.ToString());
            Assert.Equal(Path.GetExtension(name), Path.GetExtension(profile.FileName));
            await using var engine = new PikafishClient { OverridePath = "must-not-start" };
            var result = await engine.SearchAsync(XiangqiGame.InitialFen, "", new(20, 1, 16, 1, 10)
                { OpeningBook = new(store.Resolve(profile), profile.Name) }, _ => Assert.Fail("Book lookup must not emit engine scores"), default);
            Assert.Equal("b2e2", result.BestMove);
            Assert.Equal(100, result.Book!.BookScore); Assert.Equal(0, engine.ProcessStartCount);
            Assert.Equal(name.EndsWith(".obk") ? "库分" : "库权重", result.Book.ValueLabel);
            Assert.Null(await OpeningBook.TryChooseAsync(XiangqiGame.InitialFen, "b0c2 b9c7 c2b0 c7b9",
                new(store.Resolve(profile), profile.Name), default));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportAsync(Fixture(name), canceled.Token));
            Assert.Single(Directory.GetFiles(store.DirectoryPath));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("eleeye.dat")]
    [InlineData("jqxq.bin")]
    public async Task BinaryImportsRejectTruncatedUnsortedAndInvalidMoves(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-bad-book-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var bytes = File.ReadAllBytes(Fixture(name)); var size = name == "jqxq.bin" ? 16 : 8;
            var truncated = bytes[..^1]; var unsorted = bytes.Chunk(size).Reverse().SelectMany(c => c).ToArray();
            var badMove = (byte[])bytes.Clone(); badMove[size == 16 ? 8 : 4] = 0;
            foreach (var data in new[] { truncated, unsorted, badMove })
            {
                var path = Path.Combine(folder, name); File.WriteAllBytes(path, data);
                var store = new OpeningBookStore(folder);
                await Assert.ThrowsAnyAsync<FormatException>(() => store.ImportAsync(path));
                Assert.Empty(Directory.GetFiles(store.DirectoryPath));
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void CorruptObkIndexCycleAndInvalidPageSizeFailBoundedly()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".obk");
        try
        {
            var bytes = File.ReadAllBytes(Fixture("binghe.obk")); var pageSize = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16));
            var damaged = (byte[])bytes.Clone(); damaged[16] = damaged[17] = 0; File.WriteAllBytes(path, damaged);
            Assert.Throws<FormatException>(() => new BingheBook(path));
            var root = Enumerable.Range(1, bytes.Length / pageSize - 1).First(i => bytes[i * pageSize] == 2);
            var cell = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(root * pageSize + 12));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(root * pageSize + cell), (uint)(root + 1));
            File.WriteAllBytes(path, bytes);
            using var reader = new BingheBook(path);
            var timer = Stopwatch.StartNew(); Assert.Throws<FormatException>(() => reader.Validate(full: true));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ActiveSqliteJournalIsNotSilentlyDiscardedDuringCopy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "paddi-journal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "source.obk"); File.Copy(Fixture("binghe.obk"), source);
            File.WriteAllText(source + "-wal", "not-checkpointed");
            await Assert.ThrowsAsync<IOException>(() => new OpeningBookStore(folder).ImportAsync(source));
            Assert.False(Directory.Exists(Path.Combine(folder, "OpeningBooks")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("../outside.obk")]
    [InlineData("..\\outside.bin")]
    [InlineData("C:outside.dat")]
    public void ImportedPathsCannotEscapeManagedDirectory(string name) =>
        Assert.Throws<FormatException>(() => new OpeningBookStore("unused").Resolve(new("test", "test", name)));

    [Fact]
    public async Task IndexedLookupDoesNotRescanBookEveryMove()
    {
        foreach (var name in Formats.Select(f => (string)f[0]))
        {
            var options = new OpeningBookOptions(Fixture(name), name);
            await OpeningBook.TryChooseAsync(XiangqiGame.InitialFen, "", options, default); // JIT
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 30; i++) Assert.NotNull(await OpeningBook.TryChooseAsync(XiangqiGame.InitialFen, "", options, default));
            output.WriteLine($"{name}: {timer.Elapsed.TotalMilliseconds / 30:F2} ms/lookup including legality and opening history checks");
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        }
    }
}

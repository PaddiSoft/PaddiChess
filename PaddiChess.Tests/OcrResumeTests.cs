using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class OcrResumeTests
{
    [Fact]
    public async Task ExpiredSupplementBudgetYieldsAfterACompleteBatchInsteadOfRestartingItForever()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pawn-rook-correction.png"));
        var pixels = CapturedPixels.DecodePng(png);
        var grid = BoardLocator.Locate(png)! with { RedAtTop = false };
        var cache = new LocalGlyphOcr.InputCache();
        int[] indices = [86, 87, 89];
        LocalGlyphOcr.ReadResult? read = null;
        int batches = 0;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            // A zero soft budget deterministically models any batch taking longer
            // than its budget, without relying on a CI machine's inference speed.
            read = await LocalGlyphOcr.ReadCellsAsync(pixels, grid, indices, default, cache, TimeSpan.Zero);
            Assert.InRange(read.Batches, 0, 1);
            batches += read.Batches;
            if (indices.All(i => LocalGlyphOcr.IsReliable(read.Glyphs[i]))) break;
        }
        Assert.NotNull(read);
        Assert.True(batches > 1);
        Assert.All(indices, i => Assert.True(LocalGlyphOcr.IsReliable(read.Glyphs[i])));
        Assert.Equal("卒", read.Glyphs[89].Text);
        var completed = await LocalGlyphOcr.ReadCellsAsync(pixels, grid, indices, default, cache);
        Assert.Equal(0, completed.Batches);
        Assert.Equal(read.Glyphs, completed.Glyphs);
    }

    [Fact]
    public async Task RetryReusesCompletedGlyphViewsEvenAfterAnInterveningCancellation()
    {
        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pawn-rook-correction.png"));
        var pixels = CapturedPixels.DecodePng(png);
        var grid = BoardLocator.Locate(png)! with { RedAtTop = false };
        var cache = new LocalGlyphOcr.InputCache();
        var first = await LocalGlyphOcr.ReadCellsAsync(pixels, grid, [86, 87, 89], default, cache);
        Assert.True(first.InferenceCells > 0);
        Assert.Equal("卒", first.Glyphs[89].Text);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalGlyphOcr.ReadCellsAsync(pixels, grid, [86, 87, 89], cancelled.Token, cache));
        var retry = await LocalGlyphOcr.ReadCellsAsync(pixels, grid, [86, 87, 89], default, cache);
        Assert.Equal(first.Glyphs, retry.Glyphs);
        Assert.Equal(0, retry.InferenceCells); Assert.Equal(0, retry.Batches);
        Assert.True(retry.ReusedCells > 0);
    }

    [Fact]
    public void DigestCollisionCannotReuseAnotherGlyphAndMemoryRemainsBounded()
    {
        var cache = new LocalGlyphOcr.InputCache();
        cache.Add("same-digest", [1, 2, 3], new("卒", .95));
        Assert.False(cache.TryGet("same-digest", [1, 2, 4], out _));
        Assert.True(cache.TryGet("same-digest", [1, 2, 3], out var result));
        Assert.Equal("卒", result.Text);
        for (var i = 0; i < 300; i++) cache.Add(i.ToString(), [i], new("車", .96));
        Assert.Equal(256, cache.Count);
        Assert.False(cache.TryGet("same-digest", [1, 2, 3], out _));
        Assert.True(cache.TryGet("299", [299], out _));
    }
}

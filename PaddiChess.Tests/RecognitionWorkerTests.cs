using System.Diagnostics;
using System.Text.Json;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Tests;

[Collection("Local recognition")]
public class RecognitionWorkerTests
{
    private static ProcessStartInfo Real(bool cpu) => RecognitionWorkerClient.ApplicationStartInfo("dotnet",
        Path.Combine(AppContext.BaseDirectory, "Paddi象棋.dll"), cpu);

    [Fact]
    public async Task IsolatedReaderReusesProcessAndPreservesFrameIdentity()
    {
        var samples = JsonSerializer.Deserialize<BoardClassifierTests.Sample[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "recognition-cases.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var sample = samples.First(s => s.Id == "jj-rook-capture-after");
        var pixels = CapturedPixels.DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, sample.Path)));
        var frame = new ExternalFrame(new(1, 2, "test", 0, 0, pixels.Width, pixels.Height), pixels);
        var grid = new BoardCalibration(sample.Grid[0], sample.Grid[1], sample.Grid[2], sample.Grid[3], sample.Flipped);
        await using var worker = new RecognitionWorkerClient(_ => Real(true));
        for (int i = 0; i < 2; i++)
        {
            var read = await worker.ReadAsync(frame, grid, true, default, false, null, null);
            Assert.NotNull(read); Assert.True(read.Confident, read.Problem);
            Assert.Equal(sample.Fen, read.Fen.Split(' ')[0]);
        }
        Assert.Equal(1, worker.Starts); Assert.Equal(0, worker.Restarts);
        Assert.False(frame.PngEncoded);
    }

    [Fact]
    public async Task HungProcessIsKilledAndNextRequestRestartsOnCpu()
    {
        var launch = 0;
        await using var worker = new RecognitionWorkerClient(cpu =>
        {
            if (Interlocked.Increment(ref launch) > 1) { Assert.True(cpu); return Real(cpu); }
            var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell" : "/bin/sleep")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsWindows()) { info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command"); info.ArgumentList.Add("Start-Sleep 30"); }
            else info.ArgumentList.Add("30");
            return info;
        });
        var frame = new ExternalFrame(new(1, 2, "test", 0, 0, 16, 16), new CapturedPixels(16, 16, 64, new byte[1024]));
        using var ct = new CancellationTokenSource(200);
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ReadAsync(frame, new(1,1,14,14,false), true, ct.Token, false, null, null));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        await worker.PrepareAsync();
        Assert.Equal(2, worker.Starts); Assert.Equal(1, worker.Restarts);
    }
}

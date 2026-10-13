using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PaddiXiangqi.External;

namespace PaddiXiangqi.Sessions;

/// <summary>Local file replay: identical frames on different hardware, no window input or screenshot upload.</summary>
public static class RecognitionBenchmark
{
    private sealed record Sample(string Id, string Path, double[] Grid, bool Flipped, string Fen);
    public static async Task RunAsync(string manifest, string output, Func<bool, ProcessStartInfo> worker,
        CancellationToken ct = default)
    {
        manifest = Path.GetFullPath(manifest);
        if (new FileInfo(manifest).Length > 10 * 1024 * 1024) throw new IOException("验证清单过大");
        var samples = JsonSerializer.Deserialize<Sample[]>(await File.ReadAllTextAsync(manifest, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new IOException("验证清单为空");
        if (samples.Length is 0 or > 1000) throw new IOException("验证清单需包含 1–1000 帧");
        var metrics = new SessionLatencyMetrics();
        var results = new List<object>();
        await using var client = new RecognitionWorkerClient(worker, TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew(); await client.PrepareAsync();
        var warmup = clock.Elapsed.TotalMilliseconds;
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            if (sample.Grid.Length != 4 || sample.Grid.Any(x => !double.IsFinite(x))) throw new IOException("网格坐标无效");
            var path = Path.GetFullPath(sample.Path, Path.GetDirectoryName(manifest)!);
            // The repository's regression manifest stores paths relative to the output root.
            if (!File.Exists(path)) path = Path.GetFullPath(sample.Path, Path.GetDirectoryName(Path.GetDirectoryName(manifest))!);
            var pixels = CapturedPixels.DecodePng(await File.ReadAllBytesAsync(path, ct));
            var frame = new ExternalFrame(new(1, 0, "benchmark", 0, 0, pixels.Width, pixels.Height), pixels);
            var grid = new BoardCalibration(sample.Grid[0], sample.Grid[1], sample.Grid[2], sample.Grid[3], sample.Flipped);
            for (int pass = 0; pass < 3; pass++)
            {
                clock.Restart();
                var read = await client.ReadAsync(frame, grid, true, ct, false, null, null);
                var elapsed = clock.Elapsed.TotalMilliseconds;
                metrics.Record(pass == 0 ? "first-frame" : "repeat-frame", elapsed);
                results.Add(new { sample.Id, Pass = pass + 1, Milliseconds = elapsed, Confident = read?.Confident ?? false,
                    Uncertain = read?.Uncertain.Count, Matches = read?.Fen.Split(' ')[0] == sample.Fen.Split(' ')[0] });
            }
        }
        var report = new
        {
            Schema = 1, CreatedAt = DateTimeOffset.UtcNow, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), CpuThreads = Environment.ProcessorCount,
            Mode = Environment.GetEnvironmentVariable("PADDI_RECOGNITION_DEVICE") ?? "auto",
            WarmupMs = warmup, client.Starts, client.Restarts,
            Devices = RecognitionAcceleration.Status, Latency = metrics.Snapshot(), Frames = results,
            Scope = "Local recorded-frame replay; excludes live capture, input, and third-party game acknowledgement."
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
}

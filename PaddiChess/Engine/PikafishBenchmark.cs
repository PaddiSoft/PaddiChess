using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PaddiXiangqi.Engine;

/// <summary>Search-only numbers printed by Pikafish's built-in bench command.</summary>
public sealed record EngineBenchmarkResult(string EngineName, string EnginePath, int Threads, int HashMb,
    int Depth, long Nodes, long SearchTimeMs, long NodesPerSecond, long WallTimeMs);

public sealed record EngineBenchmarkProgress(int Position, int TotalPositions);

public sealed partial class PikafishClient
{
    private static readonly Regex BenchmarkPosition = new(@"^Position:\s*(\d+)\s*/\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs a separate Pikafish process so benchmarking does not change the live game's
    /// transposition table or UCI settings. Use the same settings on two binaries to compare NPS.
    /// </summary>
    public async Task<EngineBenchmarkResult> BenchmarkAsync(int threads = 1, int hashMb = 16, int depth = 10,
        CancellationToken cancellationToken = default, IProgress<EngineBenchmarkProgress>? progress = null)
    {
        if (threads is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(threads));
        if (hashMb is < 1 or > EngineSettings.MaxHashMb) throw new ArgumentOutOfRangeException(nameof(hashMb));
        if (depth is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(depth));
        if (_disposed) throw new ObjectDisposedException(nameof(PikafishClient));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = string.IsNullOrWhiteSpace(OverridePath) ? BundledEnginePath() : Path.GetFullPath(OverridePath.Trim());
            if (!File.Exists(path)) throw new FileNotFoundException("未找到皮卡鱼引擎程序。", path);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
            }
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(path)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("无法启动皮卡鱼基准测试进程。");
            process.StandardInput.AutoFlush = true;
            var stopwatch = Stopwatch.StartNew();
            var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                { SingleReader = true, SingleWriter = false });

            async Task PumpAsync(StreamReader reader)
            {
                try
                {
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                        lines.Writer.TryWrite(line);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            var stdout = PumpAsync(process.StandardOutput);
            var stderr = PumpAsync(process.StandardError);
            _ = Task.Run(async () =>
            {
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                lines.Writer.TryComplete();
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            async Task<string> NextLineAsync()
            {
                try { return await lines.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("皮卡鱼基准测试超时。"); }
                catch (ChannelClosedException)
                { throw new IOException("皮卡鱼基准测试进程提前退出。"); }
            }

            try
            {
                await process.StandardInput.WriteLineAsync("uci").ConfigureAwait(false);
                var engineName = "Pikafish";
                var options = new Dictionary<string, UciEngineOption>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var line = await NextLineAsync().ConfigureAwait(false);
                    if (line.StartsWith("id name ", StringComparison.Ordinal)) engineName = line[8..];
                    if (UciEngineOption.Parse(line) is { } option) options[option.Name] = option;
                    if (line == "uciok") break;
                }
                if (!engineName.Contains("Pikafish", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("此测速使用 Pikafish 的 bench 命令，其他 UCI 引擎请使用“检测引擎”验证。");
                ConfigureEngine(path, options, command => process.StandardInput.WriteLine(command));
                await process.StandardInput.WriteLineAsync("isready").ConfigureAwait(false);
                while (await NextLineAsync().ConfigureAwait(false) != "readyok") { }

                await process.StandardInput.WriteLineAsync($"bench {hashMb} {threads} {depth} default depth")
                    .ConfigureAwait(false);
                long? elapsed = null, nodes = null, nps = null;
                while (elapsed is null || nodes is null || nps is null)
                {
                    var line = await NextLineAsync().ConfigureAwait(false);
                    if (TryBenchmarkNumber(line, "Total time (ms)", out var value)) elapsed = value;
                    else if (TryBenchmarkNumber(line, "Nodes searched", out value)) nodes = value;
                    else if (TryBenchmarkNumber(line, "Nodes/second", out value)) nps = value;
                    else if (BenchmarkPosition.Match(line) is { Success: true } match
                             && int.TryParse(match.Groups[1].Value, out var position)
                             && int.TryParse(match.Groups[2].Value, out var total))
                        progress?.Report(new EngineBenchmarkProgress(position, total));
                }
                stopwatch.Stop();
                await process.StandardInput.WriteLineAsync("quit").ConfigureAwait(false);
                using var quitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(quitTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return new EngineBenchmarkResult(engineName, path, threads, hashMb, depth,
                    nodes.Value, elapsed.Value, nps.Value, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    private static bool TryBenchmarkNumber(string line, string label, out long number)
    {
        number = 0;
        if (!line.StartsWith(label, StringComparison.Ordinal)) return false;
        var separator = line.IndexOf(':');
        return separator >= 0 && long.TryParse(line[(separator + 1)..].Trim(), out number);
    }
}

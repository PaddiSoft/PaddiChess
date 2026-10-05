using System.Diagnostics;
using System.Text.Json;
using PaddiXiangqi.Core;

namespace PaddiXiangqi.Engine;

public sealed record NativeRuleMoves(string Version, string Fen, IReadOnlyList<string> Allowed,
    IReadOnlyList<string> Excluded);

/// <summary>Persistent, NNUE-free adapter to the bundled Pikafish rule implementation.</summary>
public sealed class PikafishRulesClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _cachedKey;
    private NativeRuleMoves? _cached;
    private bool _disposed;
    public int ProcessStartCount { get; private set; }
    public int QueryCount { get; private set; }

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "Native",
        OperatingSystem.IsWindows() ? "PaddiRules.exe" : "PaddiRules");

    public async Task<NativeRuleMoves> GetAllowedMovesAsync(string startFen, string uciMoves,
        string currentFen, CancellationToken cancellationToken = default)
    {
        if (startFen.IndexOfAny(['\n', '\r', '\t']) >= 0 || uciMoves.IndexOfAny(['\n', '\r', '\t']) >= 0)
            throw new ArgumentException("规则校验的 FEN 和棋谱必须为单行文本。");
        var key = startFen + "\t" + uciMoves;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_cachedKey == key && _cached is not null)
            {
                ValidatePosition(_cached.Fen, currentFen);
                return _cached;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (_process is null || _process.HasExited)
                {
                    StopProcess();
                    if (!File.Exists(BundledPath))
                        throw new FileNotFoundException("缺少皮卡鱼规则组件，无法核验模型着法。请重新安装完整客户端。", BundledPath);
                    _process = new Process { StartInfo = new ProcessStartInfo(BundledPath)
                    { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                      RedirectStandardError = true, CreateNoWindow = true } };
                    _process.ErrorDataReceived += (_, _) => { };
                    _process.Start();
                    _process.BeginErrorReadLine();
                    ProcessStartCount++;
                }
                await _process.StandardInput.WriteLineAsync(("rules\t" + key).AsMemory(), timeout.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                QueryCount++;
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("皮卡鱼规则组件意外退出。");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("protocol").GetInt32() != 1)
                    throw new InvalidOperationException("皮卡鱼规则组件协议不兼容。");
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("皮卡鱼规则校验失败：" + error.GetString());
                var fen = root.GetProperty("fen").GetString()!;
                ValidatePosition(fen, currentFen);
                var allowed = root.GetProperty("allowed").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var excluded = root.GetProperty("excluded").EnumerateArray().Select(x => x.GetString()!).ToArray();
                // Keep the independent client movement check. A corrupt or mismatched
                // helper response cannot add a geometrically illegal model candidate.
                var game = new XiangqiGame(); game.LoadFen(currentFen);
                var basic = game.AllLegalMoves().Select(move => move.Uci).ToHashSet(StringComparer.Ordinal);
                if (allowed.Concat(excluded).Any(move => !basic.Contains(move)) ||
                    allowed.Distinct().Count() != allowed.Length || excluded.Distinct().Count() != excluded.Length ||
                    allowed.Intersect(excluded).Any() ||
                    root.GetProperty("historyPlies").GetInt32() != uciMoves.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                    throw new InvalidOperationException("皮卡鱼规则结果与当前棋局不一致，未请求模型。");
                _cached = new(root.GetProperty("ruleVersion").GetString()!, fen,
                    Array.AsReadOnly(allowed), Array.AsReadOnly(excluded));
                _cachedKey = key;
                return _cached;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { StopProcess(); throw new TimeoutException("皮卡鱼规则校验超时，模型执棋已暂停。"); }
            catch { StopProcess(); throw; }
        }
        finally { _gate.Release(); }
    }

    private static void ValidatePosition(string actual, string expected)
    {
        // Rule-60 counting differs from a generic FEN halfmove counter; compare
        // piece placement and the side to move, with full history keyed separately.
        if (!actual.Split(' ').Take(2).SequenceEqual(expected.Split(' ').Take(2)))
            throw new InvalidOperationException("规则校验的棋谱与当前棋盘不一致，未请求模型。");
    }

    private void StopProcess()
    {
        if (_process is null) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { _process.Dispose(); _process = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; StopProcess(); _cached = null; _cachedKey = null; }
        finally { _gate.Release(); }
    }
}

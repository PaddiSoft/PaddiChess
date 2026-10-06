using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace PaddiXiangqi.Engine;

public sealed record EngineSettings(int Level, int Threads, int HashMb, int ThinkSeconds, int MaxDepth)
{
    public const int MaxHashMb = 16_384;
    public bool UseDepthLimit { get; init; } = true;
    public int? MultiPvOverride { get; init; }
    public int? MoveTimeOverrideMs { get; init; }
    public int MultiPv => MultiPvOverride ?? (Level switch { <= 2 => 8, <= 5 => 5, <= 8 => 3, <= 11 => 2, _ => 1 });
    public int MoveTimeMs => MoveTimeOverrideMs is int milliseconds ? Math.Clamp(milliseconds, 100, 120_000) : Level switch
    {
        <= 2 => 220 + Level * 100,
        <= 5 => 520 + (Level - 3) * 180,
        <= 8 => 900 + (Level - 6) * 250,
        <= 11 => 1600 + (Level - 9) * 350,
        <= 15 => 2600 + (Level - 12) * 500,
        <= 19 => 4800 + (Level - 16) * 850,
        _ => Math.Clamp(ThinkSeconds, 1, 120) * 1000
    };
    public int Depth => Level == 20 ? Math.Clamp(MaxDepth, 1, 255) : Math.Max(4, Level + 3);
    public int? SearchDepthLimit => Level == 20 && !UseDepthLimit ? null : Depth;
    public string SearchLimits => $"movetime {MoveTimeMs}" +
        (SearchDepthLimit is int depth ? $" depth {depth}" : "");
}

public sealed record EngineInfo(int Depth, int MultiPv, int? Centipawns, int? Mate, long Nodes, string Pv,
    int? Wins = null, int? Draws = null, int? Losses = null, long? Nps = null)
{
    public string ScoreText => Mate is int mate ? $"杀 {Math.Abs(mate)}" : Centipawns is int cp ? $"{cp:+0;-0;0} 分" : "—";
    public string FirstMove => Pv.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
}

public sealed record SearchResult(string BestMove, IReadOnlyList<EngineInfo> Candidates);

public sealed partial class PikafishClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _writeLock = new();
    private Channel<string> _lines = NewLineChannel();
    private Process? _process;
    private int _threads = -1;
    private int _hash = -1;
    private int _multiPv = -1;
    private int _skill = -1;
    private int _newGameRequested = 1;
    private int _clearHashRequested;
    private int _readyHandshakeCount;
    private int _processStartCount;
    private bool _disposed;

    public string? OverridePath { get; set; }
    public string? OverrideEvalPath { get; set; }
    public IReadOnlyDictionary<string, string> OverrideRuleOptions { get; set; } = new Dictionary<string, string>();
    public string EngineName { get; private set; } = "Pikafish";
    public string EngineAuthor { get; private set; } = "";
    public string? ResolvedEvalPath { get; private set; }
    private readonly Dictionary<string, UciEngineOption> _options = new(StringComparer.OrdinalIgnoreCase);
    public int ReadyHandshakeCount => Volatile.Read(ref _readyHandshakeCount);
    public int ProcessStartCount => Volatile.Read(ref _processStartCount);

    private static Channel<string> NewLineChannel() => Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public static string BundledEnginePath()
    {
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Pikafish-MacOS-universal"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "Pikafish-Windows-x86-64-universal.exe"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "Pikafish-Linux-x86-64-universal"
            : throw new PlatformNotSupportedException("当前系统架构没有附带的皮卡鱼引擎，请在设置中指定兼容的引擎程序。 ");
        return Path.Combine(AppContext.BaseDirectory, "Engine", name);
    }

    public void RequestNewGame() => Interlocked.Exchange(ref _newGameRequested, 1);
    public void RequestClearHash() => Interlocked.Exchange(ref _clearHashRequested, 1);

    public async Task<SearchResult> SearchAsync(string startFen, string uciMoves, EngineSettings settings,
        Action<EngineInfo>? onInfo, CancellationToken cancellationToken,
        IReadOnlyList<string>? allowedRootMoves = null)
    {
        // Optional explicit search restriction for callers requesting particular
        // variations. Normal play passes no whitelist: the engine judges repetition,
        // perpetual checks and chases using startFen plus the complete move history.
        string rootMoveSuffix = "";
        if (allowedRootMoves is not null)
        {
            if (allowedRootMoves.Count == 0)
                throw new ArgumentException("当前局面没有允许搜索的着法。", nameof(allowedRootMoves));
            var moves = allowedRootMoves.Distinct(StringComparer.Ordinal).ToArray();
            if (moves.Any(move => move.Length != 4 ||
                                  move[0] is < 'a' or > 'i' || move[2] is < 'a' or > 'i' ||
                                  move[1] is < '0' or > '9' || move[3] is < '0' or > '9'))
                throw new ArgumentException("允许搜索的着法必须是四字符 UCI 坐标。", nameof(allowedRootMoves));
            rootMoveSuffix = " searchmoves " + string.Join(' ', moves);
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var reachedBestMove = false;
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureAsync(settings, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Send($"position fen {startFen}{(uciMoves.Length > 0 ? " moves " + uciMoves : "")}");
            Send($"go {settings.SearchLimits}{rootMoveSuffix}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(45, settings.MoveTimeMs / 1000 + 25)));
            using var stop = cancellationToken.Register(() =>
            {
                try { Send("stop"); timeout.CancelAfter(TimeSpan.FromSeconds(5)); } catch { }
            });
            var candidates = new Dictionary<int, EngineInfo>();
            while (true)
            {
                string line;
                try { line = await _lines.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("皮卡鱼搜索超时或停止后没有返回着法。");
                }
                catch (ChannelClosedException)
                {
                    throw new IOException("皮卡鱼引擎在搜索期间退出。");
                }
                if (line.StartsWith("info ", StringComparison.Ordinal) && TryParseInfo(line, out var info))
                {
                    if (!candidates.TryGetValue(info.MultiPv, out var prior) || info.Depth >= prior.Depth)
                        candidates[info.MultiPv] = info;
                    onInfo?.Invoke(info);
                }
                else if (line.StartsWith("bestmove ", StringComparison.Ordinal))
                {
                    reachedBestMove = true;
                    // The bestmove response completes the UCI search even when the UI
                    // cancelled it. Keep the now-idle process for the next position.
                    cancellationToken.ThrowIfCancellationRequested();
                    var best = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                    return new SearchResult(best, candidates.Values.OrderBy(c => c.MultiPv).ToArray());
                }
            }
        }
        finally
        {
            if (!reachedBestMove) AbandonProcess();
            _gate.Release();
        }
    }

    /// <summary>Start the process, load NNUE and allocate its hash without choosing or sending a move.</summary>
    public async Task PrepareAsync(EngineSettings settings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(ct).ConfigureAwait(false);
            await ConfigureAsync(settings, ct).ConfigureAwait(false);
        }
        catch { AbandonProcess(); throw; }
        finally { _gate.Release(); }
    }

    /// <summary>Release an inactive client's process and hash; the next search can start it again.</summary>
    public async Task ReleaseResourcesAsync(CancellationToken ct = default)
    {
        // Serialize with search/prepare: a cancelled search first drains its bestmove.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ct.ThrowIfCancellationRequested(); AbandonProcess(); }
        finally { _gate.Release(); }
    }

    private async Task ConfigureAsync(EngineSettings settings, CancellationToken ct)
    {
        var needsReady = false;
        if (_threads != settings.Threads)
        { needsReady |= SetNumericOption("Threads", settings.Threads); _threads = settings.Threads; }
        if (_hash != settings.HashMb)
        { needsReady |= SetNumericOption("Hash", settings.HashMb); _hash = settings.HashMb; }
        if (_multiPv != settings.MultiPv)
        { needsReady |= SetNumericOption("MultiPV", settings.MultiPv); _multiPv = settings.MultiPv; }
        if (_skill != settings.Level)
        { needsReady |= SetNumericOption("Skill Level", settings.Level); _skill = settings.Level; }
        if (Interlocked.Exchange(ref _clearHashRequested, 0) == 1)
        { if (_options.ContainsKey("Clear Hash")) { Send("setoption name Clear Hash"); needsReady = true; } }
        if (Interlocked.Exchange(ref _newGameRequested, 0) == 1)
        { Send("ucinewgame"); needsReady = true; }
        if (needsReady)
        {
            Send("isready");
            Interlocked.Increment(ref _readyHandshakeCount);
            await WaitForAsync(line => line == "readyok", TimeSpan.FromSeconds(20), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false } && !_lines.Reader.Completion.IsCompleted) return;
        AbandonProcess();
        var path = string.IsNullOrWhiteSpace(OverridePath) ? BundledEnginePath() : Path.GetFullPath(OverridePath.Trim());
        if (!File.Exists(path)) throw new FileNotFoundException("未找到皮卡鱼引擎程序。", path);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
        var info = new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动皮卡鱼引擎。");
        _process = process;
        Interlocked.Increment(ref _processStartCount);
        _lines = NewLineChannel();
        var lines = _lines;
        process.StandardInput.AutoFlush = true;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                    lines.Writer.TryWrite(line);
            }
            catch (Exception ex) { lines.Writer.TryComplete(ex); }
            finally { lines.Writer.TryComplete(); }
        });
        _ = process.StandardError.ReadToEndAsync();

        _options.Clear(); EngineAuthor = ""; ResolvedEvalPath = null;
        Send("uci");
        await WaitForAsync(line =>
        {
            if (line.StartsWith("id name ", StringComparison.Ordinal)) EngineName = line[8..];
            if (line.StartsWith("id author ", StringComparison.Ordinal)) EngineAuthor = line[10..];
            if (UciEngineOption.Parse(line) is { } option) _options[option.Name] = option;
            return line == "uciok";
        }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        ConfigureEngine(path, _options, Send);
        _threads = _hash = _multiPv = _skill = -1;
        Interlocked.Exchange(ref _newGameRequested, 1);
    }

    private bool SetNumericOption(string name, int value)
    {
        if (!_options.TryGetValue(name, out var option)) return false;
        value = Math.Clamp(value, option.Minimum ?? 1, option.Maximum ?? int.MaxValue);
        Send($"setoption name {option.Name} value {value}");
        return true;
    }
    private void ConfigureEngine(string path, IReadOnlyDictionary<string, UciEngineOption> options, Action<string> send)
    {
        if (options.TryGetValue("UCI_Variant", out var variant))
        {
            if (!variant.Variants.Contains("xiangqi", StringComparer.OrdinalIgnoreCase))
                throw new NotSupportedException("该引擎未声明支持中国象棋（xiangqi）。");
            send("setoption name UCI_Variant value xiangqi");
        }
        if (!options.ContainsKey("EvalFile") && !string.IsNullOrWhiteSpace(OverrideEvalPath))
            throw new NotSupportedException("该引擎未提供 EvalFile 选项，请清空插件的权重文件设置。");
        if (options.TryGetValue("EvalFile", out var eval))
        {
            string? nnue = null;
            if (!string.IsNullOrWhiteSpace(OverrideEvalPath))
            {
                nnue = Path.GetFullPath(OverrideEvalPath);
                if (!File.Exists(nnue)) throw new FileNotFoundException("未找到该引擎的 NNUE 权重。", nnue);
            }
            else if (string.IsNullOrWhiteSpace(OverridePath)) nnue = Path.Combine(AppContext.BaseDirectory, "Engine", "pikafish.nnue");
            else
            {
                // An external version gets its own declared weights, never the app's
                // unrelated NNUE. If it embeds the default, leave that default alone.
                var folder = Path.GetDirectoryName(path)!;
                var declared = string.IsNullOrWhiteSpace(eval.DefaultValue) || eval.DefaultValue == "<empty>" ? "" : Path.GetFullPath(eval.DefaultValue, folder);
                if (File.Exists(declared)) nnue = declared;
                else if (File.Exists(Path.Combine(folder, "pikafish.nnue"))) nnue = Path.Combine(folder, "pikafish.nnue");
            }
            if (nnue is not null)
            {
                if (!File.Exists(nnue)) throw new FileNotFoundException("未找到该引擎的 NNUE 权重。", nnue);
                if (nnue.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("权重文件路径不能包含换行。");
                send($"setoption name EvalFile value {nnue}");
            }
            ResolvedEvalPath = nnue;
        }
        if (options.ContainsKey("UCI_ShowWDL")) send("setoption name UCI_ShowWDL value true");
        foreach (var (name, value) in OverrideRuleOptions)
        {
            if (!options.TryGetValue(name, out var rule))
                throw new NotSupportedException($"当前引擎没有声明规则参数 {name}，请重新检测引擎并保存配置。");
            send($"setoption name {rule.Name} value {rule.ValidateRuleValue(value)}");
        }
    }
    public async Task<EngineIdentity> ProbeAsync(CancellationToken ct)
    {
        // A one-ply legal search also verifies the selected binary can load its
        // weights, instead of treating uciok alone as a successful installation.
        var game = new PaddiXiangqi.Core.XiangqiGame();
        var result = await SearchAsync(PaddiXiangqi.Core.XiangqiGame.InitialFen, "",
            new EngineSettings(20, 1, 16, 1, 1) { MoveTimeOverrideMs = 300 }, null, ct,
            game.AllLegalMoves().Select(move => move.Uci).ToArray());
        if (!game.TryMoveUci(result.BestMove, out _)) throw new IOException("引擎未返回合法的象棋着法。");
        return new(EngineName, EngineAuthor, ResolvedEvalPath, _options.Values.ToArray());
    }

    private void Send(string command)
    {
        lock (_writeLock)
        {
            if (_process is not { HasExited: false }) throw new IOException("皮卡鱼引擎已退出。");
            _process.StandardInput.WriteLine(command);
        }
    }

    private void AbandonProcess()
    {
        Process? process;
        lock (_writeLock)
        {
            process = _process;
            _process = null;
            _lines.Writer.TryComplete();
        }
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        _threads = _hash = _multiPv = _skill = -1;
        Interlocked.Exchange(ref _newGameRequested, 1);
    }

    private async Task WaitForAsync(Func<string, bool> predicate, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        while (true)
        {
            string line;
            try { line = await _lines.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("皮卡鱼引擎初始化超时。");
            }
            catch (ChannelClosedException)
            {
                throw new IOException("皮卡鱼引擎在初始化期间退出。");
            }
            if (predicate(line)) return;
        }
    }

    public static bool TryParseInfo(string line, out EngineInfo info)
    {
        info = new EngineInfo(0, 1, null, null, 0, "");
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int depth = 0, multiPv = 1;
        int? cp = null, mate = null, wins = null, draws = null, losses = null;
        long nodes = 0;
        long? nps = null;
        string pv = "";
        for (var i = 1; i < words.Length - 1; i++)
        {
            switch (words[i])
            {
                case "depth": int.TryParse(words[++i], out depth); break;
                case "multipv": int.TryParse(words[++i], out multiPv); break;
                case "nodes": long.TryParse(words[++i], out nodes); break;
                case "nps":
                    if (long.TryParse(words[++i], out var parsedNps)) nps = parsedNps;
                    break;
                case "score" when i + 2 < words.Length:
                    if (words[i + 1] == "cp" && int.TryParse(words[i + 2], out var score)) cp = score;
                    if (words[i + 1] == "mate" && int.TryParse(words[i + 2], out var mateScore)) mate = mateScore;
                    i += 2;
                    break;
                case "wdl" when i + 3 < words.Length:
                    if (int.TryParse(words[i + 1], out var w) && int.TryParse(words[i + 2], out var d)
                        && int.TryParse(words[i + 3], out var l))
                    { wins = w; draws = d; losses = l; }
                    i += 3;
                    break;
                case "pv": pv = string.Join(' ', words[(i + 1)..]); i = words.Length; break;
            }
        }
        if (depth <= 0 || pv.Length == 0) return false;
        info = new EngineInfo(depth, multiPv, cp, mate, nodes, pv, wins, draws, losses, nps);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_process is { HasExited: false } process)
            {
                Send("quit");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch { }
        AbandonProcess();
        _gate.Dispose();
    }
}

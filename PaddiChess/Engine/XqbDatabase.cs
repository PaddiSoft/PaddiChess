using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaddiXiangqi.Engine;

public sealed record BookMove(string Move, int Score, long Wins, long Draws, long Losses);

/// <summary>Read-only, indexed and time-bounded access to the OS SQLite runtime.</summary>
public sealed class XqbDatabase : IDisposable
{
    private IntPtr _db;
    private readonly Native.Progress _progress;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    static XqbDatabase() => NativeLibrary.SetDllImportResolver(typeof(XqbDatabase).Assembly,
        (name, assembly, path) => name == "paddi_sqlite" ? NativeLibrary.Load(
            OperatingSystem.IsWindows() ? "winsqlite3.dll" : OperatingSystem.IsMacOS() ? "/usr/lib/libsqlite3.dylib" : "libsqlite3.so.0",
            assembly, path) : IntPtr.Zero);

    public XqbDatabase(string path, CancellationToken ct = default)
    {
        // READONLY | NOMUTEX: each query has its own connection and stays on one worker.
        var result = Native.sqlite3_open_v2(Path.GetFullPath(path), out _db, 1 | 0x8000, IntPtr.Zero);
        _progress = _ => ct.IsCancellationRequested || _elapsed.ElapsedMilliseconds > 500 ? 1 : 0;
        if (result != 0) { Dispose(); throw new IOException("无法只读打开开局库。"); }
        Native.sqlite3_progress_handler(_db, 1000, _progress, IntPtr.Zero);
        try { Query("PRAGMA trusted_schema=OFF", _ => { }); Query("PRAGMA query_only=ON", _ => { }); }
        catch { Dispose(); throw; }
    }

    public void Validate()
    {
        string? version = null, type = null;
        Query("SELECT name,value FROM information LIMIT 32", s =>
        { var key = Text(s, 0); if (key == "version") version = Text(s, 1); if (key == "type") type = Text(s, 1); });
        if (version != "1" || type != "xiangqi") throw new FormatException("仅支持象棋 XQB v1 开局库；OBK、BIN 和棋谱文件不能直接导入。");
        var indexed = false;
        Query("EXPLAIN QUERY PLAN SELECT move,score,win,draw,lost,valid FROM book WHERE key=x'00' LIMIT 256", s =>
        { if (Text(s, 3).Contains("SEARCH", StringComparison.OrdinalIgnoreCase)) indexed = true; });
        if (!indexed) throw new FormatException("此开局库缺少 key 查询索引，请先在开局库编辑器中建立索引。");
    }

    public IReadOnlyList<BookMove> Lookup(string fen)
    {
        var key = XqbCodec.Encode(fen); var moves = new List<BookMove>();
        Query($"SELECT move,score,win,draw,lost FROM book WHERE key=x'{Convert.ToHexString(key.Bytes)}' AND valid=1 LIMIT 256", s =>
        {
            var encoded = Native.sqlite3_column_int64(s, 0);
            if (encoded is < 0 or > 65535) return;
            var move = key.DecodeMove((int)encoded);
            if (move.Length == 4) moves.Add(new(move, (int)Math.Clamp(Native.sqlite3_column_int64(s, 1), int.MinValue, int.MaxValue),
                Math.Max(0, Native.sqlite3_column_int64(s, 2)), Math.Max(0, Native.sqlite3_column_int64(s, 3)), Math.Max(0, Native.sqlite3_column_int64(s, 4))));
        });
        return moves;
    }
    private static string Text(IntPtr s, int i) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(s, i)) ?? "";
    private void Query(string sql, Action<IntPtr> row)
    {
        if (Native.sqlite3_prepare_v2(_db, sql, -1, out var statement, IntPtr.Zero) != 0)
            throw new FormatException("开局库结构损坏或不是兼容的 XQB 文件。");
        try
        {
            int status;
            while ((status = Native.sqlite3_step(statement)) == 100) row(statement);
            if (status != 101) throw new IOException("开局库查询失败或超过时间上限。");
        }
        finally { Native.sqlite3_finalize(statement); }
    }
    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        Native.sqlite3_progress_handler(_db, 0, null, IntPtr.Zero);
        Native.sqlite3_close(_db); _db = IntPtr.Zero;
        GC.KeepAlive(_progress);
    }
    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Progress(IntPtr context);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_close(IntPtr db);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport("paddi_sqlite", CallingConvention = CallingConvention.Cdecl)] public static extern void sqlite3_progress_handler(IntPtr db, int operations, Progress? callback, IntPtr context);
    }
}

namespace PaddiXiangqi.Engine;

public interface IOpeningBookReader : IDisposable
{
    string FormatName { get; }
    string ValueLabel => "库分";
    void Validate(bool full = false);
    IReadOnlyList<BookMove> Lookup(string fen);
}

public static class OpeningBookReader
{
    public static bool HasSupportedExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xqb" or ".obk" or ".bin" or ".dat";

    public static void RequireStandaloneDatabase(string path)
    {
        foreach (var suffix in new[] { "-wal", "-journal" })
            if (File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0)
                throw new IOException("开局库正在编辑或有未合并日志。请在原软件关闭/导出开局库后再导入。");
    }

    public static IOpeningBookReader Open(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[16]; header.Clear();
        using (var file = File.OpenRead(path))
            if (file.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8))
            {
                RequireStandaloneDatabase(path);
                using var schema = new XqbDatabase(path, ct);
                var tables = schema.ReadMetadata("SELECT name FROM sqlite_master WHERE type='table' LIMIT 128", 1);
                if (tables.Any(t => t[0].Equals("bhobk", StringComparison.OrdinalIgnoreCase)))
                    return new BingheBook(path, ct);
                if (tables.Any(t => t[0] == "information") && tables.Any(t => t[0] == "book"))
                    return new XqbDatabase(path, ct);
                throw new FormatException("不支持的 SQLite 开局库结构；支持象棋 XQB v1 和兵河 OBK。");
            }
        if (Path.GetExtension(path).ToLowerInvariant() is ".bin" or ".dat")
        {
            // jqxq reserves the last four bytes as zero; a valid second ElephantEye
            // record always has a nonzero move in these bytes. Full validation is on import.
            var jqxq = header[12..16].SequenceEqual(new byte[4]) &&
                MailboxBookCodec.Decode(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[8..]), true, false).Length == 4;
            return new BinaryOpeningBook(path, jqxq, ct);
        }
        throw new FormatException("未识别到受支持的开局库。支持 XQB v1、兵河 OBK、ElephantEye BIN/DAT、jqxq BIN；加密库、棋谱和其他同后缀格式不能直接使用。");
    }
}

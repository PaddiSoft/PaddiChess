using System.Buffers.Binary;
using System.Diagnostics;

namespace PaddiXiangqi.Engine;

/// <summary>Sorted, little-endian ElephantEye (8-byte) or jqxq (16-byte) records.</summary>
public sealed class BinaryOpeningBook(string path, bool jqxq, CancellationToken ct = default) : IOpeningBookReader
{
    private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    public string FormatName => jqxq ? "jqxq BIN" : "ElephantEye BIN/DAT";
    private int EntrySize => jqxq ? 16 : 8;
    public string ValueLabel => "库权重";
    private long Count => _file.Length / EntrySize;

    public void Validate(bool full = false)
    {
        ct.ThrowIfCancellationRequested();
        if (_file.Length == 0 || _file.Length % EntrySize != 0) throw Invalid();
        // A full, streaming check happens on import only, never on the move hot path.
        ulong previous = 0; ushort previousMove = 0;
        if (full)
        {
            _file.Position = 0;
            var buffer = new byte[64 * 1024]; var first = true;
            while (_file.Position < _file.Length)
            {
                ct.ThrowIfCancellationRequested();
                var length = (int)Math.Min(buffer.Length, _file.Length - _file.Position);
                _file.ReadExactly(buffer.AsSpan(0, length));
                for (var offset = 0; offset < length; offset += EntrySize)
                {
                    var row = Decode(buffer.AsSpan(offset, EntrySize));
                    if ((!first && (row.Key < previous || (jqxq && row.Key == previous && row.Move <= previousMove))) || !Valid(row)) throw Invalid();
                    first = false; previous = row.Key; previousMove = row.Move;
                }
            }
        }
        else
        {
            var first = Read(0); var last = Read(Count - 1);
            if (!Valid(first) || !Valid(last) || first.Key > last.Key) throw Invalid();
        }
    }

    public IReadOnlyList<BookMove> Lookup(string fen)
    {
        var watch = Stopwatch.StartNew(); var result = new List<BookMove>();
        for (var mirror = 0; mirror < 2; mirror++)
        {
            var key = jqxq ? JqxqBookCodec.Key(fen, mirror != 0) : ElephantEyeBookCodec.Key(fen, mirror != 0);
            long low = 0, high = Count;
            while (low < high)
            {
                Check(); var mid = low + (high - low) / 2;
                if (Read(mid).Key < key) low = mid + 1; else high = mid;
            }
            for (var index = low; index < Count && result.Count < 512; index++)
            {
                Check(); var row = Read(index); if (row.Key != key) break;
                var move = MailboxBookCodec.Decode(row.Move, jqxq, mirror != 0);
                if (move.Length == 4 && Valid(row)) result.Add(new(move, row.Weight, 0, 0, 0));
            }
        }
        return result;
        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds > 500) throw new IOException("开局库查询超过时间上限。");
        }
    }
    private (ulong Key, ushort Move, ushort Weight, uint Reserved) Read(long index)
    {
        Span<byte> bytes = stackalloc byte[EntrySize]; _file.Position = index * EntrySize; _file.ReadExactly(bytes);
        return Decode(bytes);
    }
    private (ulong Key, ushort Move, ushort Weight, uint Reserved) Decode(ReadOnlySpan<byte> bytes) => jqxq
        ? (BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]))
        : (BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), 0);
    private bool Valid((ulong Key, ushort Move, ushort Weight, uint Reserved) row) =>
        MailboxBookCodec.Decode(row.Move, jqxq, false).Length == 4 && (!jqxq || (row.Reserved == 0 && row.Weight > 0));
    private FormatException Invalid() => new($"文件不是有效的 {FormatName} 库（条目损坏、未排序或采用其他编码）。不支持国际象棋 Polyglot BIN；不能仅修改后缀转换格式。");
    public void Dispose() => _file.Dispose();
}

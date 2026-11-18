using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace PaddiXiangqi.Engine;

/// <summary>
/// Binghe stores unsigned Zobrist keys in SQLite INTEGER or raw REAL bits. Its index
/// may use unsigned ordering instead of SQLite's numeric ordering. Read it directly;
/// passing the keys through double/SQL loses NaN payloads and searches the wrong branch.
/// SQLite file layout: https://www.sqlite.org/fileformat2.html
/// </summary>
public sealed class BingheBook : IOpeningBookReader
{
    private readonly FileStream _file;
    private readonly CancellationToken _ct;
    private readonly int _pageSize, _usable;
    private readonly long _pageCount, _tableRoot, _indexRoot;
    private readonly Dictionary<string, int> _columns;
    private readonly Dictionary<long, byte[]> _cache = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private bool _fullValidation;
    private long _visits;
    public string FormatName => "兵河 OBK";

    public BingheBook(string path, CancellationToken ct = default)
    {
        _ct = ct;
        OpeningBookReader.RequireStandaloneDatabase(path);
        _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Span<byte> header = stackalloc byte[100]; _file.ReadExactly(header);
            if (!header[..16].SequenceEqual("SQLite format 3\0"u8)) throw Invalid();
            _pageSize = BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
            if (_pageSize == 1) _pageSize = 65536;
            _usable = _pageSize - header[20];
            if (_pageSize < 512 || (_pageSize & (_pageSize - 1)) != 0 || _usable < 480 ||
                _file.Length % _pageSize != 0 || header[18] is < 1 or > 2 || header[19] is < 1 or > 2 ||
                header[21] != 64 || header[22] != 32 || header[23] != 32) throw Invalid();
            _pageCount = _file.Length / _pageSize;
            using var schema = new XqbDatabase(path, ct);
            var objects = schema.ReadMetadata("SELECT type,name,rootpage,sql FROM sqlite_master WHERE lower(tbl_name)='bhobk' LIMIT 128", 4);
            var table = objects.SingleOrDefault(r => r[0] == "table") ?? throw Invalid();
            if (table[3].Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            _tableRoot = Parse(table[2]);
            var columns = schema.ReadMetadata("PRAGMA table_info(bhobk)", 6);
            _columns = columns.ToDictionary(c => c[1], c => checked((int)Parse(c[0])), StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "vkey", "vmove", "vscore", "vwin", "vdraw", "vlost", "vvalid" })
                if (!_columns.ContainsKey(name)) throw new FormatException("兵河 OBK 缺少 " + name + " 字段。");
            foreach (var index in objects.Where(r => r[0] == "index"))
            {
                var info = schema.ReadMetadata("PRAGMA index_xinfo(\"" + index[1].Replace("\"", "\"\"") + "\")", 6);
                var keys = info.Where(r => r[5] == "1").ToArray();
                if (keys.Length == 1 && keys[0][2].Equals("vkey", StringComparison.OrdinalIgnoreCase) && keys[0][3] == "0" &&
                    info.Any(r => r[1] == "-1") && !index[3].Contains("WHERE", StringComparison.OrdinalIgnoreCase))
                { _indexRoot = Parse(index[2]); break; }
            }
            if (_indexRoot == 0) throw new FormatException("兵河 OBK 需要按 vkey 升序的完整索引，请先在原软件整理/导出开局库。");
        }
        catch { _file.Dispose(); throw; }
    }

    public void Validate(bool full = false)
    {
        _fullValidation = full; _elapsed.Restart(); _visits = 0;
        try
        {
            if (Page(_tableRoot).Type is not (5 or 13) || Page(_indexRoot).Type is not (2 or 10)) throw Invalid();
            if (!full) return;
            Field previous = default; long previousId = 0; var first = true; var sampled = 0;
            var unsignedOrder = true; var sqliteOrder = true;
            foreach (var entry in EnumerateIndex(_indexRoot, 0))
            {
                if (!first)
                {
                    var unsigned = Compare(entry.Key, previous, false);
                    var numeric = Compare(entry.Key, previous, true);
                    unsignedOrder &= unsigned > 0 || (unsigned == 0 && entry.Id > previousId);
                    sqliteOrder &= numeric > 0 || (numeric == 0 && entry.Id > previousId);
                    if (!unsignedOrder && !sqliteOrder)
                        throw new FormatException("兵河 OBK 索引顺序损坏，请从原软件重新导出。");
                }
                if (entry.Key.Type != 0 && sampled++ < 16)
                {
                    var record = TableRow(entry.Id);
                    if (record is null || Key(record[_columns["vkey"]]) != Key(entry.Key)) throw Invalid();
                    ReadMove(record, false);
                }
                first = false; previous = entry.Key; previousId = entry.Id;
            }
        }
        finally { _fullValidation = false; _elapsed.Restart(); _visits = 0; }
    }

    public IReadOnlyList<BookMove> Lookup(string fen)
    {
        _elapsed.Restart(); _visits = 0;
        var moves = new List<BookMove>();
        for (var mirror = 0; mirror < 2; mirror++)
        {
            var key = BingheBookCodec.Key(fen, mirror != 0); var ids = new HashSet<long>();
            var target = new Field(key > long.MaxValue ? 7UL : 6UL, key);
            // Both legacy unsigned-order indexes and ordinary SQLite rebuilt indexes exist.
            FindIndex(_indexRoot, target, ids, 0, false);
            FindIndex(_indexRoot, target, ids, 0, true);
            // SQLite reindex turns NaN keys into NULL index entries; the table retains bits.
            if (target.Type == 7 && double.IsNaN(BitConverter.UInt64BitsToDouble(key)))
                FindIndex(_indexRoot, new Field(0, 0), ids, 0, true);
            foreach (var id in ids)
            {
                var record = TableRow(id);
                if (record is null) throw Invalid();
                if (Key(record[_columns["vkey"]]) != key) continue;
                var move = ReadMove(record, mirror != 0);
                if (move is not null) moves.Add(move);
            }
        }
        return moves;
    }

    private BookMove? ReadMove(Field[] record, bool mirror)
    {
        if (record.Length <= _columns.Values.Max()) throw Invalid();
        long Number(string name) => Integer(record[_columns[name]]);
        if (Number("vvalid") != 1) return null;
        var encoded = Number("vmove");
        var move = encoded is >= 0 and <= 65535 ? MailboxBookCodec.Decode((int)encoded, true, mirror) : "";
        return move.Length == 4 ? new(move, (int)Math.Clamp(Number("vscore"), int.MinValue, int.MaxValue),
            Math.Max(0, Number("vwin")), Math.Max(0, Number("vdraw")), Math.Max(0, Number("vlost"))) : null;
    }

    private void FindIndex(long pageId, Field key, HashSet<long> ids, int depth, bool numeric)
    {
        if (depth > 64 || ids.Count >= 256) { if (depth > 64) throw Invalid(); return; }
        var page = Page(pageId); if (page.Type is not (2 or 10)) throw Invalid();
        int low = 0, high = page.Cells.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (Compare(IndexEntry(page, mid).Key, key, numeric) < 0) low = mid + 1; else high = mid;
        }
        if (page.Type == 2) FindIndex(Child(page, low), key, ids, depth + 1, numeric);
        for (var i = low; i < page.Cells.Length && ids.Count < 256; i++)
        {
            var entry = IndexEntry(page, i); if (Compare(entry.Key, key, numeric) != 0) break;
            ids.Add(entry.Id);
            if (page.Type == 2) FindIndex(Child(page, i + 1), key, ids, depth + 1, numeric);
        }
    }

    private IEnumerable<(Field Key, long Id)> EnumerateIndex(long pageId, int depth)
    {
        if (depth > 64) throw Invalid();
        var page = Page(pageId); if (page.Type is not (2 or 10)) throw Invalid();
        for (var i = 0; i <= page.Cells.Length; i++)
        {
            if (page.Type == 2)
                foreach (var entry in EnumerateIndex(Child(page, i), depth + 1)) yield return entry;
            if (i < page.Cells.Length) yield return IndexEntry(page, i);
        }
    }
    private (Field Key, long Id) IndexEntry(BtreePage page, int index)
    {
        var offset = page.Cells[index] + (page.Type == 2 ? 4 : 0);
        var record = Record(Payload(page.Bytes, ref offset, false, out _));
        if (record.Length != 2) throw Invalid();
        var id = Integer(record[1]); if (id <= 0) throw Invalid();
        _ = Key(record[0]);
        return (record[0], id);
    }
    private Field[]? TableRow(long id)
    {
        var pageId = _tableRoot;
        for (var depth = 0; depth < 64; depth++)
        {
            var page = Page(pageId); if (page.Type is not (5 or 13)) throw Invalid();
            int low = 0, high = page.Cells.Length;
            long RowId(int index)
            {
                var offset = page.Cells[index];
                if (page.Type == 5) offset += 4; else Varint(page.Bytes.AsSpan(0, _usable), ref offset);
                return unchecked((long)Varint(page.Bytes.AsSpan(0, _usable), ref offset));
            }
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (RowId(mid) < id) low = mid + 1; else high = mid;
            }
            if (page.Type == 5) { pageId = Child(page, low); continue; }
            if (low == page.Cells.Length || RowId(low) != id) return null;
            var pos = page.Cells[low];
            var record = Record(Payload(page.Bytes, ref pos, true, out _));
            if (record.Length <= _columns.Values.Max()) throw Invalid();
            return record;
        }
        throw Invalid();
    }

    private record BtreePage(byte[] Bytes, int Type, int[] Cells, long RightChild);
    private BtreePage Page(long id)
    {
        Check();
        if (++_visits > (_fullValidation ? _pageCount * 20 : 4096)) throw Invalid();
        var bytes = ReadPage(id); var start = id == 1 ? 100 : 0; var type = bytes[start];
        if (type is not (2 or 5 or 10 or 13)) throw Invalid();
        var interior = type is 2 or 5;
        var count = U16(bytes, start + 3); var pointers = start + (interior ? 12 : 8);
        if (pointers + 2 * count > _usable) throw Invalid();
        var cells = new int[count];
        for (var i = 0; i < count; i++)
        {
            cells[i] = U16(bytes, pointers + i * 2);
            if (cells[i] < pointers + 2 * count || cells[i] >= _usable) throw Invalid();
        }
        return new(bytes, type, cells, interior ? U32(bytes, start + 8) : 0);
    }
    private static long Child(BtreePage page, int index) => index == page.Cells.Length ? page.RightChild : U32(page.Bytes, page.Cells[index]);
    private byte[] ReadPage(long id)
    {
        Check();
        if (id <= 0 || id > _pageCount) throw Invalid();
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var bytes = new byte[_pageSize]; _file.Position = (id - 1) * _pageSize; _file.ReadExactly(bytes);
        if (_cache.Count >= 64) _cache.Clear(); // bounded memory, including during large imports
        _cache[id] = bytes; return bytes;
    }

    private byte[] Payload(byte[] page, ref int offset, bool table, out long rowId)
    {
        var span = page.AsSpan(0, _usable);
        var size = Varint(span, ref offset);
        if (size > 1024 * 1024) throw new FormatException("OBK 单条记录超过 1 MiB，请移除过大的注释后重新导出。");
        rowId = table ? unchecked((long)Varint(span, ref offset)) : 0;
        var count = (int)size; var maximum = table ? _usable - 35 : ((_usable - 12) * 64 / 255) - 23;
        var local = count;
        if (count > maximum)
        {
            var minimum = ((_usable - 12) * 32 / 255) - 23;
            local = minimum + (count - minimum) % (_usable - 4);
            if (local > maximum) local = minimum;
        }
        if (offset > _usable - local - (count > local ? 4 : 0)) throw Invalid();
        var result = new byte[count]; span.Slice(offset, local).CopyTo(result); offset += local;
        if (local == count) return result;
        var next = U32(page, offset); var written = local; var seen = new HashSet<long>();
        while (written < count)
        {
            if (!seen.Add(next)) throw Invalid();
            var overflow = ReadPage(next); var length = Math.Min(_usable - 4, count - written);
            overflow.AsSpan(4, length).CopyTo(result.AsSpan(written)); written += length; next = U32(overflow, 0);
        }
        if (next != 0) throw Invalid();
        return result;
    }

    private readonly record struct Field(ulong Type, ulong Raw);
    private static Field[] Record(byte[] bytes)
    {
        var pos = 0; var header = Varint(bytes, ref pos);
        if (header > (ulong)bytes.Length || header < (ulong)pos) throw Invalid();
        var data = (int)header; var fields = new List<Field>();
        while (pos < (int)header)
        {
            if (fields.Count >= 128) throw Invalid();
            var type = Varint(bytes.AsSpan(0, (int)header), ref pos);
            var length = type switch { 0 or 8 or 9 => 0UL, >= 1 and <= 4 => type, 5 => 6UL, 6 or 7 => 8UL,
                10 or 11 => throw Invalid(), _ => (type - 12) / 2 };
            if (length > (ulong)(bytes.Length - data)) throw Invalid();
            ulong raw = type == 9 ? 1UL : 0;
            if (type is >= 1 and <= 7)
                for (var b = 0; b < (int)length; b++) raw = (raw << 8) | bytes[data + b];
            if (type is >= 1 and <= 5 && (bytes[data] & 128) != 0) raw |= ulong.MaxValue << ((int)length * 8);
            fields.Add(new(type, raw)); data += (int)length;
        }
        if (data != bytes.Length) throw Invalid();
        return fields.ToArray();
    }
    private static int Compare(Field a, Field b, bool numeric)
    {
        if (a.Type == 0 || b.Type == 0) return a.Type == 0 ? (b.Type == 0 ? 0 : -1) : 1;
        if (!numeric) return a.Raw.CompareTo(b.Raw);
        if (a.Type != 7 && b.Type != 7) return unchecked((long)a.Raw).CompareTo(unchecked((long)b.Raw));
        // In this format REAL is used only for high-bit unsigned keys (negative doubles).
        // Keep integer/integer comparisons exact even above the double precision limit.
        double Number(Field f) => f.Type == 7 ? BitConverter.UInt64BitsToDouble(f.Raw) : unchecked((long)f.Raw);
        return Number(a).CompareTo(Number(b));
    }
    private static ulong? Key(Field field) => field.Type == 0 ? null : field.Type is >= 1 and <= 9 ? field.Raw : throw Invalid();
    private static long Integer(Field field) => field.Type is >= 0 and <= 6 or 8 or 9 ? unchecked((long)field.Raw) : throw Invalid();
    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong result = 0;
        for (var i = 0; i < 9; i++)
        {
            if (offset >= bytes.Length) throw Invalid();
            var b = bytes[offset++];
            if (i == 8) return (result << 8) | b;
            result = (result << 7) | (uint)(b & 127);
            if ((b & 128) == 0) return result;
        }
        throw Invalid();
    }
    private static int U16(byte[] bytes, int offset) => offset >= 0 && offset <= bytes.Length - 2 ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset)) : throw Invalid();
    private static uint U32(byte[] bytes, int offset) => offset >= 0 && offset <= bytes.Length - 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) : throw Invalid();
    private static long Parse(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw Invalid();
    private void Check()
    {
        _ct.ThrowIfCancellationRequested();
        if (!_fullValidation && _elapsed.ElapsedMilliseconds > 500) throw new IOException("开局库查询超过时间上限。");
    }
    private static FormatException Invalid() => new("兵河 OBK 数据或索引损坏，或采用不受支持的编码。");
    public void Dispose() => _file.Dispose();
}

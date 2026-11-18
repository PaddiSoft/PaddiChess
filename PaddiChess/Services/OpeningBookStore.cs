using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Services;

public sealed record OpeningBookProfile(string Id, string Name, string FileName)
{
    public override string ToString() => Name;
}

public sealed class OpeningBookStore(string dataDirectory, string? cacheDirectory = null)
{
    public const string BundledId = "bundled-guagudao-20261118";
    public const string BundledName = "内置 · 新编刮骨刀 2026-11-18";
    public string DirectoryPath => Path.Combine(dataDirectory, "OpeningBooks");
    public string BundledPath => Path.Combine(cacheDirectory ?? DirectoryPath, BundledId + ".xqb");
    private static readonly SemaphoreSlim PrepareGate = new(1, 1);

    public async Task PrepareBundledAsync(CancellationToken ct = default)
    {
        await PrepareGate.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "OpeningBooks");
            using var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(assets, "bundled.json"), ct).ConfigureAwait(false));
            var expected = meta.RootElement.GetProperty("sha256").GetString()!;
            var size = meta.RootElement.GetProperty("size").GetInt64();
            // Only a successfully verified atomic extraction creates this marker.
            var marker = BundledPath + ".sha256";
            if (File.Exists(BundledPath) && new FileInfo(BundledPath).Length == size && File.Exists(marker) &&
                await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false) == expected) return;
            Directory.CreateDirectory(Path.GetDirectoryName(BundledPath)!);
            temporary = BundledPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var input = File.OpenRead(Path.Combine(assets, meta.RootElement.GetProperty("file").GetString()!)))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            await using (var output = File.Create(temporary))
                await gzip.CopyToAsync(output, ct).ConfigureAwait(false);
            await using (var file = File.OpenRead(temporary))
                if (file.Length != size || Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant() != expected)
                    throw new IOException("内置开局库校验失败。");
            using (var db = new XqbDatabase(temporary, ct)) db.Validate();
            File.Move(temporary, BundledPath, true); temporary = null;
            await File.WriteAllTextAsync(marker, expected, ct).ConfigureAwait(false);
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
            PrepareGate.Release();
        }
    }

    public string Resolve(OpeningBookProfile profile)
    {
        if (profile.Id == BundledId) return BundledPath;
        // Only a managed file name is persisted. Imported files never execute code.
        if (profile.FileName != Path.GetFileName(profile.FileName) || !profile.FileName.EndsWith(".xqb", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("开局库文件名无效。");
        return Path.Combine(DirectoryPath, profile.FileName);
    }

    public async Task<OpeningBookProfile> ImportAsync(string source, CancellationToken ct = default)
    {
        if (!source.EndsWith(".xqb", StringComparison.OrdinalIgnoreCase)) throw new FormatException("请选择 .xqb 开局库。");
        Directory.CreateDirectory(DirectoryPath);
        var id = Guid.NewGuid().ToString("N"); var file = id + ".xqb";
        var destination = Path.Combine(DirectoryPath, file);
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(destination))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await Task.Run(() => { using var db = new XqbDatabase(destination, ct); db.Validate(); }, ct).ConfigureAwait(false);
            return new(id, Path.GetFileNameWithoutExtension(source), file);
        }
        catch { File.Delete(destination); throw; }
    }
}

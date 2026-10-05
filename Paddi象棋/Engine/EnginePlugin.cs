using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaddiXiangqi.Engine;

/// <summary>A UCI engine runs as an isolated process, with its own binary and optional weights.</summary>
public sealed class EnginePlugin
{
    public const string BundledId = "builtin-pikafish";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新引擎";
    public string ExecutablePath { get; set; } = "";
    public string EvalFilePath { get; set; } = "";
    public string DetectedName { get; set; } = "";
    public string Author { get; set; } = "";
    public Dictionary<string, string> RuleOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<UciEngineOption> DetectedRuleOptions { get; set; } = [];
    [JsonIgnore] public bool IsBundled => Id == BundledId;
    public static EnginePlugin Bundled() => new()
    {
        Id = BundledId, Name = "内置皮卡鱼", DetectedName = "Pikafish 2026-09-25",
        Author = "the Pikafish developers (see AUTHORS file)",
        // This fixed release's UCI declarations let a fresh install display its
        // rules immediately. The bundled-engine integration test checks parity.
        DetectedRuleOptions =
        [
            new("Mate Threat Depth", "spin", "10", 0, 10, []),
            new("Repetition Rule", "combo", "AsianRule", null, null,
                ["AsianRule", "ChineseRule", "SkyRule", "ComputerRule", "YitianRule", "AllowChase", "NoJudgement"]),
            new("Draw Rule", "combo", "None", null, null,
                ["None", "DrawAsBlackWin", "DrawAsRedWin", "DrawRepAsBlackWin", "DrawRepAsRedWin"]),
            new("Sixty Move Rule", "check", "true", null, null, []),
            new("Rule60MaxPly", "spin", "120", 1, 150, [])
        ]
    };
    public PikafishClient CreateClient() => new()
    {
        OverridePath = IsBundled ? null : ExecutablePath,
        OverrideEvalPath = IsBundled ? null : EvalFilePath,
        OverrideRuleOptions = new Dictionary<string, string>(RuleOptions, StringComparer.OrdinalIgnoreCase)
    };
    public override string ToString() => Name;

    public static EnginePlugin Import(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<EnginePluginManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new FormatException("引擎插件文件为空。");
        if (manifest.Protocol != "UCI" || manifest.SchemaVersion != 1)
            throw new FormatException("当前支持版本 1 的 UCI 象棋引擎插件。");
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        string Resolve(string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path, folder);
        var executable = Resolve(manifest.Executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("插件中的引擎程序不存在。", executable);
        return new() { Name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(executable) : manifest.Name,
            ExecutablePath = executable, EvalFilePath = Resolve(manifest.EvalFile),
            RuleOptions = manifest.RuleOptions ?? new(StringComparer.OrdinalIgnoreCase) };
    }
    public void Export(string manifestPath)
    {
        if (IsBundled) throw new InvalidOperationException("内置引擎随客户端提供，无需导出插件。");
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var manifest = new EnginePluginManifest { Name = Name, Executable = Path.GetRelativePath(folder, ExecutablePath),
            EvalFile = string.IsNullOrWhiteSpace(EvalFilePath) ? "" : Path.GetRelativePath(folder, EvalFilePath),
            RuleOptions = new(RuleOptions, StringComparer.OrdinalIgnoreCase) };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
public sealed class EnginePluginManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Protocol { get; set; } = "UCI";
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public string EvalFile { get; set; } = "";
    public Dictionary<string, string> RuleOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

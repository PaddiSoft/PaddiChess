using System.Text.Json;
using System.Text.Json.Nodes;
using PaddiXiangqi.Engine;

namespace PaddiXiangqi.Services;

public sealed class LlmServiceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新服务";
    public string BaseUrl { get; set; } = "";
    public List<string> Models { get; set; } = [];
    public List<string> EnabledModels { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class AppPreferences
{
    public bool RedEngine { get; set; }
    public bool BlackEngine { get; set; }
    public bool AutoAnalyze { get; set; } = true;
    public bool Flipped { get; set; }
    public bool SoundEnabled { get; set; } = true;
    public bool ShowCoordinates { get; set; } = true;
    public int AnimationDurationMs { get; set; } = 230;
    public int ExternalSyncSpeed { get; set; }
    public int ExternalInputDelivery { get; set; }
    public bool ExternalPrimeWindow { get; set; } = true;
    public int ExternalScoreMode { get; set; }
    public int Level { get; set; } = 10;
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    public int HashMb { get; set; } = 128;
    public int AnalysisLines { get; set; } = 3;
    public int ThinkSeconds { get; set; } = 12;
    public int MaxDepth { get; set; } = 30;
    public bool UseDepthLimit { get; set; }
    public EnginePhaseSettings EnginePhases { get; set; } = new();
    public string EnginePath { get; set; } = "";
    public List<EnginePlugin> EnginePlugins { get; set; } = [];
    public string DefaultEngineId { get; set; } = EnginePlugin.BundledId;
    public bool EnginePluginsMigrated { get; set; }
    public string LlmBaseUrl { get; set; } = "";
    public string LlmModel { get; set; } = "";
    public string RedLlmBaseUrl { get; set; } = "";
    public string RedLlmModel { get; set; } = "";
    public string RedLlmReasoning { get; set; } = "auto";
    public string BlackLlmBaseUrl { get; set; } = "";
    public string BlackLlmModel { get; set; } = "";
    public string BlackLlmReasoning { get; set; } = "auto";
    public bool LlmProfilesMigrated { get; set; }
    public List<LlmServiceProfile> LlmServices { get; set; } = [];
    public string RedLlmServiceId { get; set; } = "";
    public string BlackLlmServiceId { get; set; } = "";
    public bool LlmServiceDirectoryMigrated { get; set; }

    public static string FilePath => AppDataPaths.Current.SettingsFilePath;

    public static AppPreferences Load()
    {
        AppPreferences preferences;
        try
        {
            var path = AppDataPaths.Current.PrepareSettingsLoad();
            if (File.Exists(path))
                preferences = DeserializeRecoverable(File.ReadAllText(path));
            else preferences = new AppPreferences();
        }
        catch { preferences = new AppPreferences(); /* A malformed settings file must never prevent the board from opening. */ }
        preferences.LlmBaseUrl ??= "";
        preferences.NormalizeEnginePlugins();
        preferences.EnginePhases ??= new();
        preferences.LlmModel ??= "";
        preferences.RedLlmBaseUrl ??= "";
        preferences.BlackLlmBaseUrl ??= "";
        preferences.RedLlmModel ??= "";
        preferences.BlackLlmModel ??= "";
        preferences.RedLlmReasoning ??= "auto";
        preferences.BlackLlmReasoning ??= "auto";
        preferences.RedLlmServiceId ??= "";
        preferences.BlackLlmServiceId ??= "";
        if (!preferences.LlmProfilesMigrated)
        {
            if (string.IsNullOrWhiteSpace(preferences.RedLlmBaseUrl)) preferences.RedLlmBaseUrl = preferences.LlmBaseUrl;
            if (string.IsNullOrWhiteSpace(preferences.RedLlmModel)) preferences.RedLlmModel = preferences.LlmModel;
            if (string.IsNullOrWhiteSpace(preferences.BlackLlmBaseUrl)) preferences.BlackLlmBaseUrl = preferences.LlmBaseUrl;
            if (string.IsNullOrWhiteSpace(preferences.BlackLlmModel)) preferences.BlackLlmModel = preferences.LlmModel;
            preferences.LlmProfilesMigrated = true;
        }
        preferences.LlmServices ??= [];
        preferences.LlmServices = preferences.LlmServices.Where(profile => profile is not null).ToList();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in preferences.LlmServices)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !serviceIds.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
                serviceIds.Add(profile.Id);
            }
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "未命名服务" : profile.Name;
            profile.BaseUrl ??= "";
            profile.EnabledModels = NormalizeStrings(profile.EnabledModels);
            profile.Models = NormalizeStrings((profile.Models ?? []).Concat(profile.EnabledModels));
        }
        if (!preferences.LlmServiceDirectoryMigrated)
        {
            static string DisplayName(string url, int ordinal)
            {
                return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
                    ? uri.Host : $"API 服务 {ordinal}";
            }
            LlmServiceProfile EnsureService(string url, string model)
            {
                var existing = preferences.LlmServices.FirstOrDefault(profile =>
                    string.Equals(profile.BaseUrl.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new LlmServiceProfile { Name = DisplayName(url, preferences.LlmServices.Count + 1), BaseUrl = url };
                    preferences.LlmServices.Add(existing);
                }
                if (!string.IsNullOrWhiteSpace(model))
                {
                    if (!existing.Models.Contains(model)) existing.Models.Add(model);
                    if (!existing.EnabledModels.Contains(model)) existing.EnabledModels.Add(model);
                }
                return existing;
            }
            var redUrl = preferences.RedLlmBaseUrl.Trim();
            var blackUrl = preferences.BlackLlmBaseUrl.Trim();
            if (redUrl.Length > 0 && !preferences.LlmServices.Any(profile => profile.Id == preferences.RedLlmServiceId))
                preferences.RedLlmServiceId = EnsureService(redUrl, preferences.RedLlmModel).Id;
            if (blackUrl.Length > 0 && !preferences.LlmServices.Any(profile => profile.Id == preferences.BlackLlmServiceId))
                preferences.BlackLlmServiceId = EnsureService(blackUrl, preferences.BlackLlmModel).Id;
            if (preferences.LlmServices.Count == 0)
                preferences.LlmServices.Add(new LlmServiceProfile { Name = "默认服务", BaseUrl = preferences.LlmBaseUrl });
            preferences.LlmServiceDirectoryMigrated = true;
        }
        return preferences;
    }

    private static AppPreferences DeserializeRecoverable(string json)
    {
        // A type error in one setting must not discard all unrelated preferences.
        // Invalid JSON itself is handled by Load's fallback; well-formed JSON with
        // an invalid field keeps every other field. Never log this payload.
        if (JsonNode.Parse(json) is not JsonObject document) return new();
        while (true)
        {
            try { return document.Deserialize<AppPreferences>() ?? new(); }
            catch (JsonException error)
            {
                var path = error.Path;
                if (path is null || !path.StartsWith("$.", StringComparison.Ordinal)) throw;
                var end = path.IndexOfAny(['.', '['], 2);
                var property = end < 0 ? path[2..] : path[2..end];
                if (!document.Remove(property)) throw;
            }
        }
    }

    private static List<string> NormalizeStrings(IEnumerable<string>? values) =>
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal).ToList();

    public void NormalizeEnginePlugins()
    {
        EnginePlugins ??= [];
        var currentBundled = EnginePlugin.Bundled();
        var bundled = EnginePlugins.FirstOrDefault(plugin => plugin is not null && plugin.IsBundled) ?? currentBundled;
        // Only refresh metadata owned by the shipped binary. Keep the user's
        // saved rule values and default plugin when upgrading a cached release.
        bundled.DetectedName = currentBundled.DetectedName;
        bundled.Author = currentBundled.Author;
        bundled.DetectedRuleOptions = currentBundled.DetectedRuleOptions;
        EnginePlugins = EnginePlugins.Where(plugin => plugin is not null && !plugin.IsBundled).ToList();
        EnginePlugins.Insert(0, bundled);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in EnginePlugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Id) || !identifiers.Add(plugin.Id))
            {
                plugin.Id = Guid.NewGuid().ToString("N");
                identifiers.Add(plugin.Id);
            }
            if (string.IsNullOrWhiteSpace(plugin.Name)) plugin.Name = "未命名引擎";
            plugin.ExecutablePath ??= ""; plugin.EvalFilePath ??= "";
            plugin.DetectedName ??= ""; plugin.Author ??= "";
            var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in plugin.RuleOptions ?? [])
                if (!string.IsNullOrWhiteSpace(key) && value is not null) rules[key] = value;
            plugin.RuleOptions = rules;
            plugin.DetectedRuleOptions = (plugin.DetectedRuleOptions ?? [])
                .Where(option => option is not null && !string.IsNullOrWhiteSpace(option.Name) &&
                    !string.IsNullOrWhiteSpace(option.Type) &&
                    !(option.Minimum.HasValue && option.Maximum.HasValue && option.Minimum > option.Maximum))
                .Select(option => option with { DefaultValue = option.DefaultValue ?? "", Variants = NormalizeStrings(option.Variants) })
                .DistinctBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (!EnginePluginsMigrated && !string.IsNullOrWhiteSpace(EnginePath))
        {
            var plugin = EnginePlugins.FirstOrDefault(item => item.ExecutablePath == EnginePath);
            if (plugin is null)
            {
                plugin = new() { Name = Path.GetFileNameWithoutExtension(EnginePath), ExecutablePath = EnginePath };
                EnginePlugins.Add(plugin);
            }
            DefaultEngineId = plugin.Id;
        }
        if (!EnginePlugins.Any(plugin => plugin.Id == DefaultEngineId)) DefaultEngineId = EnginePlugin.BundledId;
        EnginePluginsMigrated = true;
        EnginePath = EnginePlugins.First(plugin => plugin.Id == DefaultEngineId).ExecutablePath;
    }

    public void Save()
    {
        string? temp = null;
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        catch { /* The game remains playable when a profile folder is read-only. */ }
        finally
        {
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

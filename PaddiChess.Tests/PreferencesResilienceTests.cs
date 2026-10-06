using System.Text.Json;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

[Collection("Desktop integration")]
public class PreferencesResilienceTests
{
    private sealed class SettingsFile : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH");
        public string Folder { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paddi-preferences-audit-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Folder, "settings.json");
        public SettingsFile(string json)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path, json);
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", Path);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PADDI_SETTINGS_PATH", _previous);
            Directory.Delete(Folder, true);
        }
    }

    [Fact]
    public void NullOptionalFieldsKeepValidHardwareAndExplicitReasoningPreferences()
    {
        using var file = new SettingsFile("""
            {"Threads":6,"HashMb":8192,"Level":20,"EnginePhases":null,"EnginePlugins":null,
             "LlmBaseUrl":null,"LlmModel":null,"RedLlmBaseUrl":null,"BlackLlmBaseUrl":null,
             "RedLlmModel":null,"BlackLlmModel":null,"LlmServices":null,
             "RedLlmReasoning":"high","BlackLlmReasoning":null,
             "RedLlmServiceId":null,"BlackLlmServiceId":null}
            """);
        var preferences = AppPreferences.Load();
        Assert.Equal(6, preferences.Threads);
        Assert.Equal(8192, preferences.HashMb);
        Assert.Equal(20, preferences.Level);
        Assert.Equal("high", preferences.RedLlmReasoning);
        Assert.Equal("auto", preferences.BlackLlmReasoning);
        Assert.NotNull(preferences.EnginePhases);
        Assert.True(Assert.Single(preferences.EnginePlugins).IsBundled);
        Assert.NotNull(preferences.RedLlmBaseUrl);
        Assert.NotNull(preferences.BlackLlmModel);
    }

    [Fact]
    public void OneMalformedSettingDoesNotDiscardOtherValidSettings()
    {
        using var file = new SettingsFile("""
            {"Threads":12,"HashMb":4096,"Level":"mistyped","AnimationDurationMs":null,
             "RedLlmModel":"chosen-model","RedLlmReasoning":"xhigh","LlmProfilesMigrated":true}
            """);
        var preferences = AppPreferences.Load();
        Assert.Equal(12, preferences.Threads);
        Assert.Equal(4096, preferences.HashMb);
        Assert.Equal(new AppPreferences().Level, preferences.Level);
        Assert.Equal(new AppPreferences().AnimationDurationMs, preferences.AnimationDurationMs);
        Assert.Equal("chosen-model", preferences.RedLlmModel);
        Assert.Equal("xhigh", preferences.RedLlmReasoning);
        Assert.Contains("mistyped", File.ReadAllText(file.Path)); // Loading never rewrites the original evidence.
    }

    [Fact]
    public void MissingMigrationFlagsDoNotReplacePerSideChoicesWithLegacyValues()
    {
        using var file = new SettingsFile("""
            {"LlmBaseUrl":"https://legacy.example","LlmModel":"legacy-model",
             "RedLlmBaseUrl":"https://red.example","RedLlmModel":"red-model",
             "BlackLlmBaseUrl":"https://black.example","BlackLlmModel":"black-model",
             "RedLlmReasoning":"low","BlackLlmReasoning":"high",
             "RedLlmServiceId":"chosen","LlmServices":[{"Id":"chosen","BaseUrl":"https://chosen.example"}]}
            """);
        var preferences = AppPreferences.Load();
        Assert.Equal("red-model", preferences.RedLlmModel);
        Assert.Equal("black-model", preferences.BlackLlmModel);
        Assert.Equal("https://red.example", preferences.RedLlmBaseUrl);
        Assert.Equal("chosen", preferences.RedLlmServiceId);
        Assert.Equal("low", preferences.RedLlmReasoning);
        Assert.Equal("high", preferences.BlackLlmReasoning);
    }

    [Fact]
    public void ModelListsRemoveNullsButKeepEnabledManualModelsAndSeparateServiceIdentities()
    {
        using var file = new SettingsFile("""
            {"LlmServiceDirectoryMigrated":true,"LlmProfilesMigrated":true,"LlmServices":[null,
              {"Id":"same","BaseUrl":"https://first.example","Models":[null,""," model-a ","model-a"],
               "EnabledModels":[null,"manual-model","manual-model"]},
              {"Id":"same","Name":null,"BaseUrl":"https://second.example","Models":null,"EnabledModels":null}]}
            """);
        var preferences = AppPreferences.Load();
        Assert.Equal(2, preferences.LlmServices.Count);
        var first = preferences.LlmServices[0];
        var second = preferences.LlmServices[1];
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(["model-a", "manual-model"], first.Models);
        Assert.Equal(["manual-model"], first.EnabledModels);
        Assert.Equal("https://second.example", second.BaseUrl);
        Assert.Empty(second.Models);
        Assert.Empty(second.EnabledModels);
    }

    [Fact]
    public void MissingOrDuplicateEngineIdsDoNotDeleteValidPluginConfigurations()
    {
        var bundled = EnginePlugin.Bundled();
        bundled.RuleOptions["Repetition Rule"] = "SkyRule";
        var preferences = new AppPreferences
        {
            EnginePluginsMigrated = true, DefaultEngineId = "same",
            EnginePlugins = [bundled,
                new() { Id = null!, ExecutablePath = "/fixture/first" },
                new() { Id = "", ExecutablePath = "/fixture/second" },
                new() { Id = "same", ExecutablePath = "/fixture/third" },
                new() { Id = "same", ExecutablePath = "/fixture/fourth" }]
        };
        preferences.NormalizeEnginePlugins();
        Assert.Equal(5, preferences.EnginePlugins.Count);
        Assert.Equal(5, preferences.EnginePlugins.Select(plugin => plugin.Id).Distinct().Count());
        Assert.Equal("/fixture/third", preferences.EnginePath);
        Assert.Equal("SkyRule", preferences.EnginePlugins[0].RuleOptions["Repetition Rule"]);
        preferences.NormalizeEnginePlugins();
        Assert.Equal(5, preferences.EnginePlugins.Count);
        Assert.Equal("/fixture/third", preferences.EnginePath);
    }

    [Fact]
    public void CaseVariantsAndNullCachedRuleOptionsCannotPreventSettingsLoading()
    {
        var plugin = new EnginePlugin
        {
            RuleOptions = new() { ["Repetition Rule"] = "AsianRule", ["repetition rule"] = "SkyRule", ["ignored"] = null! },
            DetectedRuleOptions = [null!, new(null!, "combo", "", null, null, []),
                new("Repetition Rule", "combo", null!, null, null, ["SkyRule", null!, "SkyRule"])]
        };
        using var file = new SettingsFile(JsonSerializer.Serialize(new AppPreferences
        { EnginePluginsMigrated = true, EnginePlugins = [plugin], DefaultEngineId = plugin.Id, HashMb = 8192 }));
        var preferences = AppPreferences.Load();
        var restored = preferences.EnginePlugins.Single(item => item.Id == plugin.Id);
        Assert.Equal(8192, preferences.HashMb);
        Assert.Equal("SkyRule", Assert.Single(restored.RuleOptions).Value);
        var option = Assert.Single(restored.DetectedRuleOptions);
        Assert.Equal("", option.DefaultValue);
        Assert.Equal(["SkyRule"], option.Variants);
    }

    [Fact]
    public void MalformedJsonCanOpenDefaultsWithoutRewritingTheDamagedFile()
    {
        using var file = new SettingsFile("{incomplete");
        var preferences = AppPreferences.Load();
        Assert.Equal(new AppPreferences().HashMb, preferences.HashMb);
        Assert.Equal("{incomplete", File.ReadAllText(file.Path));
    }

    [Fact]
    public async Task WriterRecoversAfterTransientIoFailureAndRejectsInvalidDelayBeforeStarting()
    {
        using var file = new SettingsFile("{}");
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreferencesWriter(file.Path, Timeout.InfiniteTimeSpan));
        var blocker = System.IO.Path.Combine(file.Folder, "blocked");
        await File.WriteAllTextAsync(blocker, "existing-file");
        var path = System.IO.Path.Combine(blocker, "settings.json");
        var writer = new PreferencesWriter(path, TimeSpan.Zero);
        writer.Schedule("{\"Level\":1}");
        await writer.FlushAsync();
        Assert.NotNull(writer.LastError);
        Assert.Equal("existing-file", await File.ReadAllTextAsync(blocker));
        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        Assert.Throws<ArgumentNullException>(() => writer.Schedule(null!));
        writer.Schedule("{\"Level\":20}");
        await writer.FlushAsync();
        Assert.Null(writer.LastError);
        Assert.Equal(1, writer.WriteCount);
        Assert.Equal("{\"Level\":20}", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(blocker));
    }

    [Fact]
    public void SynchronousSaveUsesAnAtomicReplacementAndCleansTemporaryFiles()
    {
        using var file = new SettingsFile("{\"HashMb\":1024}");
        new AppPreferences { HashMb = 8192, RedLlmReasoning = "high" }.Save();
        var restored = AppPreferences.Load();
        Assert.Equal(8192, restored.HashMb);
        Assert.Equal("high", restored.RedLlmReasoning);
        Assert.Single(Directory.GetFiles(file.Folder));
    }
}

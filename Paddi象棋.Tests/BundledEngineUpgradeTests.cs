using System.Runtime.InteropServices;
using PaddiXiangqi.Engine;
using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public class BundledEngineUpgradeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldBundledMetadataUpgradesWhileRulesAndDefaultSelectionRemain(bool bundledIsDefault)
    {
        var bundled = new EnginePlugin
        {
            Id = EnginePlugin.BundledId, Name = "内置皮卡鱼", DetectedName = "Pikafish 2026-09-06",
            RuleOptions = new() { ["Repetition Rule"] = "SkyRule", ["Sixty Move Rule"] = "false" },
            DetectedRuleOptions = []
        };
        var custom = new EnginePlugin
        {
            Name = "用户引擎", ExecutablePath = "/engines/custom", EvalFilePath = "/engines/custom.nnue",
            DetectedName = "User Engine", RuleOptions = new() { ["Repetition Rule"] = "ChineseRule" }
        };
        var selectedId = bundledIsDefault ? bundled.Id : custom.Id;
        var preferences = new AppPreferences
        {
            EnginePluginsMigrated = true, EnginePlugins = [bundled, custom], DefaultEngineId = selectedId
        };

        preferences.NormalizeEnginePlugins();
        preferences.NormalizeEnginePlugins();

        Assert.Equal("Pikafish 2026-09-25", bundled.DetectedName);
        Assert.Equal(5, bundled.DetectedRuleOptions.Count);
        Assert.Equal("AsianRule", bundled.DetectedRuleOptions.Single(option => option.Name == "Repetition Rule").DefaultValue);
        Assert.Equal("SkyRule", bundled.RuleOptions["Repetition Rule"]);
        Assert.Equal("false", bundled.RuleOptions["Sixty Move Rule"]);
        Assert.Equal(2, bundled.RuleOptions.Count);
        Assert.Equal(selectedId, preferences.DefaultEngineId);
        Assert.Equal(bundledIsDefault ? "" : custom.ExecutablePath, preferences.EnginePath);
        Assert.Equal("/engines/custom.nnue", custom.EvalFilePath);
        Assert.Equal("User Engine", custom.DetectedName);
        Assert.Equal("ChineseRule", custom.RuleOptions["Repetition Rule"]);
        Assert.Equal(2, preferences.EnginePlugins.Count);
    }

    [Fact]
    public async Task BundledRuleMetadataMatchesShippedBinaryAndMatchingWeightsLoad()
    {
        if (!OperatingSystem.IsMacOS() &&
            !((OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
              RuntimeInformation.ProcessArchitecture == Architecture.X64)) return;

        var bundled = EnginePlugin.Bundled();
        Assert.True(File.Exists(PikafishClient.BundledEnginePath()));
        await using var client = bundled.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var identity = await client.ProbeAsync(timeout.Token);

        Assert.Equal(bundled.DetectedName, identity.Name);
        Assert.Equal(bundled.Author, identity.Author);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Engine", "pikafish.nnue"), identity.EvalFile);
        var actualRules = identity.Options.Where(option => option.IsRuleOption).ToArray();
        Assert.Equal(bundled.DetectedRuleOptions.Count, actualRules.Length);
        foreach (var expected in bundled.DetectedRuleOptions)
        {
            var actual = Assert.Single(actualRules, option => option.Name == expected.Name);
            Assert.Equal((expected.Type, expected.DefaultValue, expected.Minimum, expected.Maximum),
                (actual.Type, actual.DefaultValue, actual.Minimum, actual.Maximum));
            Assert.Equal(expected.Variants.ToArray(), actual.Variants.ToArray());
        }
    }
}

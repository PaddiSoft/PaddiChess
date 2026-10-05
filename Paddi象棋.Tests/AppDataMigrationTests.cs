using PaddiXiangqi.Services;

namespace PaddiXiangqi.Tests;

public sealed class AppDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "paddi-migration-" + Guid.NewGuid().ToString("N"));
    private string Legacy => Path.Combine(_root, "PikaDesk");
    private AppDataPaths Paths => new(_root);

    [Fact]
    public void CopiesAllProfileDataAndRetainsOriginals()
    {
        Write(Legacy, "settings.json", "{\"Level\":17}");
        Write(Legacy, "BoardSkins/custom.json", "skin");
        Write(Legacy, "Recovery/2026/record.json", "record");
        Write(Legacy, "user-notes.txt", "notes");

        var paths = Paths;
        Assert.Equal(Path.Combine(_root, "Paddi象棋", "settings.json"), paths.PrepareSettingsLoad());
        foreach (var file in Directory.GetFiles(Legacy, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(File.ReadAllText(file),
                File.ReadAllText(Path.Combine(paths.DataDirectory, Path.GetRelativePath(Legacy, file))));
            Assert.True(File.Exists(file));
        }
    }

    [Fact]
    public void ExistingNewFilesWinAndRepeatedMigrationDoesNotOverwriteThem()
    {
        Write(Legacy, "settings.json", "old settings");
        Write(Legacy, "BoardSkins/one.json", "old skin");
        Write(Legacy, "BoardSkins/two.json", "missing skin");
        Write(Paths.DataDirectory, "settings.json", "new settings");
        Write(Paths.DataDirectory, "BoardSkins/one.json", "new skin");

        Paths.PrepareSettingsLoad();
        Write(Legacy, "BoardSkins/two.json", "old source later changed");
        Paths.PrepareSettingsLoad();

        Assert.Equal("new settings", File.ReadAllText(Paths.SettingsFilePath));
        Assert.Equal("new skin", File.ReadAllText(Path.Combine(Paths.DataDirectory, "BoardSkins/one.json")));
        Assert.Equal("missing skin", File.ReadAllText(Path.Combine(Paths.DataDirectory, "BoardSkins/two.json")));
        Assert.Empty(Directory.GetFiles(Paths.DataDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void FailedSubdirectoryCopyCanRetryWithoutBlockingOtherFiles()
    {
        Write(Legacy, "settings.json", "settings");
        Write(Legacy, "BoardSkins/skin.json", "skin");
        Write(Paths.DataDirectory, "BoardSkins", "temporary obstruction");
        Paths.PrepareSettingsLoad();
        Assert.Equal("settings", File.ReadAllText(Paths.SettingsFilePath));
        Assert.Equal("skin", File.ReadAllText(Path.Combine(Legacy, "BoardSkins/skin.json")));

        File.Delete(Path.Combine(Paths.DataDirectory, "BoardSkins"));
        Paths.PrepareSettingsLoad();
        Assert.Equal("skin", File.ReadAllText(Path.Combine(Paths.DataDirectory, "BoardSkins/skin.json")));
    }

    [Fact]
    public void UnwritableDestinationKeepsLegacySettingsReadableUntilRetry()
    {
        Write(Legacy, "settings.json", "retained settings");
        Write(_root, "Paddi象棋", "temporary obstruction");
        Assert.Equal(Path.Combine(Legacy, "settings.json"), Paths.PrepareSettingsLoad());
        File.Delete(Paths.DataDirectory);
        Assert.Equal(Paths.SettingsFilePath, Paths.PrepareSettingsLoad());
        Assert.Equal("retained settings", File.ReadAllText(Paths.SettingsFilePath));
    }

    [Fact]
    public void ExplicitSettingsOverrideStaysIsolatedAndDoesNotMigrate()
    {
        Write(Legacy, "settings.json", "legacy settings");
        Write(Legacy, "BoardSkins/skin.json", "skin");
        var custom = Path.Combine(_root, "isolated", "special.json");
        var paths = new AppDataPaths(_root, custom);

        Assert.Equal(custom, paths.PrepareSettingsLoad());
        Assert.Equal(Path.GetDirectoryName(custom), paths.DataDirectory);
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.False(Directory.Exists(Paths.DataDirectory));
    }

    private static void Write(string directory, string relative, string text)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

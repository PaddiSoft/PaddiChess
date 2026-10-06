namespace PaddiXiangqi.Services;

/// <summary>Product data locations and the non-destructive upgrade from the former profile directory.</summary>
public sealed class AppDataPaths
{
    // This is a compatibility path. Keep the former name when renaming the project.
    private const string LegacyDirectoryName = "PikaDesk";

    public string SettingsFilePath { get; }
    public string DataDirectory => Path.GetDirectoryName(SettingsFilePath)!;
    private string? LegacyDirectory { get; }

    public static AppDataPaths Current => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("PADDI_SETTINGS_PATH"));

    public AppDataPaths(string localApplicationData, string? customSettingsPath = null)
    {
        if (!string.IsNullOrEmpty(customSettingsPath))
        {
            SettingsFilePath = Path.GetFullPath(customSettingsPath);
            return;
        }
        SettingsFilePath = Path.Combine(localApplicationData, "Paddi象棋", "settings.json");
        LegacyDirectory = Path.Combine(localApplicationData, LegacyDirectoryName);
    }

    /// <summary>Run at preferences load, never in the capture or rendering loop.</summary>
    public string PrepareSettingsLoad()
    {
        if (LegacyDirectory is null) return SettingsFilePath;
        CopyMissingDirectory(LegacyDirectory, DataDirectory);
        // A temporarily locked destination must not reset the user's settings.
        var legacySettings = Path.Combine(LegacyDirectory, "settings.json");
        return !File.Exists(SettingsFilePath) && File.Exists(legacySettings)
            ? legacySettings : SettingsFilePath;
    }

    private static void CopyMissingDirectory(string source, string destination)
    {
        string[] entries;
        try
        {
            if (!Directory.Exists(source) || IsLink(source) || IsLink(destination)) return;
            Directory.CreateDirectory(destination);
            entries = Directory.GetFileSystemEntries(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }

        foreach (var entry in entries)
        {
            string? temporary = null;
            try
            {
                if (IsLink(entry)) continue;
                var target = Path.Combine(destination, Path.GetFileName(entry));
                if (Directory.Exists(entry))
                {
                    CopyMissingDirectory(entry, target);
                    continue;
                }
                if (Path.Exists(target)) continue;
                // Commit complete files only. A failed copy can be retried at the next load.
                temporary = target + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(entry, temporary, overwrite: false);
                File.Move(temporary, target, overwrite: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            finally
            {
                try { if (temporary is not null) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool IsLink(string path) => Path.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

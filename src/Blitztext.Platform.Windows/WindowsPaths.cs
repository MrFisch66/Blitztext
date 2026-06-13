using System.IO;

namespace Blitztext.Platform.Windows;

public static class WindowsPaths
{
    public static string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blitztext");

    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    public static string CacheDirectory => Path.Combine(AppDataDirectory, "cache");

    public static string ToolsDirectory => Path.Combine(AppDataDirectory, "tools");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ToolsDirectory);
    }
}

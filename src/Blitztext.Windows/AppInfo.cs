using System.Reflection;

namespace Blitztext.Windows;

/// <summary>Exposes the application version (from <c>Directory.Build.props</c>) for display.</summary>
internal static class AppInfo
{
    public static string Version { get; } = ResolveVersion();

    public static string DisplayVersion => $"v{Version}";

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip any "+<commit>" build metadata that tooling may append.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}

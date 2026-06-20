using System.IO;
using System.Text;

namespace Blitztext.Platform.Windows;

/// <summary>
/// Minimal, dependency-free error log writing to <see cref="WindowsPaths.LogPath"/>. It only ever
/// records genuine faults — there is no informational/debug chatter, so in normal operation no log
/// file is produced. Logging is strictly best-effort: a failure here must never surface or become a
/// new crash source, so every path swallows its own exceptions.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    /// <summary>The log is rotated once it grows past this size, keeping a single ".1" backup.</summary>
    private const long MaxBytes = 1024 * 1024;

    public static void Error(string category, Exception? exception, string? message = null)
        => Write(category, message, exception);

    private static void Write(string category, string? message, Exception? exception)
    {
        try
        {
            var path = WindowsPaths.LogPath;
            lock (Gate)
            {
                Directory.CreateDirectory(WindowsPaths.LogsDirectory);
                RotateIfNeeded(path);

                var builder = new StringBuilder();
                builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [ERROR] ")
                    .Append(category);
                if (!string.IsNullOrEmpty(message))
                {
                    builder.Append(" — ").Append(message);
                }

                builder.AppendLine();
                if (exception is not null)
                {
                    builder.AppendLine(exception.ToString());
                }

                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                var archive = path + ".1";
                File.Delete(archive); // no-op if the backup does not exist
                File.Move(path, archive);
            }
        }
        catch
        {
            // If rotation fails we simply keep appending to the existing file.
        }
    }
}

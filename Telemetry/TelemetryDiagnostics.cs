using System;
using System.IO;

namespace MultiEnchantmentMod.Telemetry;

internal static class TelemetryDiagnostics
{
    internal static string LogPath => GetLogPath();

    internal static void Append(string msg)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* diagnostics must never affect the game */ }
    }

    private static string GetLogPath()
    {
        // Try multiple paths: Assembly.Location → user AppData → temp folder.
        try
        {
            string? loc = typeof(TelemetryDiagnostics).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
            {
                string? dir = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    return Path.Combine(dir, "telemetry_diag.log");
                }
            }
        }
        catch { }

        try
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiEnchantmentMod");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "telemetry_diag.log");
        }
        catch { }

        return Path.Combine(Path.GetTempPath(), "telemetry_diag.log");
    }
}

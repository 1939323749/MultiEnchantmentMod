namespace MultiEnchantmentMod.Telemetry;

internal static class TelemetryDiagnostics
{
    internal static string LogPath => string.Empty;

    internal static void Append(string msg)
    {
        // Telemetry diagnostics are intentionally disabled in release builds.
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Debug;

namespace MultiEnchantmentMod.Telemetry;

internal static partial class TelemetryConfig
{
    // SupabaseUrl and AnonKey are generated at build time into TelemetrySecrets.g.cs
    // from .env.props (local) or -p: MSBuild properties (CI). See .env.props.template.

    internal static bool IsEnabled { get; private set; }
    internal static string ModVersion { get; private set; } = "unknown";
    internal static string GameVersion { get; private set; } = "unknown";
    internal static string InstallationId { get; private set; } = "unknown";

    internal static void Initialize()
    {
        if (SupabaseUrl.Contains("REPLACE_ME"))
        {
            IsEnabled = false;
            return;
        }

        try
        {
            ReadManifest();
            if (!IsEnabled)
            {
                return;
            }

            ReadGameVersion();
            ReadInstallationId();
        }
        catch
        {
            IsEnabled = false;
        }
    }

    /// <summary>
    /// Reads mod version and telemetry switch from the root <c>MultiEnchantmentMod.json</c> manifest.
    /// The manifest is the single source of truth for mod metadata — no separate telemetry config file.
    /// Telemetry defaults to <c>true</c> when the field is absent.
    /// </summary>
    private static void ReadManifest()
    {
        string manifestPath = GetManifestPath();
        if (!File.Exists(manifestPath))
        {
            IsEnabled = false;
            return;
        }

        string json = File.ReadAllText(manifestPath);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("version", out JsonElement versionEl))
        {
            ModVersion = versionEl.GetString() ?? "unknown";
        }

        // "telemetry": false in the manifest disables telemetry. Default is true.
        if (root.TryGetProperty("telemetry", out JsonElement telemetryEl) &&
            telemetryEl.ValueKind == JsonValueKind.False)
        {
            IsEnabled = false;
        }
        else
        {
            IsEnabled = true;
        }
    }

    private static void ReadGameVersion()
    {
        try
        {
            Assembly? gameAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(static a => string.Equals(a.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase));

            if (TryReadReleaseInfoManagerVersion(out string managerVersion))
            {
                GameVersion = managerVersion;
                return;
            }

            if (TryReadReleaseInfoVersion(gameAsm, out string releaseVersion))
            {
                GameVersion = releaseVersion;
                return;
            }

            if (gameAsm == null) return;

            // Prefer InformationalVersion (often contains the semantic version string).
            string? infoVersion = gameAsm
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            infoVersion = NormalizeGameVersion(infoVersion);
            if (!string.IsNullOrEmpty(infoVersion))
            {
                GameVersion = infoVersion;
                return;
            }

            // Fall back to FileVersion.
            string? fileVersion = gameAsm
                .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            fileVersion = NormalizeGameVersion(fileVersion);
            if (!string.IsNullOrEmpty(fileVersion))
            {
                GameVersion = fileVersion;
                return;
            }

            // Last resort: assembly version.
            Version? asmVersion = gameAsm.GetName().Version;
            if (asmVersion != null)
            {
                GameVersion = asmVersion.ToString();
            }
        }
        catch { /* best-effort */ }
    }

    private static bool TryReadReleaseInfoManagerVersion(out string version)
    {
        version = string.Empty;

        try
        {
            string? normalized = NormalizeGameVersion(ReleaseInfoManager.Instance.ReleaseInfo?.Version);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            version = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadReleaseInfoVersion(Assembly? gameAsm, out string version)
    {
        version = string.Empty;

        foreach (string path in GetReleaseInfoCandidates(gameAsm).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;
                string? rawVersion = null;
                if (root.TryGetProperty("version", out JsonElement versionEl))
                {
                    rawVersion = versionEl.GetString();
                }
                else if (root.TryGetProperty("branch", out JsonElement branchEl))
                {
                    rawVersion = branchEl.GetString();
                }

                string? normalized = NormalizeGameVersion(rawVersion);
                if (!string.IsNullOrEmpty(normalized))
                {
                    version = normalized;
                    return true;
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return false;
    }

    private static IEnumerable<string> GetReleaseInfoCandidates(Assembly? gameAsm)
    {
        string? gameDir = GetAssemblyDirectory(gameAsm);
        foreach (string candidate in GetAncestorReleaseInfoCandidates(gameDir, 6))
        {
            yield return candidate;
        }

        string? modDir = Path.GetDirectoryName(typeof(TelemetryConfig).Assembly.Location);
        foreach (string candidate in GetAncestorReleaseInfoCandidates(modDir, 4))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> GetAncestorReleaseInfoCandidates(string? startDir, int maxDepth)
    {
        string? current = startDir;
        for (int depth = 0; depth <= maxDepth && !string.IsNullOrEmpty(current); depth++)
        {
            yield return Path.Combine(current, "release_info.json");
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static string? GetAssemblyDirectory(Assembly? assembly)
    {
        try
        {
            string? location = assembly?.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeGameVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion)) return null;

        string version = rawVersion.Trim();
        const string tagPrefix = "refs/tags/";
        if (version.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            version = version[tagPrefix.Length..];
        }

        if (version.Length > 1 &&
            version[0] is 'v' or 'V' &&
            char.IsDigit(version[1]))
        {
            version = version[1..];
        }

        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private static string GetManifestPath()
    {
        // The manifest sits next to the mod's DLL in the mods folder, or in the project root
        // during development. Use the DLL's directory as the anchor.
        string? dir = Path.GetDirectoryName(typeof(TelemetryConfig).Assembly.Location);
        return Path.Combine(dir ?? ".", "MultiEnchantmentMod.json");
    }

    private static void ReadInstallationId()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiEnchantmentMod");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "telemetry_installation_id.txt");
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out Guid parsed))
                {
                    InstallationId = parsed.ToString("D");
                    return;
                }
            }

            InstallationId = Guid.NewGuid().ToString("D");
            File.WriteAllText(path, InstallationId);
        }
        catch
        {
            InstallationId = "unknown";
        }
    }
}

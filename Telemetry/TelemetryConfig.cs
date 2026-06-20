using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Debug;

namespace MultiEnchantmentMod.Telemetry;

internal static partial class TelemetryConfig
{
    // PostHogHost and PostHogProjectKey are generated at build time into
    // TelemetrySecrets.g.cs from .env.props (local) or -p: MSBuild properties (CI).
    // See .env.props.template.

    /// <summary>
    /// Master telemetry switch: requires the manifest opt-in AND the game's own
    /// "Upload Data" privacy preference. The vanilla mod metrics hook
    /// (ModManager.CallMetricsHooks) is gated on that preference; our Harmony
    /// patches bypass that path, so it must be checked explicitly here.
    /// </summary>
    internal static bool IsEnabled => _manifestEnabled && GameUploadPreferenceAllows();

    private static bool _manifestEnabled;

    internal static string ModVersion { get; private set; } = "unknown";
    internal static string GameVersion { get; private set; } = "unknown";
    internal static string InstallationId { get; private set; } = "unknown";

    /// <summary>card_reward carries no enchantment signal; disabled to cut volume.</summary>
    internal static readonly bool CardRewardEnabled = false;

    /// <summary>
    /// Percent of installations whose non-crash streams are uploaded. Deterministic
    /// per-installation bucketing keeps a stable cohort across sessions so run/session
    /// data stays internally consistent. Crashes (mod_crash) are never sampled.
    /// </summary>
    internal const int SampleCohortPercent = 100; // TEMP: smoke test (revert to 20 before release)

    /// <summary>
    /// True when this installation is in the sampled-in cohort. Computed once in
    /// <see cref="Initialize"/>. Installations with an unknown id are excluded.
    /// </summary>
    internal static bool IsSampledIn { get; private set; }

    internal static void Initialize()
    {
        if (PostHogProjectKey.Contains("REPLACE_ME"))
        {
            _manifestEnabled = false;
            return;
        }

        try
        {
            ReadManifest();
            if (!_manifestEnabled)
            {
                return;
            }

            ReadGameVersion();
            ReadInstallationId();
            ComputeSampleCohort();
        }
        catch
        {
            _manifestEnabled = false;
        }
    }

    private static bool GameUploadPreferenceAllows()
    {
        try
        {
            return MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.PrefsSave?.UploadData ?? true;
        }
        catch
        {
            // Save system not ready yet (early startup) — don't block telemetry
            // that is gated again at actual send time.
            return true;
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
            _manifestEnabled = false;
            return;
        }

        string json = File.ReadAllText(manifestPath, Encoding.UTF8);
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
            _manifestEnabled = false;
        }
        else
        {
            _manifestEnabled = true;
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

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
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
            string? path = TryGetDataFilePath("telemetry_installation_id.txt");
            if (path == null)
            {
                InstallationId = "unknown";
                return;
            }

            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (Guid.TryParse(existing, out Guid parsed))
                {
                    InstallationId = parsed.ToString("D");
                    return;
                }
            }

            InstallationId = Guid.NewGuid().ToString("D");
            File.WriteAllText(path, InstallationId, Encoding.UTF8);
        }
        catch
        {
            InstallationId = "unknown";
        }
    }

    /// <summary>
    /// Deterministically buckets this installation into [0,100) from a SHA256 of its
    /// id and marks it sampled-in when the bucket is below <see cref="SampleCohortPercent"/>.
    /// Stable across launches (the id is persisted), so a given install is always in or
    /// always out. Unknown ids are treated as sampled-out.
    /// </summary>
    private static void ComputeSampleCohort()
    {
        IsSampledIn = false;
        try
        {
            if (string.Equals(InstallationId, "unknown", StringComparison.Ordinal))
            {
                return;
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(InstallationId));
            ulong bucket = BitConverter.ToUInt64(hash, 0) % 100UL;
            IsSampledIn = bucket < (ulong)SampleCohortPercent;
        }
        catch
        {
            IsSampledIn = false;
        }
    }

    /// <summary>
    /// Resolves a writable path for a per-installation data file under
    /// <c>%LocalAppData%/MultiEnchantmentMod/</c>, creating the directory if needed.
    /// Returns <c>null</c> when the location can't be resolved or created so callers
    /// can degrade gracefully instead of throwing.
    /// </summary>
    internal static string? TryGetDataFilePath(string fileName)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiEnchantmentMod");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
        catch
        {
            return null;
        }
    }
}

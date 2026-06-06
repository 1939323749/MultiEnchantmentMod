using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiEnchantmentMod.Telemetry;

internal static class CrashReporter
{
    private static readonly HashSet<string> RelevantNamespaces = new()
    {
        "MultiEnchantmentMod",
        "MultiEnchantmentSupport",
        "MultiEnchantmentPatches",
        "MultiEnchantmentStackSupport",
        "MultiEnchantmentScopeSupport",
    };

    private static string? _sessionId;
    private static string? _lastHookName;
    private static bool _installed;

    internal static void Install(string sessionId)
    {
        if (_installed) return;
        _installed = true;
        _sessionId = sessionId;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    internal static void SetLastHook(string hookName) => _lastHookName = hookName;

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ReportIfRelevant(ex, ensureSessionRow: true);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (e.Exception is { } aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                ReportIfRelevant(inner, ensureSessionRow: false);
            }

            e.SetObserved();
        }
    }

    private static void ReportIfRelevant(Exception ex, bool ensureSessionRow)
    {
        if (!TelemetryConfig.IsEnabled) return;

        try
        {
            string trace = ex.StackTrace ?? "";
            bool isOurFault = IsOurCodeAtFault(ex, trace);

            // Only report crashes that involve our code or registered third-party enchantment mods.
            if (!isOurFault && !IsOurCodeInvolved(ex, trace) && !IsRegisteredThirdPartyCodeInvolved(trace))
            {
                return;
            }

            string[] activeTypes;
            try
            {
                activeTypes = Api.Internal.EnchantmentRegistry.GetAllRegisteredTypes()
                    .Select(static t => t.FullName ?? t.Name)
                    .ToArray();
            }
            catch { activeTypes = Array.Empty<string>(); }

            string[] loadedAssemblies;
            try
            {
                loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(static a => a.GetName().Name ?? "")
                    .Where(static n => !string.IsNullOrEmpty(n) &&
                                       !n.StartsWith("System", StringComparison.Ordinal) &&
                                       !n.StartsWith("Microsoft", StringComparison.Ordinal))
                    .ToArray();
            }
            catch { loadedAssemblies = Array.Empty<string>(); }

            if (ensureSessionRow && !TelemetryCollector.SessionDataQueued)
            {
                string catalogHash = TelemetryCollector.MinimalCrashCatalogHash;
                string environmentHash = TelemetryCollector.MinimalCrashEnvironmentHash;
                TelemetryReporter.SendStartupData(
                    TelemetryCollector.BuildMinimalCrashEnvironmentData(environmentHash),
                    new
                    {
                        id = _sessionId,
                        installation_id = TelemetryConfig.InstallationId,
                        mod_version = TelemetryConfig.ModVersion,
                        game_version = TelemetryConfig.GameVersion,
                        api_version = Api.MultiEnchantmentApiVersion.Current,
                        os_platform = GetOsPlatform(),
                        catalog_hash = catalogHash,
                        environment_hash = environmentHash,
                    },
                    TelemetryCollector.BuildMinimalCrashCatalogData(catalogHash),
                    null,
                    null,
                    null);
            }

            TelemetryReporter.SendCrash(new
            {
                session_id = _sessionId,
                exception_type = ex.GetType().FullName ?? ex.GetType().Name,
                exception_message = Truncate(ex.Message, 500),
                stack_trace = Truncate(trace, 2000),
                is_our_fault = isOurFault,
                last_hook_name = _lastHookName,
                active_enchantment_types = activeTypes,
                loaded_mod_assemblies = loadedAssemblies,
            });

            TelemetryReporter.Flush(TimeSpan.FromSeconds(2));
        }
        catch { /* crash reporter must never itself crash */ }
    }

    private static bool IsOurCodeInvolved(Exception ex, string trace)
    {
        Type exceptionType = ex.GetType();
        if (IsRelevantType(exceptionType))
        {
            return true;
        }

        return RelevantNamespaces.Any(ns =>
            trace.Contains($" at {ns}.", StringComparison.Ordinal) ||
            trace.Contains($" in {ns}.", StringComparison.Ordinal));
    }

    private static bool IsOurCodeAtFault(Exception ex, string trace)
    {
        Type exceptionType = ex.GetType();
        if (IsRelevantType(exceptionType))
        {
            return true;
        }

        foreach (string frame in GetStackFrames(trace).Take(8))
        {
            if (IsExternalDescriptionFormattingFrame(frame))
            {
                return false;
            }

            if (RelevantNamespaces.Any(ns => frame.Contains($" at {ns}.", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetStackFrames(string trace) =>
        trace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim());

    private static bool IsExternalDescriptionFormattingFrame(string frame) =>
        frame.Contains("SmartFormat", StringComparison.Ordinal) ||
        frame.Contains("LocManager.SmartFormat", StringComparison.Ordinal) ||
        frame.Contains("CardModel.GetDescriptionForPile", StringComparison.Ordinal) ||
        frame.Contains("CardModel.GetDescriptionForUpgradePreview", StringComparison.Ordinal) ||
        frame.Contains("PowerModel.GetDumbHoverTip", StringComparison.Ordinal);

    private static bool IsRegisteredThirdPartyCodeInvolved(string trace)
    {
        try
        {
            foreach (Type type in Api.Internal.EnchantmentRegistry.GetAllRegisteredTypes())
            {
                if (IsRelevantType(type))
                {
                    continue;
                }

                if (StackTraceContainsType(trace, type))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool IsRelevantType(Type type)
    {
        string? fullName = type.FullName;
        return fullName != null && RelevantNamespaces.Any(ns =>
            fullName.Equals(ns, StringComparison.Ordinal) ||
            fullName.StartsWith(ns + ".", StringComparison.Ordinal));
    }

    private static bool StackTraceContainsType(string trace, Type type)
    {
        string? fullName = type.FullName;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return false;
        }

        string? assemblyName = type.Assembly.GetName().Name;
        return trace.Contains($" at {fullName}.", StringComparison.Ordinal) ||
               (!string.IsNullOrWhiteSpace(assemblyName) &&
                trace.Contains($" in {assemblyName}", StringComparison.Ordinal));
    }

    private static string GetOsPlatform()
    {
        try
        {
            return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows) ? "Windows"
                : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Linux) ? "Linux"
                : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX) ? "macOS" : "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string Truncate(string? s, int max) =>
        s == null ? "" : s.Length <= max ? s : s[..max];
}

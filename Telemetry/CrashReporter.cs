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

    private static readonly string[] BridgeFrameMarkers =
    {
        "PostfixAsync(",
        "PrefixAsync(",
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

            // Only report crashes that originate in our code. Async postfix bridge frames are
            // ignored for attribution: if the original game/other-mod task faults, our await
            // continuation naturally appears below it in the stack trace, but that does not make
            // it our crash. Third-party enchantment handler failures are tracked through
            // SafeInvoker/combat telemetry instead of this global crash table.
            if (!isOurFault)
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

            string catalogHash = TelemetryCollector.MinimalCrashCatalogHash;
            string environmentHash = TelemetryCollector.MinimalCrashEnvironmentHash;
            string osPlatform = GetOsPlatform();

            if (ensureSessionRow && !TelemetryCollector.SessionDataQueued)
            {
                TelemetryReporter.SendStartupData(
                    TelemetryCollector.BuildMinimalCrashEnvironmentData(environmentHash),
                    new
                    {
                        id = _sessionId,
                        installation_id = TelemetryConfig.InstallationId,
                        mod_version = TelemetryConfig.ModVersion,
                        game_version = TelemetryConfig.GameVersion,
                        api_version = Api.MultiEnchantmentApiVersion.Current,
                        os_platform = osPlatform,
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
                installation_id = TelemetryConfig.InstallationId,
                mod_version = TelemetryConfig.ModVersion,
                game_version = TelemetryConfig.GameVersion,
                api_version = Api.MultiEnchantmentApiVersion.Current,
                os_platform = osPlatform,
                catalog_hash = catalogHash,
                environment_hash = environmentHash,
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

    private static bool IsOurCodeAtFault(Exception ex, string trace)
    {
        Type exceptionType = ex.GetType();
        if (IsRelevantType(exceptionType))
        {
            return true;
        }

        foreach (string frame in GetStackFrames(trace).Take(16))
        {
            if (IsIgnorableRuntimeFrame(frame))
            {
                continue;
            }

            return IsRelevantFrame(frame) && !IsBridgeFrame(frame);
        }

        return false;
    }

    private static bool IsRelevantFrame(string frame) =>
        RelevantNamespaces.Any(ns => frame.Contains($" at {ns}.", StringComparison.Ordinal));

    private static bool IsBridgeFrame(string frame)
    {
        if (!BridgeFrameMarkers.Any(marker => frame.Contains(marker, StringComparison.Ordinal)))
        {
            return false;
        }

        return frame.Contains("MultiEnchantmentPatches.", StringComparison.Ordinal) ||
               frame.Contains("MultiEnchantmentTransformPatches.", StringComparison.Ordinal) ||
               frame.Contains("MultiEnchantmentStackPatches.", StringComparison.Ordinal);
    }

    private static bool IsIgnorableRuntimeFrame(string frame) =>
        frame.StartsWith("at System.", StringComparison.Ordinal) ||
        frame.StartsWith("at Microsoft.", StringComparison.Ordinal) ||
        frame.StartsWith("--- End of stack trace", StringComparison.Ordinal);

    private static IEnumerable<string> GetStackFrames(string trace) =>
        trace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim());

    private static bool IsRelevantType(Type type)
    {
        string? fullName = type.FullName;
        return fullName != null && RelevantNamespaces.Any(ns =>
            fullName.Equals(ns, StringComparison.Ordinal) ||
            fullName.StartsWith(ns + ".", StringComparison.Ordinal));
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

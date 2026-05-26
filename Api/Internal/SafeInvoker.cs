using System;
using System.Collections.Concurrent;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Centralised wrapper around downstream-author delegates (lifecycle callbacks, predicates,
/// dynamic-var contributions, keyword amount functions, etc.) so a buggy enchantment cannot
/// crash an unrelated Harmony patch path.
///
/// All public helpers swallow the exception, log it with the offending enchantment type and the
/// hook name so the author can locate the failure, and return a caller-supplied fallback.
///
/// Repeated failures from the same (type, hook) key are throttled: full <c>{ex}</c> goes to the
/// log the first <see cref="DetailedFailuresPerKey"/> times, after which only the message is
/// retained until <see cref="ResetThrottle"/> is invoked (currently on combat-start /
/// combat-end via <c>MultiEnchantmentScopeSupport</c>). This keeps logs readable when a
/// per-frame hook fails repeatedly.
/// </summary>
internal static class SafeInvoker
{
    private const int DetailedFailuresPerKey = 3;
    private const int SilencedAfterFailures = 50;

    private static readonly ConcurrentDictionary<(Type Type, string Hook), int> FailureCounts = new();

    internal static void Run(Type enchantmentType, string hookName, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            LogFailure(enchantmentType, hookName, ex);
        }
    }

    internal static T Run<T>(Type enchantmentType, string hookName, Func<T> body, T fallback)
    {
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            LogFailure(enchantmentType, hookName, ex);
            return fallback;
        }
    }

    /// <summary>
    /// Clears the per-(type, hook) failure counter. Called by combat lifecycle so a flaky
    /// callback that fired 50 times last combat can produce a fresh detailed log next combat.
    /// </summary>
    internal static void ResetThrottle()
    {
        FailureCounts.Clear();
    }

    internal static void LogFailure(Type enchantmentType, string hookName, Exception ex)
    {
        (Type Type, string Hook) key = (enchantmentType, hookName);
        int count = FailureCounts.AddOrUpdate(key, 1, static (_, prior) => prior + 1);

        string typeName = enchantmentType.FullName ?? enchantmentType.Name;
        string assemblyName = enchantmentType.Assembly.GetName().Name ?? "<unknown>";

        if (count <= DetailedFailuresPerKey)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] {typeName} (assembly={assemblyName}) threw in {hookName}: {ex}");
        }
        else if (count <= SilencedAfterFailures)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] {typeName} (assembly={assemblyName}) threw in {hookName} again (#{count}): {ex.GetBaseException().Message}");
        }
        else if (count == SilencedAfterFailures + 1)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] {typeName} (assembly={assemblyName}) keeps throwing in {hookName}; suppressing further messages until combat ends.");
        }
    }
}

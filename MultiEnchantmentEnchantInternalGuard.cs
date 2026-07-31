using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod;

/// <summary>
/// Watches <c>CardModel.EnchantInternal</c> — the low-level attach that assigns the vanilla primary
/// enchantment slot.
///
/// <para>Unlike its sibling <c>AfflictInternal</c> (which throws when the slot is taken), vanilla's
/// <c>EnchantInternal</c> assigns <c>Enchantment</c> unconditionally, so calling it on a card that
/// already carries an enchantment silently destroys the previous one — and, with this mod, whatever
/// state hung off it. Every vanilla call site reaches it with a free primary slot (a freshly built
/// card during save restore, a fresh clone in <c>Claws</c> / <c>NEnchantPreview</c>), and this mod's
/// own calls are guarded by an explicit <c>card.Enchantment == null</c> check, so a call arriving
/// with the slot occupied means some third party is replaying an attach on a live card.</para>
///
/// <para>This is deliberately an <b>entry-point</b> guard rather than another IL rewrite: the third
/// party is still calling the vanilla method, so intercepting the method itself covers every call
/// site at once — regardless of where it lives, whether it got inlined, or what the caller's IL
/// looks like. IL rewriting is only the right tool when a mod has copied vanilla logic into its own
/// method body, where there is no entry point left to hook.</para>
/// </summary>
/// <remarks>
/// <para><b>Why the repair is off by default.</b> Detection always runs, but re-routing the newcomer
/// into an additional slot ships disabled (<c>"guardEnchantInternal": true</c> in
/// <c>MultiEnchantmentMod.json</c> turns it on). Both available behaviors are lossy in different
/// ways and there is currently no field evidence about which third parties hit this path:</para>
/// <list type="bullet">
///   <item>Letting vanilla proceed destroys the existing enchantment, but its <c>ModifyCard</c>
///   effects were already applied to the card and are never reverted.</item>
///   <item>Re-routing preserves both, but a caller following vanilla's
///   <c>EnchantInternal(); card.Enchantment.ModifyCard();</c> idiom would then re-run
///   <c>ModifyCard</c> on the pre-existing primary — double-applying its effects.</item>
/// </list>
/// <para>So the default keeps today's behavior (which the save-restore re-assert in
/// <c>MultiEnchantmentSupport.Serialization</c> already compensates for) and logs loudly instead.
/// Flip the flag once the log or telemetry shows which mods actually land here.</para>
/// </remarks>
[HarmonyPatch]
internal static class MultiEnchantmentEnchantInternalGuard
{
    /// <summary>
    /// Set while this mod is itself inside a mutation that ends up calling <c>EnchantInternal</c>.
    /// The occupied-slot test below already excludes our own calls (we only call it when the primary
    /// is free), but this keeps the guard correct if that ordering ever changes.
    /// </summary>
    [ThreadStatic]
    private static bool _inOwnMutation;

    /// <summary>Callers already reported, so a per-frame offender cannot flood the log.</summary>
    private static readonly ConcurrentDictionary<string, byte> ReportedCallers = new();

    private static int _interceptCount;
    private static int _reroutedCount;

    internal static int InterceptCount => _interceptCount;
    internal static int ReroutedCount => _reroutedCount;

    /// <summary>Distinct "assembly:Type.Method" callers seen attaching onto an occupied slot.</summary>
    internal static System.Collections.Generic.IReadOnlyCollection<string> ReportedCallerKeys =>
        ReportedCallers.Keys.ToArray();

    internal static IDisposable SuppressForOwnMutation()
    {
        bool previous = _inOwnMutation;
        _inOwnMutation = true;
        return new Suppression(previous);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.EnchantInternal))]
    [HarmonyPrefix]
    private static bool BeforeEnchantInternal(CardModel __instance, EnchantmentModel enchantment, decimal amount)
    {
        try
        {
            if (_inOwnMutation || __instance.Enchantment == null)
            {
                return true;
            }

            // Re-attaching the very same instance is a no-op replay, not a destructive overwrite.
            if (ReferenceEquals(__instance.Enchantment, enchantment))
            {
                return true;
            }

            System.Threading.Interlocked.Increment(ref _interceptCount);
            ReportCaller(__instance, enchantment);

            if (!MultiEnchantmentMod.GuardEnchantInternal)
            {
                return true;
            }

            MultiEnchantmentSupport.AttachAdditionalForForeignEnchantInternal(
                __instance, enchantment, (int)amount);
            System.Threading.Interlocked.Increment(ref _reroutedCount);
            return false;
        }
        catch (Exception ex)
        {
            // A guard that throws would take down whatever third-party flow called in. Fall back to
            // vanilla behavior on any failure.
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] EnchantInternal guard failed; letting the base-game path run. {ex}");
            return true;
        }
    }

    private static void ReportCaller(CardModel card, EnchantmentModel enchantment)
    {
        string callerKey = DescribeExternalCaller();
        if (!ReportedCallers.TryAdd(callerKey, 0))
        {
            return;
        }

        string action = MultiEnchantmentMod.GuardEnchantInternal
            ? "Routing it into an additional slot instead so neither is lost."
            : "Leaving the base-game behavior in place, which DISCARDS the existing enchantment. " +
              "Set \"guardEnchantInternal\": true in MultiEnchantmentMod.json to keep both instead.";

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment] {callerKey} called CardModel.EnchantInternal to attach " +
            $"{enchantment.GetType().Name} to '{card.Id}', which already carries " +
            $"{card.Enchantment?.GetType().Name ?? "<none>"}. Vanilla assigns the primary slot " +
            $"unconditionally, so this overwrites the existing enchantment. {action}");
    }

    /// <summary>
    /// Walks the stack for the first frame outside this mod and the game assembly, so the warning
    /// names the mod responsible rather than the vanilla method everyone goes through.
    /// </summary>
    private static string DescribeExternalCaller()
    {
        try
        {
            Assembly self = typeof(MultiEnchantmentEnchantInternalGuard).Assembly;
            StackTrace trace = new(fNeedFileInfo: false);

            for (int i = 0; i < trace.FrameCount; i++)
            {
                MethodBase? method = trace.GetFrame(i)?.GetMethod();
                Type? declaring = method?.DeclaringType;
                if (method == null || declaring == null)
                {
                    continue;
                }

                Assembly assembly = declaring.Assembly;
                if (assembly == self || assembly == typeof(CardModel).Assembly)
                {
                    continue;
                }

                string assemblyName = assembly.GetName().Name ?? "<unknown>";
                return $"{assemblyName}:{declaring.FullName}.{method.Name}";
            }
        }
        catch
        {
            // Attribution is best-effort; never let it break the guard.
        }

        return "<unattributed caller>";
    }

    private sealed class Suppression : IDisposable
    {
        private readonly bool _previous;

        internal Suppression(bool previous) => _previous = previous;

        public void Dispose() => _inOwnMutation = _previous;
    }
}

using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace MultiEnchantmentMod;

[ModInitializer(nameof(Initialize))]
public partial class MultiEnchantmentMod : Node
{
    private const string ModId = "MultiEnchantmentMod";
    private static bool _loggedThievingHopperReflectionFallback;

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        MultiEnchantmentSupport.Initialize();

        // Register every built-in MegaCrit enchantment via the v2 fluent builder before any
        // Resolve* call can hit. Replaces the hardcoded switch tables that used to live in
        // MultiEnchantmentStackSupport.GetBuiltInDefinition / GetBuiltInKeywordSourceAmount.
        Api.Internal.BuiltInRegistrations.RegisterAll();

        // Pre-scan our own assembly so any v2 attributes / EnchantmentDefinition<T> classes the
        // mod itself ships (none yet — the built-in matrix is registered above) get picked up
        // before the first Resolve* call. Third-party mods are expected to call
        // MultiEnchantmentApi.ScanCallingAssembly() from their own [ModInitializer]; the lazy
        // first-Resolve fallback in MultiEnchantmentStackApi covers anyone who forgets.
        Api.MultiEnchantmentApi.ScanAssembly(Assembly.GetExecutingAssembly());

        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());
        LogHarmonyPatchConflicts();
        PatchThievingHopperPriorities();

        // Final discovery sweep before the game proper starts: pick up every loaded assembly
        // that references the API. Third-party mods whose own [ModInitializer] runs after ours
        // can still ScanCallingAssembly themselves up until the first BeforeCombatStart Harmony
        // patch fires SealRegistryIfNeeded — at that point the registry freezes for the run.
        // Hot-path registry reads no longer call EnsureScanned, so this single sweep is the
        // canonical "I see everything that's loaded now" pass.
        Api.Internal.AssemblyScanner.EnsureScanned();

        // Boot-time integrity check for the small set of vanilla methods this mod copies
        // verbatim. Logs current IL hashes (and any drift vs. frozen baseline). Idempotent.
        Api.Internal.VanillaCopyGuard.RunOnce();

        // Telemetry: read config from MultiEnchantmentMod.json and install crash reporter.
        // Session data is sent later at first BeforeCombatStart (after all mods have registered).
        Telemetry.TelemetryConfig.Initialize();
        if (Telemetry.TelemetryConfig.IsEnabled)
        {
            Telemetry.CrashReporter.Install(Telemetry.TelemetryCollector.SessionId);
        }
    }

    private static void LogHarmonyPatchConflicts()
    {
        try
        {
            int conflictCount = 0;
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Patches patchInfo = Harmony.GetPatchInfo(method);
                if (patchInfo == null)
                {
                    continue;
                }

                // Collect our patches and other patches separately with full detail.
                List<string> ourPatchDetails = new();
                List<string> otherPatchDetails = new();
                HashSet<string> otherOwners = new();

                foreach (Patch patch in patchInfo.Prefixes)
                {
                    string detail = FormatPatchDetail("Prefix", patch);
                    if (patch.owner == ModId) ourPatchDetails.Add(detail);
                    else { otherPatchDetails.Add(detail); otherOwners.Add(patch.owner); }
                }
                foreach (Patch patch in patchInfo.Postfixes)
                {
                    string detail = FormatPatchDetail("Postfix", patch);
                    if (patch.owner == ModId) ourPatchDetails.Add(detail);
                    else { otherPatchDetails.Add(detail); otherOwners.Add(patch.owner); }
                }
                foreach (Patch patch in patchInfo.Transpilers)
                {
                    string detail = FormatPatchDetail("Transpiler", patch);
                    if (patch.owner == ModId) ourPatchDetails.Add(detail);
                    else { otherPatchDetails.Add(detail); otherOwners.Add(patch.owner); }
                }

                if (ourPatchDetails.Count > 0 && otherOwners.Count > 0)
                {
                    string methodName = $"{method.DeclaringType?.FullName}.{method.Name}";
                    Logger.Info(
                        $"[MultiEnchantment] Shared patch target: {methodName}\n" +
                        $"  Ours: {string.Join("; ", ourPatchDetails)}\n" +
                        $"  Others: {string.Join("; ", otherPatchDetails)}");
                    conflictCount++;
                }
            }

            if (conflictCount > 0)
            {
                Logger.Info($"[MultiEnchantment] Detected {conflictCount} shared Harmony patch target(s) with other mods. " +
                    "This is informational — conflicts may or may not cause issues.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[MultiEnchantment] Failed to scan for Harmony patch conflicts: {ex.Message}");
        }
    }

    private static string FormatPatchDetail(string patchType, Patch patch)
    {
        string methodName = patch.PatchMethod?.Name ?? "unknown";
        string owner = patch.owner ?? "unknown";
        return $"{patchType}({owner}, pri={patch.priority}, {methodName})";
    }

    private static void PatchThievingHopperPriorities()
    {
        // Base-game source: ThievingHopper._stealPriorities.
        // We only widen the Imbued check so multi-enchanted cards are prioritized consistently.
        FieldInfo? field = AccessTools.Field(typeof(ThievingHopper), "_stealPriorities");
        if (field == null)
        {
            LogThievingHopperReflectionFallback("Field _stealPriorities was not found.");
            return;
        }

        object? value;
        try
        {
            value = field.GetValue(null);
        }
        catch (Exception ex)
        {
            LogThievingHopperReflectionFallback($"Reading _stealPriorities threw: {ex}");
            return;
        }

        if (value is not Func<CardModel, bool>[] priorities)
        {
            LogThievingHopperReflectionFallback("Field _stealPriorities did not contain the expected delegate array.");
            return;
        }

        if (priorities.Length < 4)
        {
            LogThievingHopperReflectionFallback($"Field _stealPriorities had length {priorities.Length}, expected at least 4.");
            return;
        }

        priorities[0] = static card => !MultiEnchantmentSupport.HasEnchantment<Imbued>(card) &&
                                       card.Rarity == CardRarity.Uncommon;
        priorities[1] = static card => !MultiEnchantmentSupport.HasEnchantment<Imbued>(card) &&
                                       (card.Rarity == CardRarity.Common ||
                                        card.Rarity == CardRarity.Rare ||
                                        card.Rarity == CardRarity.Event);
        priorities[2] = static card => !MultiEnchantmentSupport.HasEnchantment<Imbued>(card) &&
                                       (card.Rarity == CardRarity.Basic ||
                                        card.Rarity == CardRarity.Quest);
        priorities[3] = static card => card.Rarity == CardRarity.Ancient ||
                                       MultiEnchantmentSupport.HasEnchantment<Imbued>(card);
    }

    private static void LogThievingHopperReflectionFallback(string reason)
    {
        if (_loggedThievingHopperReflectionFallback)
        {
            return;
        }

        _loggedThievingHopperReflectionFallback = true;
        Logger.Warn(
            "[MultiEnchantmentMod] Failed to patch ThievingHopper steal priorities via reflection. Falling back to the base-game implementation, which may ignore additional Imbued enchantments. Reason: " +
            reason);
    }
}

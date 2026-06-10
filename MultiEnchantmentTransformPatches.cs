using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using static MultiEnchantmentMod.SafeLog;

namespace MultiEnchantmentMod;

[HarmonyPatch]
internal static class MultiEnchantmentTransformPatches
{
    private static readonly PropertyInfo? ArchaicToothTranscendenceUpgradesProperty =
        AccessTools.Property(typeof(ArchaicTooth), "TranscendenceUpgrades");
    private static bool _loggedArchaicToothReflectionFallback;

    [HarmonyPatch(typeof(ArchaicTooth), "GetTranscendenceTransformedCard")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool ArchaicToothPrefix(ArchaicTooth __instance, CardModel starterCard, ref CardModel __result)
    {
        // Base-game source: ArchaicTooth.GetTranscendenceTransformedCard.
        // Preserve the vanilla transform result, then copy over every compatible enchantment.
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting ArchaicTooth.GetTranscendenceTransformedCard. " +
            $"StarterCard={GetSafeCardId(starterCard)}");
        try
        {
            if (!TryGetTranscendenceTransformedCardWithMultiEnchantments(__instance, starterCard, out CardModel? result))
            {
                LogArchaicToothReflectionFallback();
                return true;
            }

            __result = result;
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] ArchaicTooth.GetTranscendenceTransformedCard failed for StarterCard={GetSafeCardId(starterCard)}. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(Claws), "CreateMaulFromOriginal")]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool ClawsPrefix(Claws __instance, CardModel original, bool forPreview, ref CardModel __result)
    {
        // Base-game source: Claws.CreateMaulFromOriginal.
        // Preserve the vanilla Maul creation/upgrade rules, then copy compatible enchantments.
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Claws.CreateMaulFromOriginal. " +
            $"Original={GetSafeCardId(original)} ForPreview={forPreview}");
        try
        {
            __result = CreateMaulFromOriginalWithMultiEnchantments(__instance, original, forPreview);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Claws.CreateMaulFromOriginal failed for Card={GetSafeCardId(original)}. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    private static bool TryGetTranscendenceTransformedCardWithMultiEnchantments(ArchaicTooth relic, CardModel starterCard, out CardModel result)
    {
        result = null!;

        if (ArchaicToothTranscendenceUpgradesProperty == null)
        {
            return false;
        }

        Dictionary<ModelId, CardModel>? upgrades;
        try
        {
            upgrades = ArchaicToothTranscendenceUpgradesProperty.GetValue(null) as Dictionary<ModelId, CardModel>;
        }
        catch (Exception ex)
        {
            LogArchaicToothReflectionFallback(ex);
            return false;
        }

        if (upgrades == null)
        {
            return false;
        }

        if (upgrades.TryGetValue(starterCard.Id, out CardModel? upgradedCard))
        {
            result = starterCard.Owner.RunState.CreateCard(upgradedCard, starterCard.Owner);
            if (starterCard.IsUpgraded)
            {
                CardCmd.Upgrade(result);
            }
        }
        else
        {
            result = relic.Owner.RunState.CreateCard<Doubt>(starterCard.Owner);
        }

        result = MultiEnchantmentTransformApi.CopyCompatibleEnchantments(starterCard, result);
        return true;
    }

    private static CardModel CreateMaulFromOriginalWithMultiEnchantments(Claws relic, CardModel original, bool forPreview)
    {
        CardModel result = forPreview ? ModelDb.Card<Maul>().ToMutable() : relic.Owner.RunState.CreateCard<Maul>(relic.Owner);
        if (original.IsUpgraded && result.IsUpgradable)
        {
            if (forPreview)
            {
                result.UpgradeInternal();
            }
            else
            {
                CardCmd.Upgrade(result);
            }
        }

        return MultiEnchantmentTransformApi.CopyCompatibleEnchantments(original, result);
    }

    // === Transform / clone lifecycle bridges ==================================================
    // Vanilla CardCmd.Transform calls original.AfterTransformedFrom() and
    // replacement.AfterTransformedTo() back-to-back per transformation (verified identical in
    // 0.106.x and 0.107.0), so the postfix pair below records the original and dispatches the
    // OnCardTransformed lifecycle when the replacement lands. SovereignBlade is the only vanilla
    // override of AfterTransformedFrom and does not call base, so it gets its own patch.

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.AfterTransformedFrom))]
    [HarmonyPostfix]
    private static void CardModelAfterTransformedFromPostfix(CardModel __instance)
    {
        try
        {
            MultiEnchantmentScopeSupport.NoteTransformSource(__instance);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CardModel.AfterTransformedFrom postfix failed for Card={GetSafeCardId(__instance)}. Error: {ex}");
        }
    }

    [HarmonyPatch(typeof(SovereignBlade), nameof(SovereignBlade.AfterTransformedFrom))]
    [HarmonyPostfix]
    private static void SovereignBladeAfterTransformedFromPostfix(SovereignBlade __instance)
    {
        try
        {
            MultiEnchantmentScopeSupport.NoteTransformSource(__instance);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] SovereignBlade.AfterTransformedFrom postfix failed for Card={GetSafeCardId(__instance)}. Error: {ex}");
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.AfterTransformedTo))]
    [HarmonyPostfix]
    private static void CardModelAfterTransformedToPostfix(CardModel __instance)
    {
        try
        {
            MultiEnchantmentScopeSupport.DispatchOnCardTransformed(__instance);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CardModel.AfterTransformedTo postfix failed for Card={GetSafeCardId(__instance)}. Error: {ex}");
        }
    }

    // CardModel.CreateClone is the gameplay clone entry point (Juggling, Nightmare, Music Box,
    // Dual Wield, Heirloom Hammer, Burning Sticks, History Course via CreateDupe, ...). UI
    // previews clone via CombatState.CloneCard / RunState.CloneCard directly and never reach
    // this method, so author hooks cannot fire from a preview. Enchantment inheritance itself is
    // handled universally by the AbstractModel.MutableClone postfix — this patch only adds the
    // OnCardCloned notification.
    [HarmonyPatch(typeof(CardModel), nameof(CardModel.CreateClone))]
    [HarmonyPostfix]
    private static void CardModelCreateClonePostfix(CardModel __instance, CardModel __result)
    {
        try
        {
            MultiEnchantmentScopeSupport.DispatchOnCardCloned(__instance, __result);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CardModel.CreateClone postfix failed for Card={GetSafeCardId(__instance)}. Error: {ex}");
        }
    }

    private static void LogArchaicToothReflectionFallback(Exception? ex = null)
    {
        if (_loggedArchaicToothReflectionFallback)
        {
            return;
        }

        _loggedArchaicToothReflectionFallback = true;
        string suffix = ex == null ? string.Empty : $" Reason: {ex}";
        MultiEnchantmentMod.Logger.Warn(
            "[TransformApi] Failed to mirror ArchaicTooth.GetTranscendenceTransformedCard via reflection. Falling back to the base-game implementation, which may only preserve the primary enchantment." +
            suffix);
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;

namespace MultiEnchantmentMod;

[HarmonyPatch]
internal static class MultiEnchantmentTransformPatches
{
    private static readonly PropertyInfo? ArchaicToothTranscendenceUpgradesProperty =
        AccessTools.Property(typeof(ArchaicTooth), "TranscendenceUpgrades");
    private static bool _loggedArchaicToothReflectionFallback;

    [HarmonyPatch(typeof(ArchaicTooth), "GetTranscendenceTransformedCard")]
    [HarmonyPrefix]
    private static bool ArchaicToothPrefix(ArchaicTooth __instance, CardModel starterCard, ref CardModel __result)
    {
        // Base-game source: ArchaicTooth.GetTranscendenceTransformedCard.
        // Preserve the vanilla transform result, then copy over every compatible enchantment.
        if (!TryGetTranscendenceTransformedCardWithMultiEnchantments(__instance, starterCard, out CardModel? result))
        {
            LogArchaicToothReflectionFallback();
            return true;
        }

        __result = result;
        return false;
    }

    [HarmonyPatch(typeof(Claws), "CreateMaulFromOriginal")]
    [HarmonyPrefix]
    private static bool ClawsPrefix(Claws __instance, CardModel original, bool forPreview, ref CardModel __result)
    {
        // Base-game source: Claws.CreateMaulFromOriginal.
        // Preserve the vanilla Maul creation/upgrade rules, then copy compatible enchantments.
        __result = CreateMaulFromOriginalWithMultiEnchantments(__instance, original, forPreview);
        return false;
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

    private static void LogArchaicToothReflectionFallback(Exception? ex = null)
    {
        if (_loggedArchaicToothReflectionFallback)
        {
            return;
        }

        _loggedArchaicToothReflectionFallback = true;
        string suffix = ex == null ? string.Empty : $" Reason: {ex.GetBaseException().Message}";
        MultiEnchantmentMod.Logger.Warn(
            "[TransformApi] Failed to mirror ArchaicTooth.GetTranscendenceTransformedCard via reflection. Falling back to the base-game implementation, which may only preserve the primary enchantment." +
            suffix);
    }
}

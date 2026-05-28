using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.ValueProps;

namespace MultiEnchantmentMod;

[HarmonyPatch]
internal static class MultiEnchantmentStackPatches
{
    [HarmonyPatch(typeof(Glam), nameof(Glam.EnchantPlayCount))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool GlamEnchantPlayCountPrefix(Glam __instance, int originalPlayCount, ref int __result)
    {
        int result = __instance.Status == EnchantmentStatus.Disabled
            ? originalPlayCount
            : originalPlayCount + __instance.Amount;
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Glam.EnchantPlayCount. " +
            $"Card={__instance.Card?.Id} Original={originalPlayCount} Result={result} Disabled={__instance.Status == EnchantmentStatus.Disabled}");
        __result = result;
        return false;
    }

    [HarmonyPatch(typeof(Spiral), nameof(Spiral.EnchantPlayCount))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool SpiralEnchantPlayCountPrefix(Spiral __instance, int originalPlayCount, ref int __result)
    {
        int result = originalPlayCount + __instance.Amount;
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Spiral.EnchantPlayCount. " +
            $"Card={__instance.Card?.Id} Original={originalPlayCount} Amount={__instance.Amount} Result={result}");
        __result = result;
        return false;
    }

    [HarmonyPatch(typeof(Slither), nameof(Slither.AfterCardDrawn))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool SlitherAfterCardDrawnPrefix(Slither __instance, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw, ref Task __result)
    {
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Slither.AfterCardDrawn. " +
            $"Card={card.Id} FromHandDraw={fromHandDraw} SlitherCard={__instance.Card?.Id}");
        try
        {
            __result = HandleStackedSlitherAfterCardDrawn(__instance, card);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Slither.AfterCardDrawn failed for Card={card.Id}. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(Imbued), nameof(Imbued.AfterAutoPrePlayPhaseEntered))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool ImbuedAfterAutoPrePlayPhaseEnteredPrefix(Imbued __instance, PlayerChoiceContext choiceContext, Player player, ref Task __result)
    {
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Imbued.AfterAutoPrePlayPhaseEntered. " +
            $"Player={player.NetId} ImbuedCard={__instance.Card?.Id} Turn={player.PlayerCombatState?.TurnNumber}");
        try
        {
            __result = HandleStackedImbuedAfterAutoPrePlayPhaseEntered(__instance, choiceContext, player);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Imbued.AfterAutoPrePlayPhaseEntered failed. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(SlumberingEssence), nameof(SlumberingEssence.BeforeFlush))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool SlumberingEssenceBeforeFlushPrefix(
        SlumberingEssence __instance,
        PlayerChoiceContext choiceContext,
        Player player,
        ref Task __result)
    {
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting SlumberingEssence.BeforeFlush. " +
            $"Player={player.NetId} SlumberingCard={__instance.Card?.Id}");
        try
        {
            __result = HandleStackedSlumberingEssenceBeforeFlush(__instance, player);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] SlumberingEssence.BeforeFlush failed. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    private static Task HandleStackedSlitherAfterCardDrawn(Slither slither, CardModel card)
    {
        if (card != slither.Card || slither.Card.Pile?.Type != PileType.Hand)
        {
            return Task.CompletedTask;
        }

        int stackAmount = MultiEnchantmentStackApi.GetHookExecutionCount(
            slither,
            EnchantmentHookKind.AfterCardDrawn);
        for (int i = 0; i < stackAmount; i++)
        {
            int energyCost = slither.TestEnergyCostOverride >= 0
                ? slither.TestEnergyCostOverride
                : slither.Card.Owner.RunState.Rng.CombatEnergyCosts.NextInt(4);
            slither.Card.EnergyCost.SetThisCombat(energyCost);
        }

        NCard.FindOnTable(card)?.PlayRandomizeCostAnim();
        return Task.CompletedTask;
    }

    private static async Task HandleStackedImbuedAfterAutoPrePlayPhaseEntered(Imbued imbued, PlayerChoiceContext choiceContext, Player player)
    {
        if (player != imbued.Card.Owner || player.PlayerCombatState?.TurnNumber > 1)
        {
            return;
        }

        int stackAmount = MultiEnchantmentStackApi.GetHookExecutionCount(
            imbued,
            EnchantmentHookKind.BeforePlayPhaseStart);
        for (int i = 0; i < stackAmount; i++)
        {
            await CardCmd.AutoPlay(choiceContext, imbued.Card, null);
        }
    }

    private static Task HandleStackedSlumberingEssenceBeforeFlush(SlumberingEssence slumberingEssence, Player player)
    {
        if (player != slumberingEssence.Card.Owner)
        {
            return Task.CompletedTask;
        }

        CardPile? pile = slumberingEssence.Card.Pile;
        if (pile == null || pile.Type != PileType.Hand)
        {
            return Task.CompletedTask;
        }

        int stackAmount = MultiEnchantmentStackApi.GetHookExecutionCount(
            slumberingEssence,
            EnchantmentHookKind.BeforeFlush);
        for (int i = 0; i < stackAmount; i++)
        {
            slumberingEssence.Card.EnergyCost.AddUntilPlayed(-1);
        }

        return Task.CompletedTask;
    }
}

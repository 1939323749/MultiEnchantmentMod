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
    private static bool GlamEnchantPlayCountPrefix(Glam __instance, int originalPlayCount, ref int __result)
    {
        __result = __instance.Status == EnchantmentStatus.Disabled
            ? originalPlayCount
            : originalPlayCount + __instance.Amount;
        return false;
    }

    [HarmonyPatch(typeof(Spiral), nameof(Spiral.EnchantPlayCount))]
    [HarmonyPrefix]
    private static bool SpiralEnchantPlayCountPrefix(Spiral __instance, int originalPlayCount, ref int __result)
    {
        __result = originalPlayCount + __instance.Amount;
        return false;
    }

    [HarmonyPatch(typeof(Slither), nameof(Slither.AfterCardDrawn))]
    [HarmonyPrefix]
    private static bool SlitherAfterCardDrawnPrefix(Slither __instance, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw, ref Task __result)
    {
        __result = HandleStackedSlitherAfterCardDrawn(__instance, card);
        return false;
    }

    [HarmonyPatch(typeof(Imbued), nameof(Imbued.BeforePlayPhaseStart))]
    [HarmonyPrefix]
    private static bool ImbuedBeforePlayPhaseStartPrefix(Imbued __instance, PlayerChoiceContext choiceContext, Player player, ref Task __result)
    {
        __result = HandleStackedImbuedBeforePlayPhaseStart(__instance, choiceContext, player);
        return false;
    }

    [HarmonyPatch(typeof(SlumberingEssence), nameof(SlumberingEssence.BeforeFlush))]
    [HarmonyPrefix]
    private static bool SlumberingEssenceBeforeFlushPrefix(
        SlumberingEssence __instance,
        PlayerChoiceContext choiceContext,
        Player player,
        ref Task __result)
    {
        __result = HandleStackedSlumberingEssenceBeforeFlush(__instance, player);
        return false;
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

    private static async Task HandleStackedImbuedBeforePlayPhaseStart(Imbued imbued, PlayerChoiceContext choiceContext, Player player)
    {
        if (player != imbued.Card.Owner || imbued.Card.CombatState.RoundNumber != 1)
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

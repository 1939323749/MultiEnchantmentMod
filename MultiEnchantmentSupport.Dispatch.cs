using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

internal static partial class MultiEnchantmentSupport
{
    public static async Task RunAdditionalEnchantmentsOnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // Base-game source: CardModel.OnPlayWrapper.
        // Extra enchantments must execute in the same phase as the primary enchantment's OnPlay so
        // cards/relics/powers observing AfterCardPlayed see the post-OnPlay state consistently.
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(cardPlay.Card)
                     .Where(IsGameplayEnchantment)
                     .ToList())
        {
            if (!MultiEnchantmentScopeSupport.IsActive(cardPlay.Card, enchantment))
            {
                continue;
            }

            choiceContext.PushModel(enchantment);
            try
            {
                int onPlayCount = MultiEnchantmentStackApi.GetHookExecutionCount(
                    enchantment,
                    EnchantmentHookKind.OnPlay);
                for (int i = 0; i < onPlayCount; i++)
                {
                    await enchantment.OnPlay(choiceContext, cardPlay);
                    enchantment.InvokeExecutionFinished();
                    MultiEnchantmentScopeSupport.NoteActivation(enchantment, ActivationTrigger.OnPlay);
                }
            }
            finally
            {
                choiceContext.PopModel(enchantment);
            }
        }

        FlushPendingRemovals(cardPlay.Card);
    }

    public static async Task OnPlayWrapperWithMultiEnchantments(
        CardModel card,
        PlayerChoiceContext choiceContext,
        Creature? target,
        bool isAutoPlay,
        ResourceInfo resources,
        bool skipCardPileVisuals)
    {
        // Base-game source: CardModel.OnPlayWrapper in STS2 v0.99.1.
        // This copy stays intentionally close to vanilla. The only functional change is inserting
        // extra-enchantment OnPlay execution immediately after the primary enchantment OnPlay.
        ICombatState combatState = card.CombatState!;
        choiceContext.PushModel(card);
        await CombatManager.Instance.WaitForUnpause();
        SetCurrentTargetForMultiEnchantmentPatch(card, target);
        if (!isAutoPlay)
        {
            await CardPileCmd.AddDuringManualCardPlay(card);
        }
        else
        {
            await CardPileCmd.Add(card, PileType.Play, CardPilePosition.Bottom, null, skipCardPileVisuals);
            if (!skipCardPileVisuals)
            {
                await Cmd.CustomScaledWait(0.25f, 0.35f);
            }
        }

        (PileType resultPileType, CardPilePosition resultPilePosition) =
            Hook.ModifyCardPlayResultPileTypeAndPosition(
                combatState,
                card,
                isAutoPlay,
                resources,
                GetResultPileTypeForMultiEnchantmentPatch(card),
                CardPilePosition.Bottom,
                out IEnumerable<AbstractModel> modifiers);

        foreach (AbstractModel item in modifiers)
        {
            await item.AfterModifyingCardPlayResultPileOrPosition(card, resultPileType, resultPilePosition);
        }

        int playCount = card.GetEnchantedReplayCount() + 1;
        playCount = Hook.ModifyCardPlayCount(combatState, card, playCount, target, out List<AbstractModel> modifyingModels);
        playCount = ApplyCardPlayCountContributions(card, playCount);
        await Hook.AfterModifyingCardPlayCount(combatState, card, modifyingModels);

        ulong playStartTime = Time.GetTicksMsec();
        for (int i = 0; i < playCount; i++)
        {
            if (card.Type == CardType.Power)
            {
                await PlayPowerCardFlyVfxForMultiEnchantmentPatch(card);
            }
            else if (i > 0)
            {
                NCard? nCard = NCard.FindOnTable(card);
                if (nCard != null)
                {
                    await nCard.AnimMultiCardPlay();
                }
            }

            CardPlay cardPlay = new()
            {
                Card = card,
                Target = target,
                ResultPile = resultPileType,
                Resources = resources,
                IsAutoPlay = isAutoPlay,
                PlayIndex = i,
                PlayCount = playCount,
            };

            await Hook.BeforeCardPlayed(combatState, cardPlay);
            await DispatchBeforeCardPlayedStacked(card, cardPlay);
            CombatManager.Instance.History.CardPlayStarted(combatState, cardPlay);
            await OnPlayForMultiEnchantmentPatch(card, choiceContext, cardPlay);
            card.InvokeExecutionFinished();
            if (card.Enchantment != null && MultiEnchantmentScopeSupport.IsActive(card, card.Enchantment))
            {
                int primaryOnPlayCount = MultiEnchantmentStackApi.GetHookExecutionCount(
                    card.Enchantment,
                    EnchantmentHookKind.OnPlay);
                for (int j = 0; j < primaryOnPlayCount; j++)
                {
                    await card.Enchantment.OnPlay(choiceContext, cardPlay);
                    card.Enchantment.InvokeExecutionFinished();
                    MultiEnchantmentScopeSupport.NoteActivation(card.Enchantment, ActivationTrigger.OnPlay);
                }
            }

            await RunAdditionalEnchantmentsOnPlay(choiceContext, cardPlay);
            await DispatchOnPlayStacked(choiceContext, cardPlay);

            if (card.Affliction != null)
            {
                AfflictionModel affliction = card.Affliction;
                await affliction.OnPlay(choiceContext, target);
                affliction.InvokeExecutionFinished();
            }

            CombatManager.Instance.History.CardPlayFinished(combatState, cardPlay);
            if (CombatManager.Instance.IsInProgress)
            {
                await Hook.AfterCardPlayed(combatState, choiceContext, cardPlay);
                await DispatchAfterCardPlayedStacked(choiceContext, cardPlay);
            }
        }

        if (!skipCardPileVisuals)
        {
            float elapsed = (float)(Time.GetTicksMsec() - playStartTime) / 1000f;
            await Cmd.CustomScaledWait(0.15f - elapsed, 0.3f - elapsed);
        }

        CardPile? pile = card.Pile;
        if (pile != null && pile.Type == PileType.Play)
        {
            switch (resultPileType)
            {
                case PileType.None:
                    await CardPileCmd.RemoveFromCombat(card, skipCardPileVisuals);
                    break;
                case PileType.Exhaust:
                    await CardCmd.Exhaust(choiceContext, card, causedByEthereal: false, skipCardPileVisuals);
                    break;
                default:
                    await CardPileCmd.Add(card, resultPileType, resultPilePosition, null, skipCardPileVisuals);
                    break;
            }
        }

        await CombatManager.Instance.CheckForEmptyHand(choiceContext, card.Owner);
        if (card.EnergyCost.AfterCardPlayedCleanup())
        {
            card.InvokeEnergyCostChanged();
        }

        if (ClearTemporaryStarCostsOnPlay(card))
        {
            InvokeStarCostChangedForMultiEnchantmentPatch(card);
        }

        SetCurrentTargetForMultiEnchantmentPatch(card, null);
        InvokePlayedForMultiEnchantmentPatch(card);
        choiceContext.PopModel(card);
    }

    public static Task HandleGoopyAfterCardPlayed(Goopy goopy, PlayerChoiceContext context, CardPlay cardPlay)
    {
        if (cardPlay.Card != goopy.Card)
        {
            return Task.CompletedTask;
        }

        goopy.Amount++;
        RememberLastAppliedEnchantment(goopy.Card, goopy);
        MultiEnchantmentScopeSupport.NoteActivation(goopy, ActivationTrigger.AfterCardPlayed);
        FlushPendingRemovals(goopy.Card);
        goopy.Card.DynamicVars.RecalculateForUpgradeOrEnchant();
        goopy.Card.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(goopy.Card);
        TriggerEnchantmentChanged(goopy.Card);

        CardModel? deckVersion = goopy.Card.DeckVersion;
        if (deckVersion == null || ReferenceEquals(deckVersion, goopy.Card))
        {
            return Task.CompletedTask;
        }

        // Mod source: once Goopy is allowed to stack, its Amount becomes per-instance persistent
        // growth state, not "how many Goopies exist on the card". Mirror the matching instance on
        // DeckVersion instead of adding a new merged stack.
        List<Goopy> combatGoopies = GetEnchantments(goopy.Card).OfType<Goopy>().ToList();
        int goopyIndex = combatGoopies.IndexOf(goopy);
        if (goopyIndex < 0)
        {
            return Task.CompletedTask;
        }

        List<Goopy> deckGoopies = GetEnchantments(deckVersion).OfType<Goopy>().ToList();
        Goopy mirroredGoopy;
        if (goopyIndex < deckGoopies.Count)
        {
            mirroredGoopy = deckGoopies[goopyIndex];
        }
        else
        {
            EnchantmentModel? model = ModelDb.GetById<EnchantmentModel>(goopy.Id);
            if (model == null)
            {
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment] Could not mirror Goopy {goopy.Id} to DeckVersion because ModelDb.GetById returned null.");
                return Task.CompletedTask;
            }

            mirroredGoopy = (Goopy)model.ToMutable();
            AttachEnchantmentState(choiceContext: null, deckVersion, mirroredGoopy, 1, modifyCard: true, triggerChanged: false);
        }

        mirroredGoopy.Amount = goopy.Amount;
        RememberLastAppliedEnchantment(deckVersion, mirroredGoopy);
        deckVersion.DynamicVars.RecalculateForUpgradeOrEnchant();
        deckVersion.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(deckVersion);
        TriggerEnchantmentChanged(deckVersion);

        return Task.CompletedTask;
    }
}

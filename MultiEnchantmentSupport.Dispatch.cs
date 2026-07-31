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
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
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
                    if (cardPlay.Card.Owner.Creature.IsDead)
                    {
                        break;
                    }

                    enchantment.InvokeExecutionFinished();
                    MultiEnchantmentScopeSupport.NoteActivation(enchantment, ActivationTrigger.OnPlay);
                }
            }
            finally
            {
                choiceContext.PopModel(enchantment);
            }

            if (cardPlay.Card.Owner.Creature.IsDead)
            {
                break;
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
        // Base-game source: CardModel.OnPlayWrapper in STS2 v0.110.0.
        // This copy stays intentionally close to vanilla. Functional changes are limited to the
        // stacked hook dispatches and executing additional enchantments beside the primary one.
        choiceContext.PushModel(card);
        await CombatManager.Instance.WaitForUnpause();
        SetCurrentTargetForMultiEnchantmentPatch(card, target);
        SetCurrentPlayIndexForMultiEnchantmentPatch(card, 0);
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

        ICombatState? combatState = card.CombatState;
        if (combatState == null)
        {
            return;
        }

        CardLocation resultLocation = Hook.ModifyCardPlayResultLocation(
            combatState,
            card,
            isAutoPlay,
            resources,
            GetResultLocationForMultiEnchantmentPatch(card),
            out IEnumerable<AbstractModel> modifiers);

        foreach (AbstractModel item in modifiers)
        {
            await item.AfterModifyingCardPlayResultLocation(card, resultLocation);
        }

        int playCount = card.GetEnchantedReplayCount() + 1;
        playCount = Hook.ModifyCardPlayCount(combatState, card, playCount, target, out List<AbstractModel> modifyingModels);
        playCount = ApplyCardPlayCountContributions(card, playCount);
        await Hook.AfterModifyingCardPlayCount(combatState, card, modifyingModels);
        if (card.Owner.Creature.IsDead)
        {
            return;
        }

        Perf.Count("Play.card");

        ulong playStartTime = Time.GetTicksMsec();
        // v0.110.0: the effect body is tagged with the combat it belongs to, so the teardown and
        // empty-hand checks below can be dropped when that combat is no longer the running one.
        CombatId? effectCombatId = CombatManager.Instance.BeginCardOrPotionEffect(card.Owner);
        try
        {
            for (int i = 0; i < playCount; i++)
            {
                if (CombatManager.Instance.IsOverOrEnding)
                {
                    break;
                }

                SetCurrentPlayIndexForMultiEnchantmentPatch(card, i);
                Perf.Count("Play.replay");
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
                    Player = card.Owner,
                    Target = target,
                    ResultPile = resultLocation.pileType,
                    Resources = resources,
                    IsAutoPlay = isAutoPlay,
                    PlayIndex = i,
                    PlayCount = playCount,
                };

                await Hook.BeforeCardPlayed(combatState, cardPlay);
                await DispatchBeforeCardPlayedStacked(card, cardPlay);
                if (card.Owner.Creature.IsDead)
                {
                    return;
                }

                CombatManager.Instance.History.CardPlayStarted(combatState, cardPlay);

                // v0.110.0: vanilla now names the card as the branch source; it propagates into the
                // HookPlayerChoiceContext created when a non-owner makes the choice, so omitting it
                // would attribute multiplayer choices differently than the base game.
                BranchingPlayerChoiceContext branchingChoiceContext = new(
                    card,
                    LocalContext.NetId!.Value,
                    GameActionType.Combat,
                    choiceContext);
                branchingChoiceContext.PushModel(card);
                Task onPlayTask = OnPlayForMultiEnchantmentPatch(card, branchingChoiceContext, cardPlay);
                await branchingChoiceContext.AssignTaskAndWaitForPauseOrCompletion(onPlayTask);
                if (card.Owner.Creature.IsDead)
                {
                    return;
                }

                card.InvokeExecutionFinished();
                if (card.Enchantment != null && MultiEnchantmentScopeSupport.IsActive(card, card.Enchantment))
                {
                    int primaryOnPlayCount = MultiEnchantmentStackApi.GetHookExecutionCount(
                        card.Enchantment,
                        EnchantmentHookKind.OnPlay);
                    for (int j = 0; j < primaryOnPlayCount; j++)
                    {
                        await card.Enchantment.OnPlay(choiceContext, cardPlay);
                        if (card.Owner.Creature.IsDead)
                        {
                            return;
                        }

                        card.Enchantment.InvokeExecutionFinished();
                        MultiEnchantmentScopeSupport.NoteActivation(card.Enchantment, ActivationTrigger.OnPlay);
                    }
                }

                await RunAdditionalEnchantmentsOnPlay(choiceContext, cardPlay);
                if (card.Owner.Creature.IsDead)
                {
                    return;
                }

                await DispatchOnPlayStacked(choiceContext, cardPlay);
                if (card.Owner.Creature.IsDead)
                {
                    return;
                }

                if (card.Affliction != null)
                {
                    AfflictionModel affliction = card.Affliction;
                    await affliction.OnPlay(choiceContext, target);
                    if (card.Owner.Creature.IsDead)
                    {
                        return;
                    }

                    affliction.InvokeExecutionFinished();
                }

                CombatManager.Instance.History.CardPlayFinished(combatState, cardPlay);
                if (CombatManager.Instance.IsInProgress)
                {
                    await Hook.AfterCardPlayed(combatState, choiceContext, cardPlay);
                    if (card.Owner.Creature.IsDead)
                    {
                        return;
                    }

                    await DispatchAfterCardPlayedStacked(choiceContext, cardPlay);
                    if (card.Owner.Creature.IsDead)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            await CombatManager.Instance.EndCardOrPotionEffect(effectCombatId, card.Owner);
        }

        if (!skipCardPileVisuals)
        {
            float elapsed = (float)(Time.GetTicksMsec() - playStartTime) / 1000f;
            await Cmd.CustomScaledWait(0.15f - elapsed, 0.3f - elapsed);
        }

        Player originalOwner = card.Owner;
        if (originalOwner != resultLocation.player && resultLocation.pileType != PileType.None)
        {
            await CardPileCmd.GiveToAnotherPlayer(
                card,
                resultLocation.player,
                resultLocation.pileType,
                resultLocation.position);
        }

        CardPile? pile = card.Pile;
        if (pile != null && pile.Type == PileType.Play)
        {
            switch (resultLocation.pileType)
            {
                case PileType.None:
                    await CardPileCmd.RemoveFromCombat(card, skipCardPileVisuals);
                    break;
                case PileType.Exhaust:
                    await CardCmd.Exhaust(choiceContext, card, causedByEthereal: false, skipCardPileVisuals);
                    break;
                default:
                    await CardPileCmd.Add(card, resultLocation.pileType, resultLocation.position, null, skipCardPileVisuals);
                    break;
            }
        }

        await CombatManager.Instance.CheckForEmptyHand(effectCombatId, choiceContext, originalOwner);
        if (card.EnergyCost.AfterCardPlayedCleanup())
        {
            card.InvokeEnergyCostChanged();
        }

        if (ClearTemporaryStarCostsOnPlay(card))
        {
            InvokeStarCostChangedForMultiEnchantmentPatch(card);
        }

        SetCurrentTargetForMultiEnchantmentPatch(card, null);
        SetCurrentPlayIndexForMultiEnchantmentPatch(card, 0);
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

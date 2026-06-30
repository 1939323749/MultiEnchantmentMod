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
    public static int GetReplayCount(CardModel card)
    {
        Perf.MaybeDump("interactive sample (replay count)");
        using Perf.Scope _ = Perf.Measure("GetReplayCount");
        int replayCount = card.BaseReplayCount;
        foreach (OrderedEnchantmentEntry entry in GetOrderedEnchantmentEntries(card))
        {
            // Match the IsActive gating that ApplyDamageEnchantments / ApplyBlockEnchantments do.
            // A WhenActive-false Glam / Spiral must not contribute extra plays — otherwise the
            // "is it active" surface is inconsistent across pipelines.
            CardModel? ownerCard = entry.Enchantment.Card;
            if (ownerCard == null || !MultiEnchantmentScopeSupport.IsActive(ownerCard, entry.Enchantment))
            {
                continue;
            }

            replayCount = EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantPlayCount(replayCount));
        }

        return replayCount;
    }

    public static void RecalculateAdditionalEnchantments(CardModel card)
    {
        // Snapshot the extra enchantment list: a user-overridden RecalculateValues could call
        // mod APIs (RemoveEnchantment, Enchant, etc.) that mutate state.ExtraEnchantments.
        // Defensive snapshot keeps this loop safe even when called from vanilla recalc paths.
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card)
                     .Where(IsGameplayEnchantment)
                     .ToList())
        {
            enchantment.RecalculateValues();
        }
    }

    public static decimal ApplyDamageEnchantments(CardModel? card, decimal damage, ValueProp props, ModifyDamageHookType hookType)
    {
        Perf.MaybeDump("interactive sample (damage preview)");
        using Perf.Scope _ = Perf.Measure("ApplyDamageEnchantments");
        decimal result = damage;
        foreach (OrderedEnchantmentEntry entry in GetOrderedEnchantmentEntries(card))
        {
            CardModel? ownerCard = entry.Enchantment.Card;
            if (ownerCard == null || !MultiEnchantmentScopeSupport.IsActive(ownerCard, entry.Enchantment))
            {
                continue;
            }

            if (hookType.HasFlag(ModifyDamageHookType.Additive))
            {
                result += EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantDamageAdditive(result, props));
            }

            if (hookType.HasFlag(ModifyDamageHookType.Multiplicative))
            {
                result *= EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantDamageMultiplicative(result, props));
            }
        }

        return result;
    }

    public static decimal ApplyBlockEnchantments(CardModel? card, decimal block, ValueProp props)
    {
        decimal result = block;
        foreach (OrderedEnchantmentEntry entry in GetOrderedEnchantmentEntries(card))
        {
            CardModel? ownerCard = entry.Enchantment.Card;
            if (ownerCard == null || !MultiEnchantmentScopeSupport.IsActive(ownerCard, entry.Enchantment))
            {
                continue;
            }

            result += EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantBlockAdditive(result));
            result *= EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantBlockMultiplicative(result));
        }

        return result;
    }

    /// <summary>
    /// Single-pass aggregator for the new <see cref="IEnchantmentRegistration.ModifyDynamicVar"/>
    /// pipeline. Walks every registered contribution for <paramref name="varKey"/> in card
    /// application order × per-enchantment registration order, folding each into the running
    /// value. Returns <paramref name="baseValue"/> unchanged when nothing is registered for the
    /// key — caller-side short-circuit via <see cref="HasDynamicVarContributionsFor"/> avoids the
    /// fan-out cost in that case.
    /// </summary>
    /// <remarks>
    /// Order sensitivity is the design intent: "+5 then ×2" produces a different result than
    /// "×2 then +5". Same-type repeats under <c>MergeAmount</c> are expanded into gameplay
    /// slices for this pipeline so interleaved applications compose in their original order.
    /// </remarks>
    public static decimal ApplyDynamicVarEnchantments(CardModel? card, string varKey, decimal baseValue)
    {
        if (card == null || string.IsNullOrEmpty(varKey))
        {
            return baseValue;
        }

        // Reentrancy guard: prevent stack overflow when a contribution callback recursively
        // triggers evaluation of the same card+varKey (e.g. enchantment A reads B's damage, B reads A).
        // Default tuple equality uses EqualityComparer<T>.Default per element — for CardModel
        // (reference type without overridden Equals) this falls back to reference equality.
        _activeDynamicVarKeys ??= new HashSet<(CardModel, string)>();
        (CardModel, string) key = (card, varKey);
        if (!_activeDynamicVarKeys.Add(key))
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] ModifyDynamicVar reentrancy detected for var={varKey} on card={card.Id}; skipping nested call.");
            return baseValue;
        }

        try
        {
            decimal result = baseValue;
            foreach (OrderedDynamicVarEnchantmentEntry entry in GetOrderedDynamicVarEnchantmentEntries(card))
            {
                Type enchantmentType = entry.Enchantment.GetType();
                CardModel? ownerCard = entry.Enchantment.Card;
                if (ownerCard == null || !MultiEnchantmentScopeSupport.IsActive(ownerCard, entry.Enchantment))
                {
                    continue;
                }

                IReadOnlyList<DynamicVarContribution> contributions =
                    EnchantmentRegistry.GetContributions(enchantmentType, varKey);
                if (contributions.Count == 0)
                {
                    continue;
                }

                foreach (DynamicVarContribution contribution in contributions)
                {
                    try
                    {
                        result = contribution.Contribution(entry.Snapshot, result);
                    }
                    catch (Exception ex)
                    {
                        MultiEnchantmentMod.Logger.Warn(
                            $"[MultiEnchantment] ModifyDynamicVar({varKey}) contribution from " +
                            $"{enchantmentType.FullName} threw; skipping. Error: {ex}");
                    }
                }
            }

            return result;
        }
        finally
        {
            _activeDynamicVarKeys.Remove(key);
        }
    }

    public static decimal ApplyEnergyCostContributions(CardModel? card, decimal baseCost)
    {
        if (card == null || baseCost < 0m)
        {
            return baseCost;
        }

        decimal result = baseCost;
        foreach (EnchantmentStackSnapshot snapshot in GetOrderedActiveStackSnapshots(card))
        {
            foreach (EnchantmentEntry entry in EnchantmentRegistry.GetEntries(snapshot.EnchantmentType))
            {
                result = entry.ModifyEnergyCostInCombat(snapshot, result);
            }
        }

        return result;
    }

    /// <summary>
    /// Folds every active enchantment's <c>ModifyPowerAmountGiven</c> contributions over the
    /// running power amount. Called from the <c>Hook.ModifyPowerAmountGiven</c> postfix with the
    /// power application's <c>cardSource</c>, after vanilla's additive/multiplicative listener
    /// pipeline has produced its result.
    /// </summary>
    public static decimal ApplyPowerAmountGivenContributions(
        CardModel? card,
        PowerModel power,
        Creature giver,
        Creature? target,
        decimal amount)
    {
        if (card == null)
        {
            return amount;
        }

        PowerGivenContext? context = null;
        decimal result = amount;
        foreach (EnchantmentStackSnapshot snapshot in GetOrderedActiveStackSnapshots(card))
        {
            foreach (EnchantmentEntry entry in EnchantmentRegistry.GetEntries(snapshot.EnchantmentType))
            {
                if (entry.PowerAmountGivenContributions.Count == 0)
                {
                    continue;
                }

                context ??= new PowerGivenContext(power, giver, target, card);
                result = entry.ModifyPowerAmountGiven(snapshot, context, result);
            }
        }

        return result;
    }

    public static int ApplyCardPlayCountContributions(CardModel? card, int playCount)
    {
        if (card == null)
        {
            return playCount;
        }

        int result = playCount;
        foreach (EnchantmentStackSnapshot snapshot in GetOrderedActiveStackSnapshots(card))
        {
            foreach (EnchantmentEntry entry in EnchantmentRegistry.GetEntries(snapshot.EnchantmentType))
            {
                result = entry.ModifyCardPlayCount(snapshot, result);
            }
        }

        return result;
    }

    public static decimal ApplyHandDrawContributions(ICombatState? combatState, Player? player, decimal baseHandDraw)
    {
        if (combatState == null || player?.PlayerCombatState == null)
        {
            return baseHandDraw;
        }

        decimal result = baseHandDraw;
        foreach (CardModel card in player.PlayerCombatState.AllCards)
        {
            foreach (EnchantmentStackSnapshot snapshot in GetOrderedActiveStackSnapshots(card))
            {
                foreach (EnchantmentEntry entry in EnchantmentRegistry.GetEntries(snapshot.EnchantmentType))
                {
                    result = entry.ModifyHandDraw(snapshot, result);
                }
            }
        }

        return result;
    }

    public static Task DispatchOnPlayStacked(PlayerChoiceContext choiceContext, CardPlay? cardPlay)
    {
        return DispatchStackedHook(
            cardPlay?.Card,
            static entry => entry.OnPlayStacked != null,
            (entry, snapshot) => entry.RunOnPlayStacked(new Api.StackedOnPlayContext(
                snapshot,
                choiceContext,
                cardPlay)));
    }

    public static Task DispatchBeforeCardPlayedStacked(CardModel? card, CardPlay cardPlay)
    {
        return DispatchStackedHook(
            card,
            static entry => entry.BeforeCardPlayedStacked != null,
            (entry, snapshot) => entry.RunBeforeCardPlayedStacked(new Api.StackedBeforeCardPlayedContext(
                snapshot,
                cardPlay)));
    }

    public static Task DispatchAfterCardPlayedStacked(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        return DispatchStackedHook(
            cardPlay.Card,
            static entry => entry.AfterCardPlayedStacked != null,
            (entry, snapshot) => entry.RunAfterCardPlayedStacked(new Api.StackedAfterCardPlayedContext(
                snapshot,
                choiceContext,
                cardPlay)));
    }

    public static Task DispatchAfterSiblingAppliedStacked(
        PlayerChoiceContext? choiceContext,
        CardModel card,
        EnchantmentModel newcomer)
    {
        return DispatchStackedHook(
            card,
            static entry => entry.AfterSiblingAppliedStacked != null,
            (entry, snapshot) => entry.RunAfterSiblingAppliedStacked(new Api.StackedAfterSiblingAppliedContext(
                snapshot,
                choiceContext,
                card,
                newcomer)),
            shouldInvoke: snapshot => !ReferenceEquals(snapshot.AnchorInstance, newcomer));
    }

    public static Task DispatchAfterCardDrawnStacked(PlayerChoiceContext choiceContext, CardModel drawnCard, bool fromHandDraw)
    {
        return DispatchStackedHook(
            drawnCard,
            static entry => entry.AfterCardDrawnStacked != null,
            (entry, snapshot) => entry.RunAfterCardDrawnStacked(new Api.StackedAfterCardDrawnContext(
                snapshot,
                choiceContext,
                drawnCard,
                fromHandDraw)));
    }

    public static Task DispatchAfterAnyCardDrawnStacked(
        PlayerChoiceContext choiceContext,
        ICombatState? combatState,
        CardModel drawnCard,
        bool fromHandDraw)
    {
        return DispatchStackedHookForCombat(
            combatState,
            static entry => entry.AfterAnyCardDrawnStacked != null,
            (entry, snapshot) => entry.RunAfterAnyCardDrawnStacked(new Api.StackedAfterCardDrawnContext(
                snapshot,
                choiceContext,
                drawnCard,
                fromHandDraw)));
    }

    public static Task DispatchBeforeFlushStacked(PlayerChoiceContext? choiceContext, Player player)
    {
        return DispatchStackedHookForPlayer(
            player,
            static entry => entry.BeforeFlushStacked != null,
            (entry, snapshot) => entry.RunBeforeFlushStacked(new Api.StackedBeforeFlushContext(
                snapshot,
                choiceContext,
                player)),
            refreshAfterEachCard: false);
    }

    public static Task DispatchAfterDamageGivenStacked(
        PlayerChoiceContext choiceContext,
        CardModel? cardSource,
        Creature? dealer,
        DamageResult result,
        ValueProp props,
        Creature target)
    {
        return DispatchStackedHook(
            cardSource,
            static entry => entry.AfterDamageGivenStacked != null,
            (entry, snapshot) => entry.RunAfterDamageGivenStacked(new Api.StackedAfterDamageGivenContext(
                snapshot,
                choiceContext,
                dealer,
                result,
                props,
                target,
                cardSource)));
    }

    private static async Task DispatchStackedHook(
        CardModel? card,
        Func<EnchantmentEntry, bool> hasHandler,
        Func<EnchantmentEntry, EnchantmentStackSnapshot, Task> invoke,
        Func<EnchantmentStackSnapshot, bool>? shouldInvoke = null)
    {
        if (card == null)
        {
            return;
        }

        foreach (EnchantmentStackSnapshot snapshot in GetOrderedActiveStackSnapshots(card))
        {
            EnchantmentEntry? entry = EnchantmentRegistry.GetDefinitionEntry(
                snapshot.EnchantmentType,
                hasHandler);
            if (entry == null)
            {
                continue;
            }

            if (shouldInvoke != null && !shouldInvoke(snapshot))
            {
                continue;
            }

            await invoke(entry, snapshot);
        }

        MultiEnchantmentStackSupport.RefreshDerivedState(card);
        TriggerEnchantmentChanged(card);
    }

    private static async Task DispatchStackedHookForPlayer(
        Player? player,
        Func<EnchantmentEntry, bool> hasHandler,
        Func<EnchantmentEntry, EnchantmentStackSnapshot, Task> invoke,
        bool refreshAfterEachCard = true)
    {
        if (player?.PlayerCombatState == null)
        {
            return;
        }

        List<CardModel>? cardsToRefresh = refreshAfterEachCard ? null : new List<CardModel>();
        foreach (CardModel card in player.PlayerCombatState.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
        {
            bool invokedForCard = false;
            foreach (EnchantmentStackSnapshot snapshot in GetOrderedActiveStackSnapshots(card))
            {
                EnchantmentEntry? entry = EnchantmentRegistry.GetDefinitionEntry(
                    snapshot.EnchantmentType,
                    hasHandler);
                if (entry == null)
                {
                    continue;
                }

                await invoke(entry, snapshot);
                invokedForCard = true;
            }

            if (!invokedForCard)
            {
                continue;
            }

            if (refreshAfterEachCard)
            {
                MultiEnchantmentStackSupport.RefreshDerivedState(card);
                TriggerEnchantmentChanged(card);
            }
            else
            {
                cardsToRefresh!.Add(card);
            }
        }

        if (cardsToRefresh == null)
        {
            return;
        }

        foreach (CardModel card in cardsToRefresh)
        {
            MultiEnchantmentStackSupport.RefreshDerivedState(card);
            TriggerEnchantmentChanged(card);
        }
    }

    private static async Task DispatchStackedHookForCombat(
        ICombatState? combatState,
        Func<EnchantmentEntry, bool> hasHandler,
        Func<EnchantmentEntry, EnchantmentStackSnapshot, Task> invoke)
    {
        if (combatState is not CombatState concreteState)
        {
            return;
        }

        foreach (Player player in concreteState.Players.Where(static p => p.IsActiveForHooks && p.PlayerCombatState != null).ToList())
        {
            await DispatchStackedHookForPlayer(player, hasHandler, invoke, refreshAfterEachCard: false);
        }
    }

    private static List<EnchantmentStackSnapshot> GetOrderedActiveStackSnapshots(CardModel card)
    {
        MultiEnchantmentScopeSupport.RefreshActiveStatuses(card);

        List<EnchantmentStackSnapshot> snapshots = new();
        HashSet<Type> handledTypes = new();
        foreach (OrderedEnchantmentEntry entry in GetOrderedEnchantmentEntries(card))
        {
            Type type = entry.Enchantment.GetType();
            if (!handledTypes.Add(type))
            {
                continue;
            }

            EnchantmentStackSnapshot snapshot = MultiEnchantmentStackSupport.GetSnapshot(entry.Enchantment);
            if (snapshot.ActiveInstanceCount <= 0)
            {
                continue;
            }

            if (!snapshot.LiveInstances.Any(instance => instance.Card != null &&
                                                       MultiEnchantmentScopeSupport.IsActive(instance.Card, instance)))
            {
                continue;
            }

            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    /// <summary>
    /// Fast existence check the <see cref="MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar"/>
    /// postfix patch uses to skip work when no enchantment in the process contributes to the
    /// given key.
    /// </summary>
    public static bool HasDynamicVarContributionsFor(string varKey)
    {
        return !string.IsNullOrEmpty(varKey) && EnchantmentRegistry.HasContributionsFor(varKey);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod;

internal sealed class ScopeRuntimeState
{
    public EnchantmentScope Scope = EnchantmentScope.Permanent;
    public int ActivationCount;
    public int TurnsRemaining;
}

internal static class MultiEnchantmentScopeSupport
{
    internal static ScopeRuntimeState EnsureScopeState(CardModel card, EnchantmentModel enchantment)
    {
        ScopeRuntimeState state = MultiEnchantmentSupport.EnsureScopeState(card, enchantment);
        state.Scope = ResolveScope(card, enchantment);
        if (state.Scope is EnchantmentScope.LingerForTurnsScope linger && state.TurnsRemaining <= 0)
        {
            state.TurnsRemaining = linger.Turns;
        }

        return state;
    }

    internal static void DispatchOnApplied(CardModel card, EnchantmentModel enchantment)
    {
        ScopeRuntimeState state = EnsureScopeState(card, enchantment);
        state.ActivationCount = 0;
        if (state.Scope is EnchantmentScope.LingerForTurnsScope linger)
        {
            state.TurnsRemaining = linger.Turns;
        }

        InvokeLifecycle(card, enchantment, static (provider, owner, model) => provider.OnApplied(owner, model));
    }

    internal static void OnCombatStarted(ICombatState combatState)
    {
        foreach (CardModel card in EnumerateCombatCards(combatState, includeDeckVersions: false))
        {
            foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card).ToList())
            {
                ScopeRuntimeState state = EnsureScopeState(card, enchantment);
                state.ActivationCount = 0;
                if (state.Scope is EnchantmentScope.LingerForTurnsScope linger)
                {
                    state.TurnsRemaining = linger.Turns;
                }

                InvokeLifecycle(card, enchantment, static (provider, owner, model) => provider.OnCombatStart(owner, model));
            }
        }
    }

    internal static void OnCombatEnded(IRunState runState, ICombatState? combatState)
    {
        HashSet<CardModel> visited = new(ReferenceEqualityComparer.Instance);
        foreach (CardModel card in EnumerateCombatCards(combatState, includeDeckVersions: true))
        {
            visited.Add(card);
            HandleTurnEnd(card, RemovalReason.CombatEnded);
            HandleCombatEnd(card);
            RemoveScopedEnchantments(card, RemovalReason.CombatEnded, static scope =>
                scope is not EnchantmentScope.PermanentScope and not EnchantmentScope.ConditionalActiveScope);
        }

        if (runState is RunState concreteRunState)
        {
            foreach (Player player in concreteRunState.Players)
            {
                foreach (CardModel card in player.Deck.Cards)
                {
                    if (visited.Add(card))
                    {
                        HandleCombatEnd(card);
                        RemoveScopedEnchantments(card, RemovalReason.CombatEnded, static scope =>
                            scope is not EnchantmentScope.PermanentScope and not EnchantmentScope.ConditionalActiveScope);
                    }
                }
            }
        }
    }

    internal static void OnPlayerTurnStarted(ICombatState combatState, Player player)
    {
        foreach (CardModel card in player.PlayerCombatState?.AllCards ?? Enumerable.Empty<CardModel>())
        {
            foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card).ToList())
            {
                EnsureScopeState(card, enchantment);
                InvokeLifecycle(card, enchantment, static (provider, owner, model) => provider.OnTurnStart(owner, model));
            }
        }
    }

    internal static void OnPlayerTurnEnded(ICombatState combatState)
    {
        foreach (CardModel card in EnumerateCombatCards(combatState, includeDeckVersions: true))
        {
            HandleTurnEnd(card, RemovalReason.TurnEnded);
        }
    }

    internal static void NoteActivation(EnchantmentModel enchantment, ActivationTrigger trigger)
    {
        CardModel? card = enchantment.Card;
        if (card == null)
        {
            return;
        }

        ScopeRuntimeState state = EnsureScopeState(card, enchantment);
        if (state.Scope is not EnchantmentScope.MaxActivationsScope maxScope || maxScope.Trigger != trigger)
        {
            return;
        }

        state.ActivationCount++;
        if (state.ActivationCount >= maxScope.Max)
        {
            MultiEnchantmentSupport.QueuePendingRemoval(card, enchantment, RemovalReason.ActivationLimitReached);
        }
    }

    internal static bool IsActive(CardModel card, EnchantmentModel enchantment)
    {
        try
        {
            ScopeRuntimeState state = EnsureScopeState(card, enchantment);
            if (state.Scope is EnchantmentScope.ConditionalActiveScope conditional)
            {
                return conditional.Predicate(card, enchantment);
            }

            return true;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][Scope] Active predicate failed for {enchantment.Id} on {card.Id}: {ex.GetBaseException().Message}");
            return true;
        }
    }

    internal static bool RemoveEnchantmentWithReason(CardModel card, EnchantmentModel enchantment, RemovalReason reason)
    {
        return MultiEnchantmentSupport.RemoveEnchantmentInternal(card, enchantment, reason, bypassVeto: reason == RemovalReason.CardCleared);
    }

    private static void HandleCombatEnd(CardModel card)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetOrderedEnchantmentsForRemoval(card))
        {
            InvokeLifecycle(card, enchantment, static (provider, owner, model) => provider.OnCombatEnd(owner, model));
        }
    }

    private static void HandleTurnEnd(CardModel card, RemovalReason removalReason)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetOrderedEnchantmentsForRemoval(card))
        {
            ScopeRuntimeState state = EnsureScopeState(card, enchantment);
            InvokeLifecycle(card, enchantment, static (provider, owner, model) => provider.OnTurnEnd(owner, model));
            if (state.Scope is EnchantmentScope.UntilTurnEndsScope)
            {
                MultiEnchantmentSupport.QueuePendingRemoval(card, enchantment, removalReason);
            }
            else if (state.Scope is EnchantmentScope.LingerForTurnsScope)
            {
                state.TurnsRemaining--;
                if (state.TurnsRemaining <= 0)
                {
                    MultiEnchantmentSupport.QueuePendingRemoval(card, enchantment, RemovalReason.TurnLimitReached);
                }
            }
        }

        MultiEnchantmentSupport.FlushPendingRemovals(card);
    }

    private static void RemoveScopedEnchantments(CardModel card, RemovalReason reason, Func<EnchantmentScope, bool> shouldRemove)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetOrderedEnchantmentsForRemoval(card))
        {
            ScopeRuntimeState state = EnsureScopeState(card, enchantment);
            if (shouldRemove(state.Scope))
            {
                MultiEnchantmentSupport.QueuePendingRemoval(card, enchantment, reason);
            }
        }

        MultiEnchantmentSupport.FlushPendingRemovals(card);
    }

    private static EnchantmentScope ResolveScope(CardModel card, EnchantmentModel enchantment)
    {
        MultiEnchantmentStackApi.ILifecycleProviderRegistration? registration = MultiEnchantmentStackApi.ResolveLifecycleProvider(enchantment.GetType());
        return registration?.GetScope() ?? EnchantmentScope.Permanent;
    }

    private static void InvokeLifecycle(
        CardModel card,
        EnchantmentModel enchantment,
        Action<MultiEnchantmentStackApi.ILifecycleProviderRegistration, CardModel, EnchantmentModel> action)
    {
        MultiEnchantmentStackApi.ILifecycleProviderRegistration? registration = MultiEnchantmentStackApi.ResolveLifecycleProvider(enchantment.GetType());
        if (registration == null)
        {
            return;
        }

        try
        {
            action(registration, card, enchantment);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][Scope] Lifecycle handler failed for {enchantment.Id} on {card.Id}: {ex}");
        }
    }

    private static IEnumerable<CardModel> EnumerateCombatCards(ICombatState? combatState, bool includeDeckVersions)
    {
        if (combatState is not CombatState concreteState)
        {
            yield break;
        }

        HashSet<CardModel> seen = new(ReferenceEqualityComparer.Instance);
        foreach (Player player in concreteState.Players.Where(static player => player.IsActiveForHooks && player.PlayerCombatState != null))
        {
            foreach (CardModel card in player.PlayerCombatState!.AllCards.Where(static card => !card.HasBeenRemovedFromState))
            {
                if (seen.Add(card))
                {
                    yield return card;
                }

                if (includeDeckVersions && card.DeckVersion != null && !ReferenceEquals(card.DeckVersion, card) && seen.Add(card.DeckVersion))
                {
                    yield return card.DeckVersion;
                }
            }
        }
    }
}

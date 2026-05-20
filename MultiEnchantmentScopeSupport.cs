using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
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

    /// <summary>
    /// Fires the <c>OnRestored</c> lifecycle callback on every enchantment attached to
    /// <paramref name="card"/>. Called from <c>CardModel.FromSerializable</c>'s postfix after
    /// <c>DeserializeAdditionalEnchantments</c> has finished re-attaching extras — at that
    /// point every enchantment's <c>Card</c> back-reference is set and its <c>Props</c> has
    /// been populated by <c>RestoreSerializedProps</c>, so authors can safely rebuild any
    /// runtime cache keyed on either.
    /// </summary>
    internal static void DispatchOnRestoredForCard(CardModel? card)
    {
        if (card == null)
        {
            return;
        }

        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card).ToList())
        {
            EnsureScopeState(card, enchantment);
            InvokeLifecycle(card, enchantment, static (provider, owner, model) => provider.OnRestored(owner, model));
        }
    }

    // Tracks which cards have already received the OnCombatStart callback for a given combat,
    // and whether the initial sweep has completed. Needed because Hook.AfterCardEnteredCombat
    // fires per-card both during deck-setup (BEFORE BeforeCombatStart) and mid-combat (AFTER
    // BeforeCombatStart). The initial sweep should handle the former; the per-card patch should
    // handle only the latter. Without this guard, deck-setup cards would either get OnCombatStart
    // fired twice or get their per-combat state reset spuriously by the late-joiner path.
    private sealed class CombatLifecycleState
    {
        public bool InitialSweepCompleted;
        public readonly HashSet<CardModel> CombatStartFiredFor = new(ReferenceEqualityComparer.Instance);
    }

    private static readonly ConditionalWeakTable<CombatState, CombatLifecycleState> CombatLifecycles = new();

    internal static void OnCombatStarted(ICombatState combatState)
    {
        CombatLifecycleState? lifecycle = combatState is CombatState concreteState
            ? CombatLifecycles.GetOrCreateValue(concreteState)
            : null;
        if (lifecycle != null)
        {
            lifecycle.CombatStartFiredFor.Clear();
            lifecycle.InitialSweepCompleted = false;
        }

        foreach (CardModel card in EnumerateCombatCards(combatState, includeDeckVersions: false))
        {
            lifecycle?.CombatStartFiredFor.Add(card);
            ResetCombatScopeStateForCard(card);
            FireOnCombatStartCallbackForCard(card);
        }

        if (lifecycle != null)
        {
            lifecycle.InitialSweepCompleted = true;
        }
    }

    /// <summary>
    /// Postfix entry point for <c>Hook.AfterCardEnteredCombat</c>. Fires the
    /// <c>OnCombatStart</c> lifecycle callback for enchantments on cards that join combat after
    /// the initial sweep (e.g. mid-combat card copies via Astrolabe, Madness-generated cards).
    /// Skipped during deck setup (before the initial sweep marks completion) — those cards are
    /// covered by the sweep itself. Does NOT reset <c>ActivationCount</c> / <c>TurnsRemaining</c>
    /// because mid-combat arrivals get their state set up by <c>DispatchOnApplied</c> (fresh
    /// applications) or <c>CopyScopeState</c> (clones); resetting here would erase that.
    /// </summary>
    internal static void OnCardEnteredCombat(ICombatState combatState, CardModel card)
    {
        if (card == null || combatState is not CombatState concreteState)
        {
            return;
        }

        if (!CombatLifecycles.TryGetValue(concreteState, out CombatLifecycleState? lifecycle))
        {
            // BeforeCombatStart hasn't fired yet for this combat. The initial sweep will pick
            // this card up.
            return;
        }

        if (!lifecycle.InitialSweepCompleted)
        {
            // Deck setup phase: card is being added before the initial sweep runs. Sweep
            // handles it.
            return;
        }

        if (!lifecycle.CombatStartFiredFor.Add(card))
        {
            // Card already received OnCombatStart this combat (e.g. moved between piles and
            // re-entered).
            return;
        }

        FireOnCombatStartCallbackForCard(card);
    }

    private static void ResetCombatScopeStateForCard(CardModel card)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card).ToList())
        {
            ScopeRuntimeState state = EnsureScopeState(card, enchantment);
            state.ActivationCount = 0;
            if (state.Scope is EnchantmentScope.LingerForTurnsScope linger)
            {
                state.TurnsRemaining = linger.Turns;
            }
        }
    }

    private static void FireOnCombatStartCallbackForCard(CardModel card)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card).ToList())
        {
            EnsureScopeState(card, enchantment);
            InvokeLifecycle(card, enchantment, static (provider, owner, model) => provider.OnCombatStart(owner, model));
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

        if (state.Scope is EnchantmentScope.MaxActivationsScope maxScope && maxScope.Trigger == trigger)
        {
            state.ActivationCount++;
            if (state.ActivationCount >= maxScope.Max)
            {
                MultiEnchantmentSupport.QueuePendingRemoval(card, enchantment, RemovalReason.ActivationLimitReached);
            }
            return;
        }

        if (state.Scope is EnchantmentScope.RemoveWhenScope removeWhen &&
            removeWhen.CheckOn.Contains(trigger))
        {
            bool shouldRemove;
            try
            {
                shouldRemove = removeWhen.Predicate(card, enchantment);
            }
            catch (Exception ex)
            {
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment][Scope] RemoveWhen predicate failed for {enchantment.Id} on {card.Id}: {ex.GetBaseException().Message}");
                return;
            }

            if (shouldRemove)
            {
                MultiEnchantmentSupport.QueuePendingRemoval(card, enchantment, RemovalReason.ConditionMet);
            }
        }
    }

    /// <summary>
    /// Fires <see cref="NoteActivation"/> for every enchantment on <paramref name="card"/> with
    /// <paramref name="trigger"/>. Used by the per-card trigger patches
    /// (AfterCardPlayed / AfterCardDrawn / AfterCardExhausted / AfterCardDiscarded). Flushes
    /// pending removals so the next iteration step doesn't see a still-attached but
    /// scope-expired enchantment.
    /// </summary>
    internal static void DispatchActivationTriggerForCard(CardModel? card, ActivationTrigger trigger)
    {
        if (card == null)
        {
            return;
        }

        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card).ToList())
        {
            NoteActivation(enchantment, trigger);
        }

        MultiEnchantmentSupport.FlushPendingRemovals(card);
    }

    /// <summary>
    /// Fires <see cref="NoteActivation"/> for every enchantment on every one of
    /// <paramref name="player"/>'s combat-pile cards with <paramref name="trigger"/>. Used by
    /// player-wide trigger patches (AfterPlayerTurnStart / AfterPlayerTurnEnd /
    /// AfterDamageReceived).
    /// </summary>
    internal static void DispatchActivationTriggerForPlayer(Player? player, ActivationTrigger trigger)
    {
        if (player?.PlayerCombatState == null)
        {
            return;
        }

        foreach (CardModel card in player.PlayerCombatState.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
        {
            DispatchActivationTriggerForCard(card, trigger);
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

    // ── Multiplayer / save-restore: ScopeRuntimeState serialization ──────────────────────────
    //
    // ScopeRuntimeState (ActivationCount / TurnsRemaining) lives in-memory on
    // CardEnchantmentState.ScopeStates. Without explicit serialization, MaxActivations counters
    // and LingerForTurns countdowns reset to defaults on save/load and never synchronize across
    // multiplayer peers. We capture the state at the EnchantmentModel.ToSerializable boundary
    // (called from EnchantmentToSerializablePostfix) and lazy-restore the first time a card +
    // enchantment pair appears in EnsureScopeState on the receiving side. The Scope value itself
    // is NOT serialized: scope kind is registry-driven (ResolveScope) and both sides resolve it
    // independently from the shared mod registry; serializing a Func<> (ConditionalActiveScope's
    // predicate) would be wrong anyway.

    internal const string ScopeStateSavePropertyName = "MultiEnchantmentScopeData";

    private sealed record ScopeStatePayload(
        [property: JsonPropertyName("activation_count")] int ActivationCount,
        [property: JsonPropertyName("turns_remaining")] int TurnsRemaining);

    /// <summary>
    /// Restores <see cref="ScopeRuntimeState.ActivationCount"/> and
    /// <see cref="ScopeRuntimeState.TurnsRemaining"/> from the enchantment's
    /// <see cref="SavedProperties.strings"/> entry if present. Called lazily from the low-level
    /// <c>MultiEnchantmentSupport.EnsureScopeState</c> when a fresh <see cref="ScopeRuntimeState"/>
    /// is constructed — covers save-restore and multiplayer packet-receive paths uniformly.
    /// Returns true if data was restored; false if the key was absent or malformed.
    /// </summary>
    internal static bool TryRestoreScopeStateFromProps(EnchantmentModel enchantment, ScopeRuntimeState state)
    {
        SavedProperties? props = enchantment.Props;
        if (props?.strings == null)
        {
            return false;
        }

        int idx = props.strings.FindIndex(p => string.Equals(p.name, ScopeStateSavePropertyName, StringComparison.Ordinal));
        if (idx < 0)
        {
            return false;
        }

        string payloadJson = props.strings[idx].value;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            ScopeStatePayload? payload = JsonSerializer.Deserialize<ScopeStatePayload>(payloadJson);
            if (payload == null)
            {
                return false;
            }

            state.ActivationCount = payload.ActivationCount;
            state.TurnsRemaining = payload.TurnsRemaining;
            return true;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][Scope] Failed to deserialize scope state for {enchantment.Id}: {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>
    /// Copies the current in-memory <see cref="ScopeRuntimeState"/> for <paramref name="enchantment"/>
    /// into <paramref name="save"/>'s <see cref="SavedProperties.strings"/>. Skipped when the
    /// enchantment isn't attached to a card (no card → no scope state lookup possible) or when
    /// both counter fields are zero (default state — no packet bytes wasted on permanent enchants).
    /// Called from <c>EnchantmentToSerializablePostfix</c>; the receiving side's
    /// <c>EnchantmentModel.FromSerializable</c> + <c>RestoreSerializedProps</c> chain clones the
    /// entire <c>save.Props</c> back into the new enchantment's live <c>Props</c>, where
    /// <see cref="TryRestoreScopeStateFromProps"/> reads it on first <c>EnsureScopeState</c> hit.
    /// </summary>
    internal static void WriteScopeStateToSerializableProps(EnchantmentModel enchantment, ref SerializableEnchantment save)
    {
        CardModel? card = enchantment.Card;
        if (card == null)
        {
            return;
        }

        if (!MultiEnchantmentSupport.TryGetExistingScopeState(card, enchantment, out ScopeRuntimeState? state) || state == null)
        {
            return;
        }

        if (state.ActivationCount == 0 && state.TurnsRemaining == 0)
        {
            // Default / fresh state. Skip the property entirely so existing-receiver upgrades
            // don't accumulate empty Props.strings entries on permanent enchants.
            RemoveScopeStatePropertyFromSave(ref save);
            return;
        }

        ScopeStatePayload payload = new(state.ActivationCount, state.TurnsRemaining);
        string json = JsonSerializer.Serialize(payload);

        save.Props ??= new SavedProperties();
        save.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
        SavedProperties.SavedProperty<string> property = new(ScopeStateSavePropertyName, json);
        int existingIndex = save.Props.strings.FindIndex(saved => saved.name == ScopeStateSavePropertyName);
        if (existingIndex >= 0)
        {
            save.Props.strings[existingIndex] = property;
        }
        else
        {
            save.Props.strings.Add(property);
        }
    }

    private static void RemoveScopeStatePropertyFromSave(ref SerializableEnchantment save)
    {
        save.Props?.strings?.RemoveAll(p => string.Equals(p.name, ScopeStateSavePropertyName, StringComparison.Ordinal));
    }
}

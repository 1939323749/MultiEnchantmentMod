using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

internal sealed class ScopeRuntimeState
{
    public EnchantmentScope Scope = EnchantmentScope.Permanent;
    public EnchantmentScope? OverrideScope;
    public int ActivationCount;
    public int TurnsRemaining;
}

internal static class MultiEnchantmentScopeSupport
{
    [ThreadStatic]
    private static HashSet<EnchantmentModel>? _activeStatusRefreshStack;

    internal static ScopeRuntimeState EnsureScopeState(CardModel card, EnchantmentModel enchantment)
    {
        ScopeRuntimeState state = MultiEnchantmentSupport.EnsureScopeState(card, enchantment);
        EnchantmentScope registryScope = ResolveScope(card, enchantment);
        state.Scope = state.OverrideScope ?? registryScope;
        if (state.Scope is EnchantmentScope.LingerForTurnsScope linger && state.TurnsRemaining <= 0)
        {
            state.TurnsRemaining = linger.Turns;
        }

        // If the save tagged a scope kind, compare it against the now-resolved Scope and warn on
        // drift. Pending entries are one-shot — removed after the first compare so subsequent
        // EnsureScopeState calls for the same enchantment don't keep logging.
        if (TryConsumePendingScopeKindMismatchCheck(enchantment, out string? savedKind))
        {
            string currentKind = GetScopeKind(state.Scope);
            if (!string.Equals(savedKind, currentKind, StringComparison.Ordinal))
            {
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment][Scope] Restored counters for {enchantment.Id} were saved under scope kind '{savedKind}' but the current registry resolves '{currentKind}'. Counters were preserved; review the EnchantmentDefinition.Scope change if behavior looks off.");
            }
        }

        return state;
    }

    internal static bool IsPersistableScopeOverride(EnchantmentScope scope)
    {
        return scope is EnchantmentScope.PermanentScope
            or EnchantmentScope.UntilCombatEndsScope
            or EnchantmentScope.UntilTurnEndsScope
            or EnchantmentScope.LingerForTurnsScope
            or EnchantmentScope.MaxActivationsScope;
    }

    internal static bool RejectNonPersistableScopeOverride(EnchantmentScope scope, string apiName, EnchantmentModel enchantment)
    {
        if (IsPersistableScopeOverride(scope))
        {
            return false;
        }

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment][Scope] {apiName} rejected non-persistable scope override {scope.GetType().Name} for {enchantment.Id}. ConditionalActive/RemoveWhen overrides carry predicates and must be registered at the enchantment type level.");
        return true;
    }

    internal static ScopeRuntimeState SetScopeOverride(CardModel card, EnchantmentModel enchantment, EnchantmentScope? newScope)
    {
        ScopeRuntimeState state = EnsureScopeState(card, enchantment);
        state.OverrideScope = newScope;
        state.Scope = newScope ?? ResolveScope(card, enchantment);

        if (newScope is EnchantmentScope.LingerForTurnsScope linger)
        {
            state.TurnsRemaining = linger.Turns;
        }
        else if (newScope is EnchantmentScope.MaxActivationsScope)
        {
            state.ActivationCount = 0;
        }
        else if (state.Scope is EnchantmentScope.LingerForTurnsScope effectiveLinger && state.TurnsRemaining <= 0)
        {
            state.TurnsRemaining = effectiveLinger.Turns;
        }

        return state;
    }

    internal static void SetScopeOverrideOnApply(CardModel card, EnchantmentModel enchantment, EnchantmentScope? scopeOverride)
    {
        if (scopeOverride == null)
        {
            EnsureScopeState(card, enchantment);
            return;
        }

        SetScopeOverride(card, enchantment, scopeOverride);
    }

    internal static void DispatchOnApplied(CardModel card, EnchantmentModel enchantment)
    {
        ScopeRuntimeState state = EnsureScopeState(card, enchantment);
        state.ActivationCount = 0;
        if (state.Scope is EnchantmentScope.LingerForTurnsScope linger)
        {
            state.TurnsRemaining = linger.Turns;
        }

        InvokeLifecycle(card, enchantment, static entry => entry.OnApplied != null, static (entry, owner, model) => entry.RunOnApplied(owner, model));
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

        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
        {
            EnsureScopeState(card, enchantment);
            InvokeLifecycle(card, enchantment, static entry => entry.OnRestored != null, static (entry, owner, model) => entry.RunOnRestored(owner, model));
        }

        RefreshActiveStatuses(card);

        // OnRestored handlers may have mutated Status / Amount / Props as part of cache rebuild;
        // refresh derived state (keywords, DynamicVars, UI) so callers don't have to remember to
        // call NotifyPropsChanged after every restore.
        RefreshAfterUserCallbacks(card);
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
        // Reset SafeInvoker per-(type,hook) failure counters so a flaky callback that fired many
        // times last combat produces fresh detailed logs in this combat.
        Api.Internal.SafeInvoker.ResetThrottle();

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
            RefreshActiveStatuses(card);
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
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
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
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
        {
            EnsureScopeState(card, enchantment);
            InvokeLifecycle(card, enchantment, static entry => entry.OnCombatStart != null, static (entry, owner, model) => entry.RunOnCombatStart(owner, model));
        }

        // OnCombatStart handlers may mutate Status / Amount / Props (e.g. priming counters,
        // pre-disabling under conditions). Refresh derived state so the very first turn
        // observes the new keyword / DynamicVar values.
        RefreshAfterUserCallbacks(card);
    }

    internal static void OnCombatEnded(IRunState runState, ICombatState? combatState)
    {
        HashSet<CardModel> visited = new(ReferenceEqualityComparer.Instance);
        foreach (CardModel card in EnumerateCombatCards(combatState, includeDeckVersions: true))
        {
            visited.Add(card);
            HandleTurnEnd(card, RemovalReason.CombatEnded);
            HandleCombatEnd(card);
            RemoveScopedEnchantments(card, RemovalReason.CombatEnded, ShouldRemoveAtCombatEnd);
        }

        if (runState is RunState concreteRunState)
        {
            foreach (Player player in concreteRunState.Players.ToList())
            {
                foreach (CardModel card in player.Deck.Cards.ToList())
                {
                    if (visited.Add(card))
                    {
                        HandleCombatEnd(card);
                        RemoveScopedEnchantments(card, RemovalReason.CombatEnded, ShouldRemoveAtCombatEnd);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Phase 1.5 T1.5.3: predicate used by combat-end cleanup to decide which scoped enchantments
    /// survive to the next combat. The exclusion list must stay in sync with scope kinds that are
    /// semantically "cross-combat persistent" rather than "combat-scoped":
    /// <list type="bullet">
    ///   <item><description><see cref="EnchantmentScope.PermanentScope"/> — never auto-removed.</description></item>
    ///   <item><description><see cref="EnchantmentScope.ConditionalActiveScope"/> — controls IsActive only; presence is permanent.</description></item>
    ///   <item><description><see cref="EnchantmentScope.RemoveWhenScope"/> — author opted into predicate-driven removal that should keep re-evaluating across combats (e.g. "remove when HP &lt; 50%"). If we swept it at combat end, the predicate would never get a chance to fire in a later combat.</description></item>
    /// </list>
    /// Every other scope (<c>UntilCombatEnds</c>, <c>UntilTurnEnds</c>, <c>LingerForTurns</c>, <c>MaxActivations</c>) is combat-scoped by definition and should be cleared.
    /// </summary>
    private static bool ShouldRemoveAtCombatEnd(EnchantmentScope scope) =>
        scope is not EnchantmentScope.PermanentScope
            and not EnchantmentScope.ConditionalActiveScope
            and not EnchantmentScope.RemoveWhenScope;

    internal static void OnPlayerTurnStarted(ICombatState combatState, Player player)
    {
        // Snapshot AllCards: the inner InvokeLifecycle fires user OnTurnStart handlers which
        // may mutate AllCards (e.g. CardCmd.AddCardToHand). Same protection pattern as the
        // Dispatch* methods below — see DispatchOnCardPlayedForCard etc.
        foreach (CardModel card in (player.PlayerCombatState?.AllCards ?? Enumerable.Empty<CardModel>()).ToList())
        {
            // Clear the per-turn "last injected" pointer before the enchantment-count gate: a card
            // injected last turn may carry no live enchantments now but still needs its turn-scoped
            // marker reset so GetMostRecentlyAppliedEnchantmentThisTurn doesn't report stale data.
            MultiEnchantmentSupport.ResetLastAppliedEnchantmentThisTurn(card);

            List<EnchantmentModel> enchantments = MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList();
            if (enchantments.Count == 0)
            {
                continue;
            }

            foreach (EnchantmentModel enchantment in enchantments)
            {
                EnsureScopeState(card, enchantment);
                InvokeLifecycle(card, enchantment, static entry => entry.OnTurnStart != null, static (entry, owner, model) => entry.RunOnTurnStart(owner, model));
            }

            // Turn start can flip active-status predicates / mutate Status. Refresh derived
            // state so keyword caches and DynamicVars stay accurate.
            RefreshAfterUserCallbacks(card);
        }
    }

    internal static void OnPlayerTurnEnded(ICombatState combatState)
    {
        foreach (CardModel card in EnumerateCombatCards(combatState, includeDeckVersions: true))
        {
            HandleTurnEnd(card, RemovalReason.TurnEnded);

            // Turn end can flip active-status predicates or invalidate cached keywords
            // (e.g. user OnTurnEnd handlers mutating Status). Refresh derived state and
            // visuals so badges dim/un-dim and keyword caches stay accurate.
            RefreshAfterUserCallbacks(card);
        }
    }

    /// <summary>
    /// Post-callback refresh: recomputes derived keywords / DynamicVars and fires the UI
    /// <c>EnchantmentChanged</c> signal. Called by every dispatcher that fans out a user
    /// lifecycle callback so that mutations made inside the callback (Status / Amount / Props)
    /// propagate without the author having to remember <see cref="MultiEnchantmentApi.NotifyPropsChanged"/>.
    /// Idempotent and cheap (RefreshDerivedKeywords short-circuits when nothing changed).
    /// </summary>
    private static void RefreshAfterUserCallbacks(CardModel? card)
    {
        if (card == null) return;
        RefreshActiveStatuses(card);
        card.DynamicVars.RecalculateForUpgradeOrEnchant();
        card.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(card);
        MultiEnchantmentSupport.TriggerEnchantmentChanged(card);
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
                    $"[MultiEnchantment][Scope] RemoveWhen predicate failed for {enchantment.Id} on {card.Id}: {ex}");
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

        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
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

    // === Phase 3a — vanilla card-event lifecycle bridges =====================================
    // These dispatchers fan out one of the new lifecycle callbacks (OnCardPlayed / OnCardDrawn /
    // OnCardExhausted / OnCardDiscarded / OnCardEnteredCombat) to every enchantment on the
    // affected card. IsActive is enforced here — inactive enchantments do not receive the
    // callback, matching the gating already applied to damage / block / replay pipelines.

    private static void DispatchCardLifecycle(
        CardModel? card,
        Func<EnchantmentEntry, bool> hasHandler,
        Action<EnchantmentEntry, CardModel, EnchantmentModel> action)
    {
        if (card == null)
        {
            return;
        }

        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
        {
            if (!IsActive(card, enchantment))
            {
                continue;
            }
            InvokeLifecycle(card, enchantment, hasHandler, action);
        }

        // Card-event handlers (OnCardPlayed / Drawn / Exhausted / Discarded / EnteredCombat /
        // Retained) may mutate Status / Amount / Props. Refresh derived state after the batch.
        RefreshAfterUserCallbacks(card);
    }

    internal static void DispatchOnCardPlayedForCard(CardModel? card) =>
        DispatchCardLifecycle(card, static e => e.OnCardPlayed != null, static (e, c, m) => e.RunOnCardPlayed(c, m));

    internal static void DispatchOnCardDrawnForCard(CardModel? card) =>
        DispatchCardLifecycle(card, static e => e.OnCardDrawn != null, static (e, c, m) => e.RunOnCardDrawn(c, m));

    internal static void DispatchOnCardExhaustedForCard(CardModel? card) =>
        DispatchCardLifecycle(card, static e => e.OnCardExhausted != null, static (e, c, m) => e.RunOnCardExhausted(c, m));

    internal static void DispatchOnCardDiscardedForCard(CardModel? card) =>
        DispatchCardLifecycle(card, static e => e.OnCardDiscarded != null, static (e, c, m) => e.RunOnCardDiscarded(c, m));

    internal static void DispatchOnCardEnteredCombatForCard(CardModel? card) =>
        DispatchCardLifecycle(card, static e => e.OnCardEnteredCombat != null, static (e, c, m) => e.RunOnCardEnteredCombat(c, m));

    // === Phase 4 — broadcast card-event hooks ================================================
    // Unlike DispatchOnCard*ForCard (per-card only), these fire for ANY card event in combat.
    // Opt-in: only entries with the matching broadcast hook are resolved. The entry helper
    // centralizes the null-check + SafeInvoker.Run behavior.

    /// <summary>
    /// Broadcasts an "any card played" event to every active enchantment in combat that
    /// registered <c>OnAnyCardPlayed</c>. <paramref name="playedCard"/> is the event subject;
    /// each enchantment's <c>selfCard</c> is the card it lives on.
    /// </summary>
    internal static void DispatchOnAnyCardPlayedBroadcast(CardModel? playedCard, ICombatState? combatState)
    {
        if (playedCard == null || combatState == null) return;
        DispatchBroadcastLifecycle(combatState, playedCard,
            static e => e.OnAnyCardPlayed != null,
            static (e, played, selfCard, ench) => e.RunOnAnyCardPlayed(played, selfCard, ench));
    }

    internal static void DispatchOnAnyCardDrawnBroadcast(CardModel? drawnCard, ICombatState? combatState)
    {
        if (drawnCard == null || combatState == null) return;
        DispatchBroadcastLifecycle(combatState, drawnCard,
            static e => e.OnAnyCardDrawn != null,
            static (e, drawn, selfCard, ench) => e.RunOnAnyCardDrawn(drawn, selfCard, ench));
    }

    internal static void DispatchOnAnyCardExhaustedBroadcast(CardModel? exhaustedCard, ICombatState? combatState)
    {
        if (exhaustedCard == null || combatState == null) return;
        DispatchBroadcastLifecycle(combatState, exhaustedCard,
            static e => e.OnAnyCardExhausted != null,
            static (e, exhausted, selfCard, ench) => e.RunOnAnyCardExhausted(exhausted, selfCard, ench));
    }

    internal static void DispatchOnAnyCardDiscardedBroadcast(CardModel? discardedCard, ICombatState? combatState)
    {
        if (discardedCard == null || combatState == null) return;
        DispatchBroadcastLifecycle(combatState, discardedCard,
            static e => e.OnAnyCardDiscarded != null,
            static (e, discarded, selfCard, ench) => e.RunOnAnyCardDiscarded(discarded, selfCard, ench));
    }

    /// <summary>
    /// Fans out a broadcast card-event hook across every active enchantment on every card in
    /// combat. Only entries that register the hook are resolved, so enchantments without the
    /// hook are skipped before invocation.
    /// </summary>
    private static void DispatchBroadcastLifecycle<TContext>(
        ICombatState combatState,
        TContext context,
        Func<EnchantmentEntry, bool> hasHandler,
        Action<EnchantmentEntry, TContext, CardModel, EnchantmentModel> action)
    {
        if (combatState is not CombatState concreteState)
        {
            return;
        }

        foreach (Player player in concreteState.Players.Where(static p => p.IsActiveForHooks && p.PlayerCombatState != null).ToList())
        {
            foreach (CardModel selfCard in player.PlayerCombatState!.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
            {
                bool anyEnchantments = false;
                foreach (EnchantmentModel ench in MultiEnchantmentSupport.GetGameplayEnchantments(selfCard).ToList())
                {
                    anyEnchantments = true;
                    if (!IsActive(selfCard, ench)) continue;
                    InvokeLifecycleWithContext(selfCard, ench, context, hasHandler, action);
                }

                // Broadcast handlers (OnAnyCardPlayed / Drawn / etc.) may mutate state. Refresh.
                if (anyEnchantments)
                {
                    RefreshAfterUserCallbacks(selfCard);
                }
            }
        }
    }

    // === Phase 5 — sibling lifecycle dispatchers ==============================================

    /// <summary>
    /// Fires <c>OnSiblingApplied(card, self, newcomer)</c> on every active enchantment on
    /// <paramref name="card"/> (except <paramref name="newcomer"/> itself). Safe to call from
    /// within <c>DispatchOnApplied</c> — the iteration uses <c>.ToList()</c>.
    /// </summary>
    internal static void DispatchOnSiblingApplied(CardModel card, EnchantmentModel newcomer)
    {
        foreach (EnchantmentModel sibling in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
        {
            if (ReferenceEquals(sibling, newcomer)) continue;
            if (!IsActive(card, sibling)) continue;
            InvokeLifecycleWithContext(card, sibling, newcomer,
                static entry => entry.OnSiblingApplied != null,
                static (entry, newcomer_, selfCard, self) => entry.RunOnSiblingApplied(selfCard, self, newcomer_));
        }

        // OnSiblingApplied handlers may mutate sibling Status / Amount (e.g. combo-counter
        // increments). Refresh derived state so the change propagates immediately.
        RefreshAfterUserCallbacks(card);
    }

    internal static Task DispatchAfterSiblingAppliedStacked(
        PlayerChoiceContext? choiceContext,
        CardModel card,
        EnchantmentModel newcomer)
    {
        return MultiEnchantmentSupport.DispatchAfterSiblingAppliedStacked(choiceContext, card, newcomer);
    }

    /// <summary>
    /// Fires <c>OnSiblingRemoved(card, self, leaving, reason)</c> on every active enchantment
    /// on <paramref name="card"/> (except <paramref name="leaving"/> itself). Called from
    /// <c>RemoveAdditionalEnchantmentState</c> BEFORE the enchantment is actually removed from
    /// the list, so handlers see the sibling at its current state.
    /// </summary>
    internal static void DispatchOnSiblingRemoved(CardModel card, EnchantmentModel leaving, RemovalReason reason)
    {
        foreach (EnchantmentModel sibling in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
        {
            if (ReferenceEquals(sibling, leaving)) continue;
            if (!IsActive(card, sibling)) continue;
            InvokeLifecycleWithContext(card, sibling, (leaving, reason),
                static entry => entry.OnSiblingRemoved != null,
                static (entry, ctx, selfCard, self) => entry.RunOnSiblingRemoved(selfCard, self, ctx.leaving, ctx.reason));
        }

        // OnSiblingRemoved handlers may mutate sibling Status / Amount (e.g. combo-counter
        // decrements). Refresh derived state so the change propagates immediately.
        RefreshAfterUserCallbacks(card);
    }

    /// <summary>
    /// Phase 3a T3a.6: fan the OnAfterDamageReceived lifecycle out to every active enchantment
    /// on every card owned by <paramref name="player"/>. Bridges vanilla
    /// <c>Hook.AfterDamageReceived</c>. <see cref="DamageReceivedContext"/> is constructed once
    /// per damage event by the caller (the Harmony patch) so all enchantments see the same
    /// payload — important if a handler wants to compare totals before vs. after the event.
    /// </summary>
    internal static void DispatchOnAfterDamageReceivedForPlayer(Player? player, DamageReceivedContext context)
    {
        if (player?.PlayerCombatState == null)
        {
            return;
        }

        foreach (CardModel card in player.PlayerCombatState.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
        {
            bool anyEnchantments = false;
            foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
            {
                anyEnchantments = true;
                if (!IsActive(card, enchantment))
                {
                    continue;
                }
                InvokeLifecycleWithContext(card, enchantment, context,
                    static entry => entry.OnAfterDamageReceived != null,
                    static (entry, ctx, owner, model) => entry.RunOnAfterDamageReceived(owner, model, ctx));
            }
            if (anyEnchantments) RefreshAfterUserCallbacks(card);
        }
    }

    /// <summary>
    /// Fans out a lifecycle callback across every active enchantment on every card in every
    /// player's combat state. Used by side-turn boundaries and attack events where vanilla
    /// dispatches one hook for the whole table rather than per-card.
    /// </summary>
    private static void DispatchCombatLifecycle<TContext>(
        ICombatState? combatState,
        TContext context,
        Func<EnchantmentEntry, bool> hasHandler,
        Action<EnchantmentEntry, TContext, CardModel, EnchantmentModel> action)
    {
        if (combatState is not CombatState concreteState)
        {
            return;
        }

        foreach (Player player in concreteState.Players.Where(static p => p.IsActiveForHooks && p.PlayerCombatState != null).ToList())
        {
            foreach (CardModel card in player.PlayerCombatState!.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
            {
                bool anyEnchantments = false;
                foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
                {
                    anyEnchantments = true;
                    if (!IsActive(card, enchantment))
                    {
                        continue;
                    }
                    InvokeLifecycleWithContext(card, enchantment, context, hasHandler, action);
                }

                // Side-turn / attack handlers (OnBeforeSideTurnStart, OnSideTurnStart,
                // OnBeforeAttack, OnAfterAttack) commonly mutate Status / Amount / Props to
                // implement "disable on enemy turn", "boost during attack", etc. Refresh
                // derived state per card so keyword caches and DynamicVars reflect the
                // mutation before the next vanilla flush / damage step.
                if (anyEnchantments)
                {
                    RefreshAfterUserCallbacks(card);
                }
            }
        }
    }

    internal static void DispatchOnSideTurnStart(ICombatState? combatState, CombatSide side) =>
        DispatchCombatLifecycle(combatState, side, static e => e.OnSideTurnStart != null, static (e, s, c, m) => e.RunOnSideTurnStart(c, m, s));

    internal static void DispatchOnBeforeSideTurnStart(ICombatState? combatState, CombatSide side) =>
        DispatchCombatLifecycle(combatState, side, static e => e.OnBeforeSideTurnStart != null, static (e, s, c, m) => e.RunOnBeforeSideTurnStart(c, m, s));

    internal static void DispatchOnBeforeAttack(ICombatState? combatState, AttackCommand command) =>
        DispatchCombatLifecycle(combatState, command, static e => e.OnBeforeAttack != null, static (e, cmd, c, m) => e.RunOnBeforeAttack(c, m, cmd));

    internal static void DispatchOnAfterAttack(ICombatState? combatState, AttackCommand command) =>
        DispatchCombatLifecycle(combatState, command, static e => e.OnAfterAttack != null, static (e, cmd, c, m) => e.RunOnAfterAttack(c, m, cmd));

    // === Phase 3c — pile / guard / block bridges ===========================================

    /// <summary>
    /// Per-card dispatch carrying <paramref name="oldPile"/> and <paramref name="source"/> as
    /// loose arguments. Used by <c>OnCardChangedPiles</c> — bundling into a context record was
    /// considered but rejected because authors typically just need (card, oldPile) and the
    /// vanilla virtual already exposes the three args directly.
    /// </summary>
    internal static void DispatchOnCardChangedPilesForCard(CardModel? card, PileType oldPile, AbstractModel? source)
    {
        if (card == null)
        {
            return;
        }

        // Sync active-status predicates before dispatching and gating on IsActive.
        RefreshActiveStatuses(card);

        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
        {
            if (!IsActive(card, enchantment)) continue;
            InvokeLifecycleWithContext(card, enchantment, (oldPile, source),
                static entry => entry.OnCardChangedPiles != null,
                static (entry, ctx, owner, model) => entry.RunOnCardChangedPiles(owner, model, ctx.oldPile, ctx.source));
        }

        // Pile changes can flip active-status predicates (e.g. "active in hand" →
        // discarded). Rebuild derived state and visuals so badges dim/un-dim accordingly.
        RefreshAfterUserCallbacks(card);
    }

    internal static void DispatchOnCardRetainedForCard(CardModel? card) =>
        DispatchCardLifecycle(card, static e => e.OnCardRetained != null, static (e, c, m) => e.RunOnCardRetained(c, m));

    /// <summary>
    /// Block-gained dispatch (before/after). Fans out across every active enchantment on every
    /// card belonging to <see cref="BlockGainContext.Creature"/>'s player. If the creature has
    /// no player (rare — enemy creatures may have null Player in some flows), no-op.
    /// </summary>
    internal static void DispatchOnBeforeBlockGainedForPlayer(BlockGainContext context)
    {
        Player? player = context.Creature?.Player;
        if (player?.PlayerCombatState == null)
        {
            return;
        }
        foreach (CardModel card in player.PlayerCombatState.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
        {
            bool anyEnchantments = false;
            foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
            {
                anyEnchantments = true;
                if (!IsActive(card, enchantment))
                {
                    continue;
                }
                InvokeLifecycleWithContext(card, enchantment, context,
                    static entry => entry.OnBeforeBlockGained != null,
                    static (entry, ctx, owner, model) => entry.RunOnBeforeBlockGained(owner, model, ctx));
            }
            if (anyEnchantments) RefreshAfterUserCallbacks(card);
        }
    }

    internal static void DispatchOnBlockGainedForPlayer(BlockGainContext context)
    {
        Player? player = context.Creature?.Player;
        if (player?.PlayerCombatState == null)
        {
            return;
        }
        foreach (CardModel card in player.PlayerCombatState.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
        {
            bool anyEnchantments = false;
            foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
            {
                anyEnchantments = true;
                if (!IsActive(card, enchantment))
                {
                    continue;
                }
                InvokeLifecycleWithContext(card, enchantment, context,
                    static entry => entry.OnBlockGained != null,
                    static (entry, ctx, owner, model) => entry.RunOnBlockGained(owner, model, ctx));
            }
            if (anyEnchantments) RefreshAfterUserCallbacks(card);
        }
    }

    /// <summary>
    /// Guard-hook dispatch for <c>Hook.ShouldDie</c>. Iterates every active enchantment on every
    /// card belonging to <paramref name="creature"/>'s player; returns <c>false</c> if ANY
    /// handler returns <c>false</c> (matching vanilla — any veto prevents death). When no
    /// player is associated (rare), no enchantments can veto and we return <c>true</c>.
    /// Per-enchantment exceptions are swallowed with a warn log and treated as "no objection"
    /// so a bug in one handler can't accidentally veto every death.
    /// </summary>
    internal static bool DispatchOnShouldDieForCreature(Creature creature, out AbstractModel? preventer)
    {
        preventer = null;
        Player? player = creature?.Player;
        if (player?.PlayerCombatState == null)
        {
            return true;
        }
        bool shouldDie = true;
        foreach (CardModel card in player.PlayerCombatState.AllCards.Where(static c => !c.HasBeenRemovedFromState).ToList())
        {
            foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetGameplayEnchantments(card).ToList())
            {
                if (!IsActive(card, enchantment))
                {
                    continue;
                }
                EnchantmentEntry? entry = EnchantmentRegistry.GetDefinitionEntry(enchantment.GetType());
                if (entry == null)
                {
                    continue;
                }

                if (!entry.RunOnShouldDie(card, enchantment, creature!))
                {
                    shouldDie = false;
                    preventer ??= enchantment;
                    // Do not break: let every veto handler observe the event for symmetry with vanilla.
                }
            }
        }
        return shouldDie;
    }

    /// <summary>
    /// Variant of <see cref="InvokeLifecycle"/> that threads an arbitrary context object through
    /// to the dispatch lambda. Used by callbacks whose vanilla counterpart carries non-trivial
    /// payload (DamageReceived, BeforeAttack, AfterAttack — Phase 3b/c extends this). Exception
    /// handling matches <see cref="InvokeLifecycle"/>: per-enchantment try/catch with warn log,
    /// so one author's bug doesn't kill the combat tick.
    /// </summary>
    private static void InvokeLifecycleWithContext<TContext>(
        CardModel card,
        EnchantmentModel enchantment,
        TContext context,
        Func<EnchantmentEntry, bool> hasHandler,
        Action<EnchantmentEntry, TContext, CardModel, EnchantmentModel> action)
    {
        EnchantmentEntry? entry = EnchantmentRegistry.GetDefinitionEntry(enchantment.GetType(), hasHandler);
        if (entry == null)
        {
            return;
        }

        action(entry, context, card, enchantment);
    }

    internal static bool IsActive(CardModel card, EnchantmentModel enchantment)
    {
        try
        {
            // WhenActiveStatus predicate gates dispatch AND controls Status.
            // Manual Status = Disabled (without WhenActiveStatus) does NOT block dispatch —
            // it only affects visuals / ActiveInstanceCount.
            EnchantmentEntry? entry = EnchantmentRegistry.GetDefinitionEntry(enchantment.GetType());
            if (entry is { HasActiveStatusPredicate: true })
            {
                return entry.ShouldBeActive(card, enchantment);
            }

            // Legacy ConditionalActiveScope path.
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
                $"[MultiEnchantment][Scope] Active predicate failed for {enchantment.Id} on {card.Id}: {ex}");
            return true;
        }
    }

    /// <summary>
    /// Evaluates the <see cref="IEnchantmentRegistration.WhenActive"/> /
    /// <see cref="EnchantmentDefinition{TEnchantment}.ShouldBeActive"/> predicate (if any) for
    /// a single enchantment and syncs its <c>Status</c>. Returns <c>true</c> when the status
    /// changed.
    /// </summary>
    internal static bool RefreshActiveStatus(CardModel card, EnchantmentModel enchantment)
    {
        // Reentrancy guard: setting Status can trigger StatusChanged which may re-enter refresh.
        _activeStatusRefreshStack ??= new HashSet<EnchantmentModel>(ReferenceEqualityComparer.Instance);
        if (!_activeStatusRefreshStack.Add(enchantment))
        {
            return false;
        }

        try
        {
            EnchantmentEntry? entry = EnchantmentRegistry.GetDefinitionEntry(enchantment.GetType());
            if (entry is not { HasActiveStatusPredicate: true })
            {
                return false;
            }

            bool active = entry.ShouldBeActive(card, enchantment);

            EnchantmentStatus target = active ? EnchantmentStatus.Normal : EnchantmentStatus.Disabled;
            if (enchantment.Status == target)
            {
                return false;
            }

            enchantment.Status = target;
            return true;
        }
        finally
        {
            _activeStatusRefreshStack.Remove(enchantment);
        }
    }

    /// <summary>
    /// Evaluates active-status predicates for every enchantment on <paramref name="card"/>, including
    /// extra-icon markers. This is a <em>visual</em> status sync (it only flips <c>Status</c> for a
    /// type that registered a <c>WhenActive</c>/<c>ShouldBeActive</c> predicate, which in turn drives
    /// <see cref="EnchantmentPresentationStyle.HideWhenDisabled"/>) — not a gameplay lifecycle hook —
    /// so markers must participate or a registered marker's HideWhenDisabled would never fire.
    /// </summary>
    internal static void RefreshActiveStatuses(CardModel card)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card).ToList())
        {
            RefreshActiveStatus(card, enchantment);
        }
    }

    /// <summary>
    /// Removes an enchantment from a card, invoking the <c>OnRemoved</c> veto hook unless
    /// <paramref name="reason"/> is <see cref="RemovalReason.CardCleared"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Safe to call from lifecycle callbacks</b> (<c>OnApplied</c>, <c>OnSiblingApplied</c>,
    /// <c>OnRemoved</c>, <c>OnCombatStart</c>, etc.). Iteration over the enchantments list uses
    /// <c>.ToList()</c> snapshots, and the removal itself is a direct
    /// <c>List&lt;T&gt;.Remove</c> on the backing store — no iterator is active on the same list
    /// at the point of removal. Queued-removal paths (scope-driven turn-end / combat-end) flush
    /// after all iteration completes.</para>
    /// </remarks>
    internal static bool RemoveEnchantmentWithReason(CardModel card, EnchantmentModel enchantment, RemovalReason reason)
    {
        return MultiEnchantmentSupport.RemoveEnchantmentInternal(card, enchantment, reason, bypassVeto: reason == RemovalReason.CardCleared);
    }

    private static void HandleCombatEnd(CardModel card)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetOrderedEnchantmentsForRemoval(card))
        {
            InvokeLifecycle(card, enchantment, static entry => entry.OnCombatEnd != null, static (entry, owner, model) => entry.RunOnCombatEnd(owner, model));
        }
    }

    private static void HandleTurnEnd(CardModel card, RemovalReason removalReason)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetOrderedEnchantmentsForRemoval(card))
        {
            ScopeRuntimeState state = EnsureScopeState(card, enchantment);
            InvokeLifecycle(card, enchantment, static entry => entry.OnTurnEnd != null, static (entry, owner, model) => entry.RunOnTurnEnd(owner, model));
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
        return EnchantmentRegistry.GetDefinitionEntry(enchantment.GetType(), static entry => entry.GetScope != null)?.GetSafeScope() ?? EnchantmentScope.Permanent;
    }

    private static void InvokeLifecycle(
        CardModel card,
        EnchantmentModel enchantment,
        Func<EnchantmentEntry, bool> hasHandler,
        Action<EnchantmentEntry, CardModel, EnchantmentModel> action)
    {
        EnchantmentEntry? entry = EnchantmentRegistry.GetDefinitionEntry(enchantment.GetType(), hasHandler);
        if (entry == null)
        {
            return;
        }

        action(entry, card, enchantment);
    }

    private static IEnumerable<CardModel> EnumerateCombatCards(ICombatState? combatState, bool includeDeckVersions)
    {
        if (combatState is not CombatState concreteState)
        {
            yield break;
        }

        HashSet<CardModel> seen = new(ReferenceEqualityComparer.Instance);
        foreach (Player player in concreteState.Players.Where(static player => player.IsActiveForHooks && player.PlayerCombatState != null).ToList())
        {
            // Snapshot AllCards: callers like OnCombatEnded / OnPlayerTurnEnded invoke user
            // OnTurnEnd / OnCombatEnd handlers in the loop body. A handler that calls
            // CardCmd.AddCardToHand or any other mutation on AllCards would otherwise crash
            // this lazy yield-return enumerator with "Collection modified during enumeration".
            foreach (CardModel card in player.PlayerCombatState!.AllCards.Where(static card => !card.HasBeenRemovedFromState).ToList())
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
    // ScopeRuntimeState (effective Scope / optional per-instance OverrideScope / counters) lives
    // in-memory on CardEnchantmentState.ScopeStates. Without explicit serialization,
    // MaxActivations counters and LingerForTurns countdowns reset to defaults on save/load and
    // never synchronize across multiplayer peers. The registry scope remains the default source of
    // truth; predicate-free per-instance overrides are persisted as scope kind + parameters.
    // Predicate-bearing ConditionalActive/RemoveWhen scopes are intentionally rejected for
    // overrides because their Func<> payloads cannot be serialized safely. We capture the state at
    // the EnchantmentModel.ToSerializable boundary (called from EnchantmentToSerializablePostfix)
    // and lazy-restore the first time a card + enchantment pair appears in EnsureScopeState on the
    // receiving side.

    internal const string ScopeStateSavePropertyName = "MultiEnchantmentScopeData";

    // Set by TryRestoreScopeStateFromProps when a save-time scope kind was tagged; consumed by
    // EnsureScopeState (upper level) after ResolveScope so the warning fires against the freshly
    // resolved Scope. The entry is removed after the first comparison; subsequent EnsureScopeState
    // calls for the same enchantment don't re-log.
    private static readonly object PendingScopeKindMismatchCheckSync = new();
    private static readonly ConditionalWeakTable<EnchantmentModel, PendingScopeKindMismatchEntry>
        PendingScopeKindMismatchCheck = new();

    private sealed class PendingScopeKindMismatchEntry
    {
        public required string ScopeKind { get; init; }
    }

    private sealed record ScopeStatePayload(
        [property: JsonPropertyName("activation_count")] int ActivationCount,
        [property: JsonPropertyName("turns_remaining")] int TurnsRemaining,
        // Recorded as a sanity tag: the effective scope kind that produced these counters. On load,
        // if the kind no longer matches the current effective scope (override or registry), we still
        // restore the counter values, but log a warning so silent semantic drift is visible.
        [property: JsonPropertyName("scope_kind")] string? ScopeKind = null,
        [property: JsonPropertyName("override_scope_kind")] string? OverrideScopeKind = null,
        [property: JsonPropertyName("override_param_int")] int? OverrideParamInt = null,
        [property: JsonPropertyName("override_param_trigger")] string? OverrideParamTrigger = null);

    private static string GetScopeKind(EnchantmentScope scope) => scope.GetType().Name;

    private static ScopeStatePayload BuildScopeStatePayload(ScopeRuntimeState state)
    {
        string? overrideKind = null;
        int? overrideParamInt = null;
        string? overrideParamTrigger = null;

        if (state.OverrideScope != null)
        {
            overrideKind = GetScopeKind(state.OverrideScope);
            if (state.OverrideScope is EnchantmentScope.LingerForTurnsScope linger)
            {
                overrideParamInt = linger.Turns;
            }
            else if (state.OverrideScope is EnchantmentScope.MaxActivationsScope max)
            {
                overrideParamInt = max.Max;
                overrideParamTrigger = max.Trigger.Name;
            }
        }

        return new ScopeStatePayload(
            state.ActivationCount,
            state.TurnsRemaining,
            GetScopeKind(state.Scope),
            overrideKind,
            overrideParamInt,
            overrideParamTrigger);
    }

    private static EnchantmentScope? DecodeScopeOverride(ScopeStatePayload payload, EnchantmentModel enchantment)
    {
        return payload.OverrideScopeKind switch
        {
            null or "" => null,
            nameof(EnchantmentScope.PermanentScope) => EnchantmentScope.Permanent,
            nameof(EnchantmentScope.UntilCombatEndsScope) => EnchantmentScope.UntilCombatEnds,
            nameof(EnchantmentScope.UntilTurnEndsScope) => EnchantmentScope.UntilTurnEnds,
            nameof(EnchantmentScope.LingerForTurnsScope) => DecodeLingerForTurnsOverride(payload.OverrideParamInt, enchantment),
            nameof(EnchantmentScope.MaxActivationsScope) => DecodeMaxActivationsOverride(
                payload.OverrideParamInt,
                payload.OverrideParamTrigger,
                enchantment),
            _ => WarnUnknownScopeOverride(payload.OverrideScopeKind!, enchantment),
        };
    }

    private static EnchantmentScope? WarnUnknownScopeOverride(string kind, EnchantmentModel enchantment)
    {
        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment][Scope] Ignoring unknown scope override kind '{kind}' for {enchantment.Id}.");
        return null;
    }

    private static EnchantmentScope? DecodeLingerForTurnsOverride(int? turns, EnchantmentModel enchantment)
    {
        if (turns is not > 0)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][Scope] Ignoring invalid LingerForTurns override for {enchantment.Id}: turns={turns?.ToString() ?? "<missing>"}.");
            return null;
        }

        return EnchantmentScope.LingerForTurns(turns.Value);
    }

    private static EnchantmentScope? DecodeMaxActivationsOverride(
        int? max,
        string? trigger,
        EnchantmentModel enchantment)
    {
        if (max is not > 0)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][Scope] Ignoring invalid MaxActivations override for {enchantment.Id}: max={max?.ToString() ?? "<missing>"}.");
            return null;
        }

        return EnchantmentScope.MaxActivations(max.Value, DecodeActivationTrigger(trigger, enchantment));
    }

    private static ActivationTrigger DecodeActivationTrigger(string? name, EnchantmentModel enchantment)
    {
        if (string.IsNullOrEmpty(name))
        {
            return ActivationTrigger.OnPlay;
        }

        return name switch
        {
            nameof(ActivationTrigger.OnPlay) => ActivationTrigger.OnPlay,
            nameof(ActivationTrigger.AfterCardPlayed) => ActivationTrigger.AfterCardPlayed,
            nameof(ActivationTrigger.AfterCardDrawn) => ActivationTrigger.AfterCardDrawn,
            nameof(ActivationTrigger.AfterCardExhausted) => ActivationTrigger.AfterCardExhausted,
            nameof(ActivationTrigger.AfterCardDiscarded) => ActivationTrigger.AfterCardDiscarded,
            nameof(ActivationTrigger.AfterPlayerTurnStart) => ActivationTrigger.AfterPlayerTurnStart,
            nameof(ActivationTrigger.AfterPlayerTurnEnd) => ActivationTrigger.AfterPlayerTurnEnd,
            nameof(ActivationTrigger.AfterDamageReceived) => ActivationTrigger.AfterDamageReceived,
            _ when name.StartsWith("Custom:", StringComparison.Ordinal) => ActivationTrigger.Custom(name["Custom:".Length..]),
            _ => WarnUnknownActivationTrigger(name, enchantment),
        };
    }

    private static ActivationTrigger WarnUnknownActivationTrigger(string name, EnchantmentModel enchantment)
    {
        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment][Scope] Unknown activation trigger '{name}' in scope override for {enchantment.Id}; falling back to OnPlay.");
        return ActivationTrigger.OnPlay;
    }

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
            state.OverrideScope = DecodeScopeOverride(payload, enchantment);

            // Stash the saved scope kind so the upper-level EnsureScopeState — which knows the
            // freshly-resolved Scope — can warn on cross-session drift. We can't compare here
            // because state.Scope is still the default at this layer.
            if (!string.IsNullOrEmpty(payload.ScopeKind))
            {
                SetPendingScopeKindMismatchCheck(enchantment, payload.ScopeKind!);
            }

            return true;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][Scope] Failed to deserialize scope state for {enchantment.Id}: {ex}");
            return false;
        }
    }

    private static void SetPendingScopeKindMismatchCheck(EnchantmentModel enchantment, string scopeKind)
    {
        lock (PendingScopeKindMismatchCheckSync)
        {
            PendingScopeKindMismatchCheck.Remove(enchantment);
            PendingScopeKindMismatchCheck.Add(enchantment, new PendingScopeKindMismatchEntry { ScopeKind = scopeKind });
        }
    }

    private static bool TryConsumePendingScopeKindMismatchCheck(
        EnchantmentModel enchantment,
        [NotNullWhen(true)] out string? scopeKind)
    {
        lock (PendingScopeKindMismatchCheckSync)
        {
            if (PendingScopeKindMismatchCheck.TryGetValue(enchantment, out PendingScopeKindMismatchEntry? entry))
            {
                PendingScopeKindMismatchCheck.Remove(enchantment);
                scopeKind = entry.ScopeKind;
                return true;
            }
        }

        scopeKind = null;
        return false;
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

        if (state.ActivationCount == 0 && state.TurnsRemaining == 0 && state.OverrideScope == null)
        {
            // Default / fresh state. Skip the property entirely so existing-receiver upgrades
            // don't accumulate empty Props.strings entries on permanent enchants.
            RemoveScopeStatePropertyFromSave(ref save);
            return;
        }

        ScopeStatePayload payload = BuildScopeStatePayload(state);
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

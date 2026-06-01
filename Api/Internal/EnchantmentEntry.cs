using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;
using LegacyStackDefinition = MultiEnchantmentMod.EnchantmentStackDefinition;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;
using EnchantmentVisualSlice = MultiEnchantmentMod.EnchantmentVisualSlice;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// In-memory representation of one v2 registration. Built up by <see cref="EnchantmentRegistration"/>
/// during fluent <c>.Stack(...).OnMergedDelta(...).Commit()</c> chains, then read directly by
/// runtime dispatchers.
/// </summary>
internal sealed class EnchantmentEntry
{
    public required Type EnchantmentType { get; init; }
    public StackDefinition? Definition { get; set; }
    public LegacyExecutionPolicy? ExecutionPolicy { get; set; }
    public Action<EnchantmentModel, int>? OnMergedDelta { get; set; }
    public Action<EnchantmentModel>? OnMergedRefresh { get; set; }
    public List<KeywordContribution> Keywords { get; } = new();
    public List<DynamicVarContribution> DynamicVarContributions { get; } = new();
    public List<EnergyCostContribution> EnergyCostContributions { get; } = new();
    public List<CardPlayCountContribution> CardPlayCountContributions { get; } = new();
    public PresentationTextFormatter? FormatExtraText { get; set; }
    public Func<EnchantmentStackSnapshot, IReadOnlyList<int>?>? GetVisualSliceAmounts { get; set; }
    public Func<EnchantmentStackSnapshot, IReadOnlyList<EnchantmentVisualSlice>?>? GetVisualSlices { get; set; }
    public EnchantmentPresentationStyle? PresentationStyle { get; set; }
    public Func<EnchantmentScope>? GetScope { get; set; }

    public HistoryDisplayMode HistoryDisplay { get; set; } = HistoryDisplayMode.Auto;
    public string? HistoryGroupHeader { get; set; }
    public HistoryTextFormatter? HistoryTextFormatter { get; set; }

    /// <summary>
    /// When non-null, <see cref="MultiEnchantmentScopeSupport"/> will call this predicate,
    /// set <c>enchantment.Status = Normal</c> when it returns <c>true</c>, and set
    /// <c>enchantment.Status = Disabled</c> when it returns <c>false</c>. Set by
    /// <see cref="IEnchantmentRegistration.WhenActive"/> and
    /// <see cref="EnchantmentDefinition{TEnchantment}.ShouldBeActive"/>. This predicate does
    /// not occupy <see cref="EnchantmentScope"/>, so it composes naturally with
    /// <see cref="WithScope"/> / <see cref="LingerForTurns"/> / <see cref="MaxActivations"/> /
    /// <see cref="RemoveWhen"/>.
    /// </summary>
    public Func<CardModel, EnchantmentModel, bool>? GetActiveStatus { get; set; }
    public Action<CardModel, EnchantmentModel>? OnApplied { get; set; }
    public Func<CardModel, EnchantmentModel, RemovalReason, bool>? OnRemoved { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCombatStart { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCombatEnd { get; set; }
    public Action<CardModel, EnchantmentModel>? OnTurnStart { get; set; }
    public Action<CardModel, EnchantmentModel>? OnTurnEnd { get; set; }
    public Action<CardModel, EnchantmentModel>? OnRestored { get; set; }

    // Phase 3a — vanilla card-event hook bridges. Each callback is dispatched only for
    // enchantments that pass MultiEnchantmentScopeSupport.IsActive at the moment the event fires,
    // matching the gating already applied to OnPlay / damage / block pipelines (Phase 1).
    public Action<CardModel, EnchantmentModel>? OnCardPlayed { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardDrawn { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardExhausted { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardDiscarded { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardEnteredCombat { get; set; }

    /// <summary>
    /// Phase 3a T3a.6: bridge to vanilla <c>Hook.AfterDamageReceived</c>. Dispatched to every
    /// active enchantment whose owning card belongs to the target player, with a context bundle
    /// covering target / result / dealer / source. Inactive enchantments are skipped.
    /// </summary>
    public Action<CardModel, EnchantmentModel, DamageReceivedContext>? OnAfterDamageReceived { get; set; }

    // Phase 3b — combat-flow bridges. Each fans out across every active enchantment on every
    // card in both players' PlayerCombatState so authors can react to side-turn boundaries and
    // attack events regardless of which side owns the card.
    public Action<CardModel, EnchantmentModel, CombatSide>? OnSideTurnStart { get; set; }
    public Action<CardModel, EnchantmentModel, CombatSide>? OnBeforeSideTurnStart { get; set; }
    public Action<CardModel, EnchantmentModel, AttackCommand>? OnBeforeAttack { get; set; }
    public Action<CardModel, EnchantmentModel, AttackCommand>? OnAfterAttack { get; set; }

    // Phase 3c — pile / guard / block bridges. OnShouldDie carries a return value (false vetoes
    // death); the rest are void.
    public Action<CardModel, EnchantmentModel, PileType, AbstractModel?>? OnCardChangedPiles { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardRetained { get; set; }
    public Action<CardModel, EnchantmentModel, BlockGainContext>? OnBeforeBlockGained { get; set; }
    public Action<CardModel, EnchantmentModel, BlockGainContext>? OnBlockGained { get; set; }
    public Func<CardModel, EnchantmentModel, Creature, bool>? OnShouldDie { get; set; }

    // Phase 4 — broadcast card-event hooks. Unlike the per-card OnCardPlayed / OnCardDrawn /
    // OnCardExhausted / OnCardDiscarded (which only fire for the card carrying the enchantment),
    // these fire for EVERY card event in combat. Opt-in: dispatch only resolves entries that
    // register these hooks, so other enchantments pay no handler cost. Parameters are
    // (playedCard, selfCard, selfEnchantment) — playedCard first so the event subject is prominent.
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardPlayed { get; set; }
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardDrawn { get; set; }
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardExhausted { get; set; }
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardDiscarded { get; set; }

    // Phase 5 — sibling lifecycle hooks. Fires when another enchantment is attached to or
    // removed from the same card. Parameters: (selfCard, selfEnchantment, siblingEnchantment).
    // OnSiblingRemoved additionally carries the RemovalReason.
    public Action<CardModel, EnchantmentModel, EnchantmentModel>? OnSiblingApplied { get; set; }
    public Action<CardModel, EnchantmentModel, EnchantmentModel, RemovalReason>? OnSiblingRemoved { get; set; }

    // Stack-aware async hooks. These are invoked once per enchantment type with a full
    // EnchantmentStackSnapshot so authors can aggregate prompts, random rolls, animations, and
    // numeric amounts deliberately instead of relying on legacy "call the whole hook N times".
    public StackedOnPlayHandler? OnPlayStacked { get; set; }
    public StackedBeforeCardPlayedHandler? BeforeCardPlayedStacked { get; set; }
    public StackedAfterCardPlayedHandler? AfterCardPlayedStacked { get; set; }
    public StackedAfterSiblingAppliedHandler? AfterSiblingAppliedStacked { get; set; }
    public StackedAfterCardDrawnHandler? AfterCardDrawnStacked { get; set; }
    public StackedAfterAnyCardDrawnHandler? AfterAnyCardDrawnStacked { get; set; }
    public StackedBeforeFlushHandler? BeforeFlushStacked { get; set; }
    public StackedAfterDamageGivenHandler? AfterDamageGivenStacked { get; set; }

    public LegacyStackDefinition GetDefinition() =>
        (Definition ?? StackDefinition.Default).ToLegacy();

    public LegacyExecutionPolicy GetExecutionPolicy() =>
        ExecutionPolicy ?? new LegacyExecutionPolicy();

    public void ApplyMergedAmountDelta(EnchantmentModel enchantment, int addedAmount)
    {
        if (OnMergedDelta == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnMergedDelta),
            () => OnMergedDelta!(enchantment, addedAmount));
    }

    public void RefreshMergedState(EnchantmentModel enchantment)
    {
        if (OnMergedRefresh != null)
        {
            SafeInvoker.Run(EnchantmentType, nameof(OnMergedRefresh),
                () => OnMergedRefresh!(enchantment));
            return;
        }

        enchantment.RecalculateValues();
        enchantment.Card?.DynamicVars.RecalculateForUpgradeOrEnchant();
    }

    public IEnumerable<CardKeyword> GetTrackedKeywords()
    {
        return Keywords.Select(contribution => contribution.Keyword).Distinct();
    }

    public int GetKeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword)
    {
        int total = 0;
        foreach (KeywordContribution contribution in Keywords)
        {
            if (contribution.Keyword != keyword)
            {
                continue;
            }

            total += SafeInvoker.Run(
                EnchantmentType,
                $"TrackKeyword({keyword})",
                () => contribution.AmountFn(snapshot),
                fallback: 0);
        }

        return total;
    }

    public IReadOnlyList<int>? GetSafeVisualSliceAmounts(EnchantmentStackSnapshot snapshot)
    {
        if (GetVisualSliceAmounts == null) return null;
        return SafeInvoker.Run(
            EnchantmentType,
            nameof(GetVisualSliceAmounts),
            () => GetVisualSliceAmounts!(snapshot),
            fallback: null);
    }

    public IReadOnlyList<EnchantmentVisualSlice>? GetSafeVisualSlices(EnchantmentStackSnapshot snapshot)
    {
        if (GetVisualSlices == null) return null;
        return SafeInvoker.Run(
            EnchantmentType,
            nameof(GetVisualSlices),
            () => GetVisualSlices!(snapshot),
            fallback: null);
    }

    public bool TryFormatExtraCardText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText)
    {
        if (FormatExtraText == null)
        {
            formattedText = defaultText;
            return false;
        }

        string captured = defaultText;
        bool handled = SafeInvoker.Run(
            EnchantmentType,
            nameof(FormatExtraText),
            () =>
            {
                bool ok = FormatExtraText!(snapshot, defaultText, out string result);
                captured = result;
                return ok;
            },
            fallback: false);

        formattedText = handled ? captured : defaultText;
        return handled;
    }

    public decimal ModifyEnergyCostInCombat(EnchantmentStackSnapshot snapshot, decimal currentCost)
    {
        decimal result = currentCost;
        foreach (EnergyCostContribution contribution in EnergyCostContributions)
        {
            result = SafeInvoker.Run(
                EnchantmentType,
                nameof(EnergyCostContributions),
                () => contribution(snapshot, result),
                fallback: result);
        }

        return result;
    }

    public int ModifyCardPlayCount(EnchantmentStackSnapshot snapshot, int currentPlayCount)
    {
        int result = currentPlayCount;
        foreach (CardPlayCountContribution contribution in CardPlayCountContributions)
        {
            result = SafeInvoker.Run(
                EnchantmentType,
                nameof(CardPlayCountContributions),
                () => contribution(snapshot, result),
                fallback: result);
        }

        return result;
    }

    public Task RunOnPlayStacked(StackedOnPlayContext context) =>
        OnPlayStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(OnPlayStacked), c => OnPlayStacked(c), context);

    public Task RunBeforeCardPlayedStacked(StackedBeforeCardPlayedContext context) =>
        BeforeCardPlayedStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(BeforeCardPlayedStacked), c => BeforeCardPlayedStacked(c), context);

    public Task RunAfterCardPlayedStacked(StackedAfterCardPlayedContext context) =>
        AfterCardPlayedStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(AfterCardPlayedStacked), c => AfterCardPlayedStacked(c), context);

    public Task RunAfterSiblingAppliedStacked(StackedAfterSiblingAppliedContext context) =>
        AfterSiblingAppliedStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(AfterSiblingAppliedStacked), c => AfterSiblingAppliedStacked(c), context);

    public Task RunAfterCardDrawnStacked(StackedAfterCardDrawnContext context) =>
        AfterCardDrawnStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(AfterCardDrawnStacked), c => AfterCardDrawnStacked(c), context);

    public Task RunAfterAnyCardDrawnStacked(StackedAfterCardDrawnContext context) =>
        AfterAnyCardDrawnStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(AfterAnyCardDrawnStacked), c => AfterAnyCardDrawnStacked(c), context);

    public Task RunBeforeFlushStacked(StackedBeforeFlushContext context) =>
        BeforeFlushStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(BeforeFlushStacked), c => BeforeFlushStacked(c), context);

    public Task RunAfterDamageGivenStacked(StackedAfterDamageGivenContext context) =>
        AfterDamageGivenStacked == null
            ? Task.CompletedTask
            : RunAsync(nameof(AfterDamageGivenStacked), c => AfterDamageGivenStacked(c), context);

    public EnchantmentScope GetSafeScope() =>
        GetScope == null
            ? EnchantmentScope.Permanent
            : SafeInvoker.Run(
                EnchantmentType,
                nameof(GetScope),
                () => GetScope!(),
                fallback: EnchantmentScope.Permanent);

    public bool HasActiveStatusPredicate => GetActiveStatus != null;

    /// <summary>
    /// True when this entry sets any field that defines the enchantment's behavior — stack
    /// definition, execution policy, scope, active-status predicate, merge response, or any
    /// lifecycle / vanilla-bridge / stacked-hook callback. Each enchantment type is allowed at
    /// most one Definition entry; later <c>Register&lt;T&gt;()</c> calls may only add
    /// Contribution-only entries (dynamic-var / energy / play-count / keyword / presentation).
    /// See <see cref="EnchantmentRegistry.Install{T}"/> for the enforcement point.
    /// </summary>
    public bool IsDefinitionEntry =>
        Definition != null
        || ExecutionPolicy != null
        || GetScope != null
        || GetActiveStatus != null
        || OnMergedDelta != null
        || OnMergedRefresh != null
        || OnApplied != null
        || OnRemoved != null
        || OnCombatStart != null
        || OnCombatEnd != null
        || OnTurnStart != null
        || OnTurnEnd != null
        || OnRestored != null
        || OnCardPlayed != null
        || OnCardDrawn != null
        || OnCardExhausted != null
        || OnCardDiscarded != null
        || OnCardEnteredCombat != null
        || OnAfterDamageReceived != null
        || OnSideTurnStart != null
        || OnBeforeSideTurnStart != null
        || OnBeforeAttack != null
        || OnAfterAttack != null
        || OnCardChangedPiles != null
        || OnCardRetained != null
        || OnBeforeBlockGained != null
        || OnBlockGained != null
        || OnShouldDie != null
        || OnAnyCardPlayed != null
        || OnAnyCardDrawn != null
        || OnAnyCardExhausted != null
        || OnAnyCardDiscarded != null
        || OnSiblingApplied != null
        || OnSiblingRemoved != null
        || OnPlayStacked != null
        || BeforeCardPlayedStacked != null
        || AfterCardPlayedStacked != null
        || AfterSiblingAppliedStacked != null
        || AfterCardDrawnStacked != null
        || AfterAnyCardDrawnStacked != null
        || BeforeFlushStacked != null
        || AfterDamageGivenStacked != null;

    public bool ShouldBeActive(CardModel card, EnchantmentModel enchantment)
    {
        if (GetActiveStatus == null) return true;
        return SafeInvoker.Run(
            EnchantmentType,
            nameof(GetActiveStatus),
            () => GetActiveStatus!(card, enchantment),
            fallback: true);
    }

    public void RunOnApplied(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnApplied), OnApplied, card, enchantment);

    public bool RunOnRemoved(CardModel card, EnchantmentModel enchantment, RemovalReason reason)
    {
        if (OnRemoved == null) return true;
        return SafeInvoker.Run(
            EnchantmentType,
            nameof(OnRemoved),
            () => OnRemoved!(card, enchantment, reason),
            fallback: true);
    }

    public void RunOnCombatStart(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCombatStart), OnCombatStart, card, enchantment);

    public void RunOnCombatEnd(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCombatEnd), OnCombatEnd, card, enchantment);

    public void RunOnTurnStart(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnTurnStart), OnTurnStart, card, enchantment);

    public void RunOnTurnEnd(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnTurnEnd), OnTurnEnd, card, enchantment);

    public void RunOnRestored(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnRestored), OnRestored, card, enchantment);

    public void RunOnCardPlayed(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCardPlayed), OnCardPlayed, card, enchantment);

    public void RunOnCardDrawn(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCardDrawn), OnCardDrawn, card, enchantment);

    public void RunOnCardExhausted(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCardExhausted), OnCardExhausted, card, enchantment);

    public void RunOnCardDiscarded(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCardDiscarded), OnCardDiscarded, card, enchantment);

    public void RunOnCardEnteredCombat(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCardEnteredCombat), OnCardEnteredCombat, card, enchantment);

    public void RunOnAfterDamageReceived(CardModel card, EnchantmentModel enchantment, DamageReceivedContext context)
    {
        if (OnAfterDamageReceived == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnAfterDamageReceived),
            () => OnAfterDamageReceived!(card, enchantment, context));
    }

    public void RunOnSideTurnStart(CardModel card, EnchantmentModel enchantment, CombatSide side)
    {
        if (OnSideTurnStart == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnSideTurnStart),
            () => OnSideTurnStart!(card, enchantment, side));
    }

    public void RunOnBeforeSideTurnStart(CardModel card, EnchantmentModel enchantment, CombatSide side)
    {
        if (OnBeforeSideTurnStart == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnBeforeSideTurnStart),
            () => OnBeforeSideTurnStart!(card, enchantment, side));
    }

    public void RunOnBeforeAttack(CardModel card, EnchantmentModel enchantment, AttackCommand command)
    {
        if (OnBeforeAttack == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnBeforeAttack),
            () => OnBeforeAttack!(card, enchantment, command));
    }

    public void RunOnAfterAttack(CardModel card, EnchantmentModel enchantment, AttackCommand command)
    {
        if (OnAfterAttack == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnAfterAttack),
            () => OnAfterAttack!(card, enchantment, command));
    }

    public void RunOnCardChangedPiles(CardModel card, EnchantmentModel enchantment, PileType oldPile, AbstractModel? source)
    {
        if (OnCardChangedPiles == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnCardChangedPiles),
            () => OnCardChangedPiles!(card, enchantment, oldPile, source));
    }

    public void RunOnCardRetained(CardModel card, EnchantmentModel enchantment) =>
        RunLifecycle(nameof(OnCardRetained), OnCardRetained, card, enchantment);

    public void RunOnBeforeBlockGained(CardModel card, EnchantmentModel enchantment, BlockGainContext context)
    {
        if (OnBeforeBlockGained == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnBeforeBlockGained),
            () => OnBeforeBlockGained!(card, enchantment, context));
    }

    public void RunOnBlockGained(CardModel card, EnchantmentModel enchantment, BlockGainContext context)
    {
        if (OnBlockGained == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnBlockGained),
            () => OnBlockGained!(card, enchantment, context));
    }

    public bool RunOnShouldDie(CardModel card, EnchantmentModel enchantment, Creature creature)
    {
        if (OnShouldDie == null) return true;
        return SafeInvoker.Run(
            EnchantmentType,
            nameof(OnShouldDie),
            () => OnShouldDie!(card, enchantment, creature),
            fallback: true);
    }

    public void RunOnAnyCardPlayed(CardModel playedCard, CardModel selfCard, EnchantmentModel enchantment)
    {
        if (OnAnyCardPlayed == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnAnyCardPlayed),
            () => OnAnyCardPlayed!(playedCard, selfCard, enchantment));
    }

    public void RunOnAnyCardDrawn(CardModel drawnCard, CardModel selfCard, EnchantmentModel enchantment)
    {
        if (OnAnyCardDrawn == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnAnyCardDrawn),
            () => OnAnyCardDrawn!(drawnCard, selfCard, enchantment));
    }

    public void RunOnAnyCardExhausted(CardModel exhaustedCard, CardModel selfCard, EnchantmentModel enchantment)
    {
        if (OnAnyCardExhausted == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnAnyCardExhausted),
            () => OnAnyCardExhausted!(exhaustedCard, selfCard, enchantment));
    }

    public void RunOnAnyCardDiscarded(CardModel discardedCard, CardModel selfCard, EnchantmentModel enchantment)
    {
        if (OnAnyCardDiscarded == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnAnyCardDiscarded),
            () => OnAnyCardDiscarded!(discardedCard, selfCard, enchantment));
    }

    public void RunOnSiblingApplied(CardModel card, EnchantmentModel self, EnchantmentModel newSibling)
    {
        if (OnSiblingApplied == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnSiblingApplied),
            () => OnSiblingApplied!(card, self, newSibling));
    }

    public void RunOnSiblingRemoved(CardModel card, EnchantmentModel self, EnchantmentModel removedSibling, RemovalReason reason)
    {
        if (OnSiblingRemoved == null) return;
        SafeInvoker.Run(EnchantmentType, nameof(OnSiblingRemoved),
            () => OnSiblingRemoved!(card, self, removedSibling, reason));
    }

    private void RunLifecycle(
        string hookName,
        Action<CardModel, EnchantmentModel>? handler,
        CardModel card,
        EnchantmentModel enchantment)
    {
        if (handler == null) return;
        SafeInvoker.Run(EnchantmentType, hookName, () => handler(card, enchantment));
    }

    private async Task RunAsync<TContext>(string hookName, Func<TContext, Task>? handler, TContext context)
    {
        if (handler == null) return;
        try
        {
            await handler(context);
        }
        catch (Exception ex)
        {
            SafeInvoker.LogFailure(EnchantmentType, hookName, ex);
        }
    }
}

/// <summary>
/// One <c>TrackKeyword</c> entry per registration call.
/// </summary>
internal sealed record KeywordContribution(
    CardKeyword Keyword,
    Func<EnchantmentStackSnapshot, int> AmountFn);

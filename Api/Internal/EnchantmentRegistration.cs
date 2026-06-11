using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;
using EnchantmentVisualSlice = MultiEnchantmentMod.EnchantmentVisualSlice;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Concrete builder behind <see cref="IEnchantmentRegistration"/>. Generic on <typeparamref name="TEnchantment"/>
/// so its <see cref="Commit"/> can call into the strongly-typed
/// <see cref="EnchantmentRegistry.Install{TEnchantment}"/> entry point. Instantiated by
/// <c>MultiEnchantmentApi.Register&lt;T&gt;()</c> and, via <see cref="Activator.CreateInstance(Type)"/>,
/// from <c>MultiEnchantmentApi.Register(Type)</c>.
/// </summary>
internal sealed class EnchantmentRegistration<TEnchantment> : IEnchantmentRegistration
    where TEnchantment : EnchantmentModel
{
    private readonly EnchantmentEntry _entry = new() { EnchantmentType = typeof(TEnchantment) };
    private bool _committed;

    public Type EnchantmentType => typeof(TEnchantment);

    public IEnchantmentRegistration Stack(StackBehavior behavior, StatusAggregation status)
    {
        EnsureNotCommitted();
        _entry.Definition = new StackDefinition(behavior, status);
        return this;
    }

    public IEnchantmentRegistration Stack(StackDefinition definition)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(definition);
        _entry.Definition = definition;
        return this;
    }

    public IEnchantmentRegistration Execution(Action<ExecutionPolicyBuilder> configure)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(configure);
        ExecutionPolicyBuilder builder = new();
        configure(builder);
        _entry.ExecutionPolicy = builder.Build();
        return this;
    }

    public IEnchantmentRegistration OnMergedDelta(Action<EnchantmentModel, int> action)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(action);
        EnsureUnset(_entry.OnMergedDelta, nameof(OnMergedDelta));
        _entry.OnMergedDelta = action;
        return this;
    }

    public IEnchantmentRegistration OnMergedRefresh(Action<EnchantmentModel> action)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(action);
        EnsureUnset(_entry.OnMergedRefresh, nameof(OnMergedRefresh));
        _entry.OnMergedRefresh = action;
        return this;
    }

    public IEnchantmentRegistration WithScope(EnchantmentScope scope)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(scope);
        EnsureUnset(_entry.GetScope, nameof(WithScope));
        _entry.GetScope = () => scope;
        return this;
    }

    public IEnchantmentRegistration LingerForTurns(int turns)
    {
        return WithScope(EnchantmentScope.LingerForTurns(turns));
    }

    public IEnchantmentRegistration MaxActivations(int n, ActivationTrigger? t = null)
    {
        return WithScope(EnchantmentScope.MaxActivations(n, t));
    }

    public IEnchantmentRegistration RemoveWhen(
        Func<CardModel, EnchantmentModel, bool> predicate,
        params ActivationTrigger[] checkOn)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(checkOn);
        return WithScope(EnchantmentScope.RemoveWhen(predicate, checkOn));
    }

    public IEnchantmentRegistration WhenActive(Func<CardModel, EnchantmentModel, bool> predicate)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(predicate);
        EnsureUnset(_entry.GetActiveStatus, nameof(WhenActive));
        _entry.GetActiveStatus = predicate;
        return this;
    }

    [Obsolete("Use WhenActive. This alias targets the same active-status slot and will be removed in a future release.")]
    public IEnchantmentRegistration WhenActiveStatus(Func<CardModel, EnchantmentModel, bool> predicate)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(predicate);
        EnsureUnset(_entry.GetActiveStatus, nameof(WhenActiveStatus));
        _entry.GetActiveStatus = predicate;
        return this;
    }

    public IEnchantmentRegistration OnApplied(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnApplied, nameof(OnApplied));
        _entry.OnApplied = handler;
        return this;
    }

    public IEnchantmentRegistration OnRemoved(Func<CardModel, EnchantmentModel, RemovalReason, bool> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnRemoved, nameof(OnRemoved));
        _entry.OnRemoved = handler;
        return this;
    }

    public IEnchantmentRegistration OnSiblingApplied(Action<CardModel, EnchantmentModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnSiblingApplied, nameof(OnSiblingApplied));
        _entry.OnSiblingApplied = handler;
        return this;
    }

    public IEnchantmentRegistration OnSiblingRemoved(Action<CardModel, EnchantmentModel, EnchantmentModel, RemovalReason> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnSiblingRemoved, nameof(OnSiblingRemoved));
        _entry.OnSiblingRemoved = handler;
        return this;
    }

    public IEnchantmentRegistration OnCombatStart(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCombatStart, nameof(OnCombatStart));
        _entry.OnCombatStart = handler;
        return this;
    }

    public IEnchantmentRegistration OnCombatEnd(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCombatEnd, nameof(OnCombatEnd));
        _entry.OnCombatEnd = handler;
        return this;
    }

    public IEnchantmentRegistration OnTurnStart(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnTurnStart, nameof(OnTurnStart));
        _entry.OnTurnStart = handler;
        return this;
    }

    public IEnchantmentRegistration OnTurnEnd(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnTurnEnd, nameof(OnTurnEnd));
        _entry.OnTurnEnd = handler;
        return this;
    }

    public IEnchantmentRegistration OnRestored(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnRestored, nameof(OnRestored));
        _entry.OnRestored = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardUpgraded(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardUpgraded, nameof(OnCardUpgraded));
        _entry.OnCardUpgraded = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardDowngraded(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardDowngraded, nameof(OnCardDowngraded));
        _entry.OnCardDowngraded = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardPlayed(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardPlayed, nameof(OnCardPlayed));
        _entry.OnCardPlayed = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardDrawn(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardDrawn, nameof(OnCardDrawn));
        _entry.OnCardDrawn = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardExhausted(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardExhausted, nameof(OnCardExhausted));
        _entry.OnCardExhausted = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardDiscarded(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardDiscarded, nameof(OnCardDiscarded));
        _entry.OnCardDiscarded = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardEnteredCombat(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardEnteredCombat, nameof(OnCardEnteredCombat));
        _entry.OnCardEnteredCombat = handler;
        return this;
    }

    public IEnchantmentRegistration OnAnyCardPlayed(Action<CardModel, CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnAnyCardPlayed, nameof(OnAnyCardPlayed));
        _entry.OnAnyCardPlayed = handler;
        return this;
    }

    public IEnchantmentRegistration OnAnyCardDrawn(Action<CardModel, CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnAnyCardDrawn, nameof(OnAnyCardDrawn));
        _entry.OnAnyCardDrawn = handler;
        return this;
    }

    public IEnchantmentRegistration OnAnyCardExhausted(Action<CardModel, CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnAnyCardExhausted, nameof(OnAnyCardExhausted));
        _entry.OnAnyCardExhausted = handler;
        return this;
    }

    public IEnchantmentRegistration OnAnyCardDiscarded(Action<CardModel, CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnAnyCardDiscarded, nameof(OnAnyCardDiscarded));
        _entry.OnAnyCardDiscarded = handler;
        return this;
    }

    public IEnchantmentRegistration OnAfterDamageReceived(Action<CardModel, EnchantmentModel, DamageReceivedContext> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnAfterDamageReceived, nameof(OnAfterDamageReceived));
        _entry.OnAfterDamageReceived = handler;
        return this;
    }

    public IEnchantmentRegistration OnSideTurnStart(Action<CardModel, EnchantmentModel, CombatSide> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnSideTurnStart, nameof(OnSideTurnStart));
        _entry.OnSideTurnStart = handler;
        return this;
    }

    public IEnchantmentRegistration OnBeforeSideTurnStart(Action<CardModel, EnchantmentModel, CombatSide> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnBeforeSideTurnStart, nameof(OnBeforeSideTurnStart));
        _entry.OnBeforeSideTurnStart = handler;
        return this;
    }

    public IEnchantmentRegistration OnBeforeAttack(Action<CardModel, EnchantmentModel, AttackCommand> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnBeforeAttack, nameof(OnBeforeAttack));
        _entry.OnBeforeAttack = handler;
        return this;
    }

    public IEnchantmentRegistration OnAfterAttack(Action<CardModel, EnchantmentModel, AttackCommand> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnAfterAttack, nameof(OnAfterAttack));
        _entry.OnAfterAttack = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardChangedPiles(Action<CardModel, EnchantmentModel, PileType, AbstractModel?> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardChangedPiles, nameof(OnCardChangedPiles));
        _entry.OnCardChangedPiles = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardRetained(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardRetained, nameof(OnCardRetained));
        _entry.OnCardRetained = handler;
        return this;
    }

    public IEnchantmentRegistration OnBeforeBlockGained(Action<CardModel, EnchantmentModel, BlockGainContext> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnBeforeBlockGained, nameof(OnBeforeBlockGained));
        _entry.OnBeforeBlockGained = handler;
        return this;
    }

    public IEnchantmentRegistration OnBlockGained(Action<CardModel, EnchantmentModel, BlockGainContext> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnBlockGained, nameof(OnBlockGained));
        _entry.OnBlockGained = handler;
        return this;
    }

    public IEnchantmentRegistration OnShouldDie(Func<CardModel, EnchantmentModel, Creature, bool> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnShouldDie, nameof(OnShouldDie));
        _entry.OnShouldDie = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardAppliedPower(Action<CardModel, EnchantmentModel, PowerAppliedContext> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardAppliedPower, nameof(OnCardAppliedPower));
        _entry.OnCardAppliedPower = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardTransformed(Action<CardModel, EnchantmentModel, CardModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardTransformed, nameof(OnCardTransformed));
        _entry.OnCardTransformed = handler;
        return this;
    }

    public IEnchantmentRegistration OnCardCloned(Action<CardModel, EnchantmentModel, CardModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnCardCloned, nameof(OnCardCloned));
        _entry.OnCardCloned = handler;
        return this;
    }

    public IEnchantmentRegistration TrackKeyword(CardKeyword keyword, Func<EnchantmentStackSnapshot, int> amountFn)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(amountFn);
        _entry.Keywords.Add(new KeywordContribution(keyword, amountFn));
        return this;
    }

    public IEnchantmentRegistration FormatExtraText(PresentationTextFormatter formatter)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(formatter);
        EnsureUnset(_entry.FormatExtraText, nameof(FormatExtraText));
        _entry.FormatExtraText = formatter;
        return this;
    }

    public IEnchantmentRegistration PresentationStyle(EnchantmentPresentationStyle style)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(style);
        EnsureUnset(_entry.PresentationStyle, nameof(PresentationStyle));
        _entry.PresentationStyle = style;
        return this;
    }

    public IEnchantmentRegistration VisualSlices(Func<EnchantmentStackSnapshot, IReadOnlyList<int>?> compute)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(compute);
        EnsureUnset(_entry.GetVisualSliceAmounts, nameof(VisualSlices));
        _entry.GetVisualSliceAmounts = compute;
        return this;
    }

    public IEnchantmentRegistration VisualSlicesWithStatus(
        Func<EnchantmentStackSnapshot, IReadOnlyList<EnchantmentVisualSlice>?> compute)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(compute);
        EnsureUnset(_entry.GetVisualSlices, nameof(VisualSlicesWithStatus));
        _entry.GetVisualSlices = compute;
        return this;
    }

    public IEnchantmentRegistration ModifyDynamicVar(
        string varKey,
        Func<EnchantmentStackSnapshot, decimal, decimal> contribution)
    {
        EnsureNotCommitted();
        if (string.IsNullOrEmpty(varKey))
        {
            throw new ArgumentException("VarKey must be a non-empty string.", nameof(varKey));
        }
        ArgumentNullException.ThrowIfNull(contribution);
        _entry.DynamicVarContributions.Add(new DynamicVarContribution(varKey, contribution));
        return this;
    }

    public IEnchantmentRegistration ModifyEnergyCostInCombat(EnergyCostContribution contribution)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(contribution);
        _entry.EnergyCostContributions.Add(contribution);
        return this;
    }

    public IEnchantmentRegistration ModifyCardPlayCount(CardPlayCountContribution contribution)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(contribution);
        _entry.CardPlayCountContributions.Add(contribution);
        return this;
    }

    public IEnchantmentRegistration ModifyHandDraw(HandDrawContribution contribution)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(contribution);
        _entry.HandDrawContributions.Add(contribution);
        return this;
    }

    public IEnchantmentRegistration ModifyPowerAmountGiven(PowerAmountGivenContribution contribution)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(contribution);
        _entry.PowerAmountGivenContributions.Add(contribution);
        return this;
    }

    public IEnchantmentRegistration OnPlayStacked(StackedOnPlayHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.OnPlayStacked, nameof(OnPlayStacked));
        _entry.OnPlayStacked = handler;
        return this;
    }

    public IEnchantmentRegistration BeforeCardPlayedStacked(StackedBeforeCardPlayedHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.BeforeCardPlayedStacked, nameof(BeforeCardPlayedStacked));
        _entry.BeforeCardPlayedStacked = handler;
        return this;
    }

    public IEnchantmentRegistration AfterCardPlayedStacked(StackedAfterCardPlayedHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.AfterCardPlayedStacked, nameof(AfterCardPlayedStacked));
        _entry.AfterCardPlayedStacked = handler;
        return this;
    }

    public IEnchantmentRegistration AfterSiblingAppliedStacked(StackedAfterSiblingAppliedHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.AfterSiblingAppliedStacked, nameof(AfterSiblingAppliedStacked));
        _entry.AfterSiblingAppliedStacked = handler;
        return this;
    }

    public IEnchantmentRegistration AfterCardDrawnStacked(StackedAfterCardDrawnHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.AfterCardDrawnStacked, nameof(AfterCardDrawnStacked));
        _entry.AfterCardDrawnStacked = handler;
        return this;
    }

    public IEnchantmentRegistration AfterAnyCardDrawnStacked(StackedAfterAnyCardDrawnHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.AfterAnyCardDrawnStacked, nameof(AfterAnyCardDrawnStacked));
        _entry.AfterAnyCardDrawnStacked = handler;
        return this;
    }

    public IEnchantmentRegistration BeforeFlushStacked(StackedBeforeFlushHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.BeforeFlushStacked, nameof(BeforeFlushStacked));
        _entry.BeforeFlushStacked = handler;
        return this;
    }

    public IEnchantmentRegistration AfterDamageGivenStacked(StackedAfterDamageGivenHandler handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        EnsureUnset(_entry.AfterDamageGivenStacked, nameof(AfterDamageGivenStacked));
        _entry.AfterDamageGivenStacked = handler;
        return this;
    }

    public IEnchantmentRegistration HistoryDisplay(HistoryDisplayMode mode)
    {
        EnsureNotCommitted();
        _entry.HistoryDisplay = mode;
        return this;
    }

    public IEnchantmentRegistration HistoryDisplay(HistoryDisplayMode mode, string groupHeader)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(groupHeader);
        _entry.HistoryDisplay = mode;
        _entry.HistoryGroupHeader = mode == HistoryDisplayMode.CustomGroup ? groupHeader : null;
        return this;
    }

    public IEnchantmentRegistration HistoryText(HistoryTextFormatter formatter)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(formatter);
        EnsureUnset(_entry.HistoryTextFormatter, nameof(HistoryText));
        _entry.HistoryTextFormatter = formatter;
        return this;
    }

    public IEnchantmentRegistration Invisible(bool invisible = true)
    {
        EnsureNotCommitted();
        _entry.Invisible = invisible;
        return this;
    }

    public IDisposable Commit()
    {
        EnsureNotCommitted();
        _committed = true;
        return EnchantmentRegistry.Install<TEnchantment>(_entry);
    }

    private void EnsureNotCommitted()
    {
        if (_committed)
        {
            throw new InvalidOperationException(
                "This registration has already been committed; create a new MultiEnchantmentApi.Register<T>() builder if you need to add more.");
        }
    }

    private static void EnsureUnset(object? existingValue, string memberName)
    {
        if (existingValue != null)
        {
            throw new InvalidOperationException(
                $"{memberName} has already been set on this builder; fluent setters are not idempotent.");
        }
    }
}

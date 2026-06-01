using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;
using EnchantmentVisualSlice = MultiEnchantmentMod.EnchantmentVisualSlice;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Delegate signature for <see cref="IEnchantmentRegistration.FormatExtraText"/>. Return
/// <c>true</c> when the formatted text should replace the default text.
/// </summary>
public delegate bool PresentationTextFormatter(
    EnchantmentStackSnapshot snapshot,
    string defaultText,
    out string formattedText);

/// <summary>
/// Fluent builder returned by <c>MultiEnchantmentApi.Register&lt;T&gt;()</c> and
/// <c>MultiEnchantmentApi.Register(Type)</c>. All setters return the same builder, ending with
/// <see cref="Commit"/>. Calling any setter after <see cref="Commit"/> throws.
/// </summary>
/// <remarks>
/// <para>
/// Typical pattern:
/// </para>
/// <code>
/// MultiEnchantmentApi.Register&lt;Goopy&gt;()
///     .Stack(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
///     .TrackKeyword(CardKeyword.Exhaust, snap => snap.ActiveInstanceCount)
///     .Commit();
/// </code>
/// <para>
/// Strongly-typed lambdas are provided by the extension methods in
/// <see cref="EnchantmentRegistrationExtensions"/>.
/// </para>
/// </remarks>
public interface IEnchantmentRegistration
    : IStackingRegistration,
      ILifecycleRegistration,
      IDynamicVarRegistration,
      IPresentationRegistration,
      IStackedHookRegistration
{
    /// <summary>The enchantment model type this registration targets.</summary>
    Type EnchantmentType { get; }

    /// <summary>
    /// Sets the full <see cref="StackDefinition"/> in one call. Use this overload when you need
    /// to configure <see cref="StackDefinition.MaxInstances"/> or
    /// <see cref="StackDefinition.OnOverflow"/> alongside the basic
    /// <see cref="StackBehavior"/> / <see cref="StatusAggregation"/> pair. The built-in
    /// registration builder preserves the full definition. The default interface
    /// implementation exists only for older third-party implementations and falls back to
    /// <see cref="IStackingRegistration.Stack(StackBehavior, StatusAggregation)"/>, dropping cap
    /// and overflow settings.
    /// </summary>
    IEnchantmentRegistration Stack(StackDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Stack(definition.Behavior, definition.Status);
    }

    // Every other method on this interface — Execution / OnMergedDelta / lifecycle bridges /
    // ModifyDynamicVar / TrackKeyword / stacked async hooks / etc. — is declared on one of the
    // five capability interfaces this type inherits (IStackingRegistration, ILifecycleRegistration,
    // IDynamicVarRegistration, IPresentationRegistration, IStackedHookRegistration). The umbrella
    // interface stays so existing references to IEnchantmentRegistration in third-party mods keep
    // compiling; the split is purely organizational for future API evolution.



    /// <summary>
    /// Finalizes the registration and writes it into the runtime registry. Returns a handle that
    /// removes the registration when disposed — useful for tests and hot-reload scenarios.
    /// Calling <see cref="Commit"/> more than once on the same builder throws.
    /// </summary>
    IDisposable Commit();
}

/// <summary>
/// Strongly-typed lambda overloads for <see cref="IEnchantmentRegistration"/>. The non-generic
/// interface is the authoritative contract; these extensions only add type sugar so consumers
/// don't have to cast <c>EnchantmentModel</c> to their concrete subtype.
/// </summary>
public static class EnchantmentRegistrationExtensions
{
    public static IEnchantmentRegistration OnMergedDelta<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<TEnchantment, int> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnMergedDelta((e, n) => action((TEnchantment)e, n));
    }

    public static IEnchantmentRegistration OnMergedRefresh<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnMergedRefresh(e => action((TEnchantment)e));
    }

    public static IEnchantmentRegistration OnApplied<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnApplied((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnRemoved<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<CardModel, TEnchantment, RemovalReason, bool> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.OnRemoved((card, enchantment, reason) => handler(card, (TEnchantment)enchantment, reason));
    }

    public static IEnchantmentRegistration OnSiblingApplied<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, EnchantmentModel> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnSiblingApplied((card, self, sibling) => action(card, (TEnchantment)self, sibling));
    }

    public static IEnchantmentRegistration OnSiblingRemoved<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, EnchantmentModel, RemovalReason> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnSiblingRemoved((card, self, sibling, reason) => action(card, (TEnchantment)self, sibling, reason));
    }

    public static IEnchantmentRegistration OnCombatStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCombatStart((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCombatEnd<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCombatEnd((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnTurnStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnTurnStart((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnTurnEnd<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnTurnEnd((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnRestored<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnRestored((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardPlayed<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardPlayed((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardDrawn<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardDrawn((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardExhausted<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardExhausted((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardDiscarded<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardDiscarded((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardEnteredCombat<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardEnteredCombat((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardPlayed<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardPlayed((played, self, enchantment) => action(played, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardDrawn<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardDrawn((drawn, self, enchantment) => action(drawn, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardExhausted<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardExhausted((exhausted, self, enchantment) => action(exhausted, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardDiscarded<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardDiscarded((discarded, self, enchantment) => action(discarded, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAfterDamageReceived<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, DamageReceivedContext> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAfterDamageReceived((card, enchantment, ctx) => action(card, (TEnchantment)enchantment, ctx));
    }

    public static IEnchantmentRegistration OnSideTurnStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, CombatSide> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnSideTurnStart((card, enchantment, side) => action(card, (TEnchantment)enchantment, side));
    }

    public static IEnchantmentRegistration OnBeforeSideTurnStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, CombatSide> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBeforeSideTurnStart((card, enchantment, side) => action(card, (TEnchantment)enchantment, side));
    }

    public static IEnchantmentRegistration OnBeforeAttack<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, AttackCommand> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBeforeAttack((card, enchantment, cmd) => action(card, (TEnchantment)enchantment, cmd));
    }

    public static IEnchantmentRegistration OnAfterAttack<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, AttackCommand> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAfterAttack((card, enchantment, cmd) => action(card, (TEnchantment)enchantment, cmd));
    }

    public static IEnchantmentRegistration OnCardChangedPiles<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, PileType, AbstractModel?> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardChangedPiles((card, enchantment, pile, source) => action(card, (TEnchantment)enchantment, pile, source));
    }

    public static IEnchantmentRegistration OnCardRetained<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardRetained((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnBeforeBlockGained<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, BlockGainContext> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBeforeBlockGained((card, enchantment, ctx) => action(card, (TEnchantment)enchantment, ctx));
    }

    public static IEnchantmentRegistration OnBlockGained<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, BlockGainContext> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBlockGained((card, enchantment, ctx) => action(card, (TEnchantment)enchantment, ctx));
    }

    public static IEnchantmentRegistration OnShouldDie<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<CardModel, TEnchantment, Creature, bool> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.OnShouldDie((card, enchantment, creature) => handler(card, (TEnchantment)enchantment, creature));
    }

    public static IEnchantmentRegistration VisualSlicesWithStatus<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<EnchantmentStackSnapshot, TEnchantment, IReadOnlyList<EnchantmentVisualSlice>?> compute)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(compute);
        return registration.VisualSlicesWithStatus(
            snapshot => compute(snapshot, (TEnchantment)snapshot.AnchorInstance));
    }

    /// <summary>
    /// Strongly-typed flavor of <see cref="IEnchantmentRegistration.ModifyDynamicVar"/>. The
    /// snapshot / current-value pair maps directly to the non-generic overload; the
    /// <typeparamref name="TEnchantment"/> parameter is present for symmetry with the other
    /// strongly-typed callbacks and is supplied as the snapshot's anchor instance cast to
    /// <typeparamref name="TEnchantment"/>.
    /// </summary>
    public static IEnchantmentRegistration ModifyDynamicVar<TEnchantment>(
        this IEnchantmentRegistration registration,
        string varKey,
        Func<EnchantmentStackSnapshot, TEnchantment, decimal, decimal> contribution)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(contribution);
        return registration.ModifyDynamicVar(
            varKey,
            (snapshot, current) => contribution(snapshot, (TEnchantment)snapshot.AnchorInstance, current));
    }

    public static IEnchantmentRegistration ModifyEnergyCostInCombat<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<EnchantmentStackSnapshot, TEnchantment, decimal, decimal> contribution)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(contribution);
        return registration.ModifyEnergyCostInCombat(
            (snapshot, current) => contribution(snapshot, (TEnchantment)snapshot.AnchorInstance, current));
    }

    public static IEnchantmentRegistration ModifyCardPlayCount<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<EnchantmentStackSnapshot, TEnchantment, int, int> contribution)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(contribution);
        return registration.ModifyCardPlayCount(
            (snapshot, current) => contribution(snapshot, (TEnchantment)snapshot.AnchorInstance, current));
    }

    public static IEnchantmentRegistration OnPlayStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedOnPlayContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.OnPlayStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }

    public static IEnchantmentRegistration BeforeCardPlayedStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedBeforeCardPlayedContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.BeforeCardPlayedStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }

    public static IEnchantmentRegistration AfterCardPlayedStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedAfterCardPlayedContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.AfterCardPlayedStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }

    public static IEnchantmentRegistration AfterSiblingAppliedStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedAfterSiblingAppliedContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.AfterSiblingAppliedStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }

    public static IEnchantmentRegistration AfterCardDrawnStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedAfterCardDrawnContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.AfterCardDrawnStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }

    public static IEnchantmentRegistration AfterAnyCardDrawnStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedAfterCardDrawnContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.AfterAnyCardDrawnStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }

    public static IEnchantmentRegistration BeforeFlushStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedBeforeFlushContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.BeforeFlushStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }

    public static IEnchantmentRegistration AfterDamageGivenStacked<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<StackedAfterDamageGivenContext, TEnchantment, Task> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.AfterDamageGivenStacked(ctx => handler(ctx, (TEnchantment)ctx.Snapshot.AnchorInstance));
    }
}

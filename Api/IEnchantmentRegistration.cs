using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Delegate signature for <see cref="IEnchantmentRegistration.FormatExtraText"/>. Same try-pattern
/// as the legacy <c>IEnchantmentPresentationProvider.TryFormatExtraCardText</c>.
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
{
    /// <summary>The enchantment model type this registration targets.</summary>
    Type EnchantmentType { get; }

    /// <summary>
    /// Sets the stacking behavior and status aggregation. Overwrites previous calls. Required
    /// before <see cref="Commit"/> unless you only want to register secondary behavior (delta /
    /// keyword) on top of an existing definition.
    /// </summary>
    IEnchantmentRegistration Stack(StackBehavior behavior, StatusAggregation status);

    /// <summary>
    /// Configures per-hook execution modes via the fluent
    /// <see cref="ExecutionPolicyBuilder"/>. Overwrites any previous execution policy.
    /// </summary>
    IEnchantmentRegistration Execution(Action<ExecutionPolicyBuilder> configure);

    /// <summary>
    /// Called once per merge application. <paramref name="action"/> receives the anchor instance
    /// and the delta amount added by this single application. Only meaningful for
    /// <see cref="StackBehavior.MergeAmount"/>.
    /// </summary>
    IEnchantmentRegistration OnMergedDelta(Action<EnchantmentModel, int> action);

    /// <summary>
    /// Called when merged enchantment state needs to resync (after save restore, etc.). Replaces
    /// the default implementation that just re-runs <c>RecalculateValues</c>.
    /// </summary>
    IEnchantmentRegistration OnMergedRefresh(Action<EnchantmentModel> action);

    /// <summary>
    /// Fires after the enchantment has been reconstructed from save / packet data and reattached
    /// to its card. Distinct from <see cref="OnApplied"/> (which is "freshly attached, never
    /// before"): <see cref="OnRestored"/> fires every time a card travels across the
    /// serialization boundary, including each multiplayer packet round-trip. Use it to rebuild
    /// any external runtime cache that doesn't survive serialization.
    /// </summary>
    IEnchantmentRegistration OnRestored(Action<CardModel, EnchantmentModel> handler);

    IEnchantmentRegistration WithScope(EnchantmentScope scope);
    IEnchantmentRegistration LingerForTurns(int turns);
    IEnchantmentRegistration MaxActivations(int n, ActivationTrigger? t = null);
    IEnchantmentRegistration WhenActive(Func<CardModel, EnchantmentModel, bool> predicate);

    /// <summary>
    /// Schedules removal as soon as <paramref name="predicate"/> evaluates to <c>true</c>. The
    /// predicate is re-checked whenever any of <paramref name="checkOn"/> fires. Equivalent to
    /// <c>WithScope(EnchantmentScope.RemoveWhen(predicate, checkOn))</c>; provided as a fluent
    /// shorthand for parity with <see cref="LingerForTurns"/> / <see cref="MaxActivations"/>.
    /// </summary>
    IEnchantmentRegistration RemoveWhen(
        Func<CardModel, EnchantmentModel, bool> predicate,
        params ActivationTrigger[] checkOn);
    IEnchantmentRegistration OnApplied(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnRemoved(Func<CardModel, EnchantmentModel, RemovalReason, bool> handler);
    IEnchantmentRegistration OnCombatStart(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCombatEnd(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnTurnStart(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnTurnEnd(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Declares that this enchantment contributes (or removes) the given card keyword while
    /// active. <paramref name="amountFn"/> receives the current stack snapshot and returns the
    /// contribution amount (negative removes, zero is no-op). Can be called multiple times for
    /// different keywords.
    /// </summary>
    IEnchantmentRegistration TrackKeyword(CardKeyword keyword, Func<EnchantmentStackSnapshot, int> amountFn);

    /// <summary>
    /// Supplies an extra-card-text formatter for the description box. Override the default text
    /// by setting <c>formattedText</c> and returning <c>true</c>.
    /// </summary>
    IEnchantmentRegistration FormatExtraText(PresentationTextFormatter formatter);

    /// <summary>
    /// Supplies custom visual slice amounts (per badge). Return <c>null</c> from
    /// <paramref name="compute"/> to fall back to the default slice computation.
    /// </summary>
    IEnchantmentRegistration VisualSlices(Func<EnchantmentStackSnapshot, IReadOnlyList<int>?> compute);

    /// <summary>
    /// Declares that this enchantment contributes to a named dynamic variable on the card. Multiple
    /// enchantments touching the same key compose in "card application order × registration order
    /// on the same enchantment"; no separate priority layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invocation count by stack behavior:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>MergeAmount</c>: <b>once per active gameplay slice</b>. Two merged stacks → two
    ///   invocations, each with a single-slice snapshot whose <c>TotalAmount</c> equals the slice
    ///   amount (typically 1). Write per-stack formulas like <c>current + 5m</c> or
    ///   <c>current * 2m</c>; the pipeline handles scaling.</item>
    ///   <item><c>ExistenceStack</c>: <b>once per type</b> regardless of instance count
    ///   (presence-only semantics). Snapshot contains the full type-wide view.</item>
    ///   <item><c>DuplicateInstance</c>: <b>once per type</b> (the dedup mirrors ExistenceStack to
    ///   keep behavior predictable). If you want per-instance scaling, multiply by
    ///   <c>snapshot.ActiveInstanceCount</c> inside the formula.</item>
    /// </list>
    /// <para>
    /// Caveat: don't pair <c>ModifyDynamicVar("damage", ...)</c> with an
    /// <c>EnchantDamageAdditive</c>/<c>EnchantBlockAdditive</c> override on the same enchantment.
    /// Both channels stack; pick exactly one for any given key.
    /// </para>
    /// </remarks>
    /// <param name="varKey">
    /// The dynamic-variable key (e.g. <c>"damage"</c>, <c>"block"</c>, <c>"Times"</c>,
    /// <c>"Combust"</c>). Matched case-insensitively against the runtime <c>DynamicVar.Name</c>
    /// (which is PascalCase in vanilla); authors may write the lowercase placeholder form here.
    /// </param>
    /// <param name="contribution">
    /// Returns the new running value given the current snapshot for this enchantment type and the
    /// running value so far. Calling this method multiple times for the same <paramref name="varKey"/>
    /// stacks contributions in registration order.
    /// </param>
    IEnchantmentRegistration ModifyDynamicVar(
        string varKey,
        Func<EnchantmentStackSnapshot, decimal, decimal> contribution);

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
}

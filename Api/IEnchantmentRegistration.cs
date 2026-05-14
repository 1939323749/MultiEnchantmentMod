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
    /// Tie-breaker priority. Higher values win when multiple registrations target the same
    /// enchantment type. Defaults to <c>0</c>.
    /// </summary>
    IEnchantmentRegistration WithPriority(int priority);

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
}

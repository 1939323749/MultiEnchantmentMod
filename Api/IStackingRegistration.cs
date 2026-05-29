using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Stacking and lifetime-shape capability surface of <see cref="IEnchantmentRegistration"/>.
/// Covers the stack-behavior contract (how multiple instances merge or coexist), the merge
/// response callbacks, execution policy, and every scope shorthand (linger / max-activations /
/// remove-when / active predicates).
/// </summary>
/// <remarks>
/// All methods return <see cref="IEnchantmentRegistration"/> so the fluent chain on the umbrella
/// builder stays intact. The split is purely organizational: third-party code never has to
/// distinguish between the sub-interfaces.
/// </remarks>
public interface IStackingRegistration
{
    /// <summary>Sets the stacking behavior and status aggregation. Overwrites previous calls.</summary>
    IEnchantmentRegistration Stack(StackBehavior behavior, StatusAggregation status);

    /// <summary>Configures per-hook execution modes via the fluent builder.</summary>
    IEnchantmentRegistration Execution(Action<ExecutionPolicyBuilder> configure);

    /// <summary>Called once per merge application with the anchor instance and added amount.</summary>
    IEnchantmentRegistration OnMergedDelta(Action<EnchantmentModel, int> action);

    /// <summary>Replaces the default <c>RecalculateValues</c>-based merge resync.</summary>
    IEnchantmentRegistration OnMergedRefresh(Action<EnchantmentModel> action);

    IEnchantmentRegistration WithScope(EnchantmentScope scope);
    IEnchantmentRegistration LingerForTurns(int turns);
    IEnchantmentRegistration MaxActivations(int n, ActivationTrigger? t = null);

    /// <summary>Active-status predicate that drives <c>enchantment.Status</c>; composes with scope.</summary>
    IEnchantmentRegistration WhenActive(Func<CardModel, EnchantmentModel, bool> predicate);

    /// <summary>
    /// Active-status predicate that drives <c>enchantment.Status</c>; composes with scope.
    /// </summary>
    IEnchantmentRegistration WhenActiveStatus(Func<CardModel, EnchantmentModel, bool> predicate);

    /// <summary>Schedules removal when <paramref name="predicate"/> first holds.</summary>
    IEnchantmentRegistration RemoveWhen(
        Func<CardModel, EnchantmentModel, bool> predicate,
        params ActivationTrigger[] checkOn);
}

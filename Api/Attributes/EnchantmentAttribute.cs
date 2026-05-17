using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Marks an <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel"/> subclass for the stacking
/// registry. The simplest path to integrating with MultiEnchantmentMod: put this attribute on
/// your enchantment class and call <c>MultiEnchantmentApi.ScanCallingAssembly()</c> from your
/// <c>[ModInitializer]</c>.
/// </summary>
/// <remarks>
/// When both this attribute and a companion <see cref="EnchantmentDefinition{TEnchantment}"/>
/// subclass declare values, attribute values are used as the seed and the companion class can
/// override individual virtual members (e.g. <see cref="EnchantmentDefinition{TEnchantment}.OnMergedDelta"/>).
/// The analyzer flags conflicting declarations as MEM002.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EnchantmentAttribute : Attribute
{
    /// <summary>
    /// How second/later applications of this enchantment to the same card are resolved.
    /// </summary>
    public StackBehavior Stack { get; init; } = StackBehavior.DisallowDuplicate;

    /// <summary>
    /// How the status of multiple visual slices / live instances is aggregated for UI and hooks.
    /// </summary>
    public StatusAggregation Status { get; init; } = StatusAggregation.AnyInstanceCountsAsOne;

    /// <summary>
    /// Optional reference to a sibling <see cref="EnchantmentDefinition{TEnchantment}"/> subclass.
    /// Currently informational only (used by the analyzer for cross-file diagnostics). The
    /// scanner discovers companion classes automatically as long as they have a parameterless
    /// constructor.
    /// </summary>
    public Type? Companion { get; init; }

    public ScopeKind Scope { get; init; } = ScopeKind.Permanent;
    public int MaxActivations { get; init; }
    public int LingerTurns { get; init; }
    public ActivationTrigger Activation { get; init; } = ActivationTrigger.OnPlay;
}

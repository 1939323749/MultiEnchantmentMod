using System;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Read-only view of a single enchantment's runtime scope state (activation count, turns
/// remaining, current scope). Exposed on <see cref="EnchantmentStackSnapshot.ScopeStates"/>
/// so authors can inspect scope counters from <c>ModifyDynamicVar</c>, <c>FormatExtraText</c>,
/// <c>VisualSlices</c>, and other presentation hooks without touching the framework's mutable
/// internal <c>ScopeRuntimeState</c>.
/// </summary>
/// <remarks>
/// This record is a snapshot taken when the enclosing <see cref="EnchantmentStackSnapshot"/>
/// is constructed. Mutations to the live scope state after the snapshot is taken are NOT
/// reflected; call <c>Snapshots.Get(enchantment)</c> again for fresh data.
/// </remarks>
public sealed record ScopeRuntimeStateView(
    /// <summary>The resolved scope for this enchantment (Permanent, LingerForTurns, MaxActivations, etc.).</summary>
    EnchantmentScope Scope,
    /// <summary>Number of times this enchantment has been activated since the last combat start.</summary>
    int ActivationCount,
    /// <summary>Turns remaining before auto-removal (LingerForTurns). -1 when not applicable.</summary>
    int TurnsRemaining,
    /// <summary>True when this instance uses an apply-time or retroactive scope override.</summary>
    bool HasOverride = false)
{
    /// <summary>
    /// Convenience: <c>true</c> when the scope is <see cref="EnchantmentScope.LingerForTurnsScope"/>
    /// and <see cref="TurnsRemaining"/> has reached zero.
    /// </summary>
    public bool IsExpired => Scope is EnchantmentScope.LingerForTurnsScope && TurnsRemaining <= 0;

    /// <summary>
    /// Convenience: for <see cref="EnchantmentScope.MaxActivationsScope"/>, returns
    /// <c>ActivationCount >= Max</c>. Always <c>false</c> for non-MaxActivations scopes.
    /// </summary>
    public bool IsLimitReached =>
        Scope is EnchantmentScope.MaxActivationsScope maxScope && ActivationCount >= maxScope.Max;
}

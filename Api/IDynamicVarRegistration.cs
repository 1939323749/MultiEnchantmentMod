using System;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Numeric / dynamic-variable contribution capability surface of
/// <see cref="IEnchantmentRegistration"/>. These contributions compose across every registration
/// for a given enchantment type — they do not occupy the single Definition slot, so a mod can
/// add dynamic-var contributions to an enchantment another mod (or built-ins) already defined.
/// </summary>
public interface IDynamicVarRegistration
{
    /// <summary>
    /// Declares a contribution to a named dynamic variable on the card. Multiple registrations
    /// for the same <paramref name="varKey"/> compose in registration order.
    /// </summary>
    IEnchantmentRegistration ModifyDynamicVar(
        string varKey,
        Func<EnchantmentStackSnapshot, decimal, decimal> contribution);

    /// <summary>Declares a combat energy-cost contribution.</summary>
    IEnchantmentRegistration ModifyEnergyCostInCombat(EnergyCostContribution contribution);

    /// <summary>Declares a card play-count contribution.</summary>
    IEnchantmentRegistration ModifyCardPlayCount(CardPlayCountContribution contribution);
}

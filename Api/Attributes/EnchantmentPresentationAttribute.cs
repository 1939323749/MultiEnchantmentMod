using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Declares that an <see cref="EnchantmentDefinition{TEnchantment}"/> overrides one or both
/// presentation hooks. Acts as a hint for the analyzer (MEM006) and as future-proofing for
/// optimization passes that skip presentation lookups for enchantments that don't customize them.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EnchantmentPresentationAttribute : Attribute
{
    /// <summary>
    /// The companion class overrides
    /// <see cref="EnchantmentDefinition{TEnchantment}.TryFormatExtraText"/>.
    /// </summary>
    public bool HasExtraText { get; init; }

    /// <summary>
    /// The companion class overrides
    /// <see cref="EnchantmentDefinition{TEnchantment}.GetVisualSliceAmounts"/>.
    /// </summary>
    public bool HasVisualSliceOverride { get; init; }
}

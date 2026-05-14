using System;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Last-resort defaults consulted by <see cref="EnchantmentDefinition{TEnchantment}"/>'s
/// <see cref="EnchantmentDefinition{TEnchantment}.GetDefinition"/> when the model type
/// carries no <see cref="EnchantmentAttribute"/>. Built-in vanilla enchantments go through
/// <c>BuiltInRegistrations.RegisterAll()</c> instead, so this only fires for third-party
/// definition classes whose author left the attribute off the model.
/// </summary>
/// <remarks>
/// The default is deliberately conservative: <see cref="StackBehavior.DisallowDuplicate"/>
/// + <see cref="StatusAggregation.AnyInstanceCountsAsOne"/>. Execution policy defaults are
/// not handled here — see <see cref="EnchantmentDefinition{TEnchantment}.GetExecutionPolicy"/>,
/// which returns <c>null</c> in the no-attribute case so the legacy behavior-derived
/// fallback in <c>MultiEnchantmentStackSupport.GetExecutionPolicy</c> stays authoritative.
/// </remarks>
internal static class BuiltInDefaults
{
    public static StackDefinition GetDefinition(Type enchantmentType)
    {
        _ = enchantmentType;
        return StackDefinition.Default;
    }
}

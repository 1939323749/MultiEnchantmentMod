using System;
using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Last-resort defaults consulted by <see cref="EnchantmentDefinition{TEnchantment}"/>'s
/// virtual fallback chain when the model type carries no <see cref="EnchantmentAttribute"/>
/// or <see cref="EnchantmentExecutionAttribute"/>. Built-in vanilla enchantments go through
/// <c>BuiltInRegistrations.RegisterAll()</c> instead, so this only fires for third-party
/// definition classes whose author left those attributes off the model.
/// </summary>
/// <remarks>
/// The defaults are deliberately conservative: <see cref="StackBehavior.DisallowDuplicate"/>
/// + <see cref="StatusAggregation.AnyInstanceCountsAsOne"/>, and an all-Default execution
/// policy. If a future refactor wants to consult <c>EnchantmentRegistry</c> here to discover
/// values previously committed for the same type, this is the place — but doing so would
/// blur the "registration-time" vs "definition-time" separation, so keep the API simple by
/// preferring attributes / explicit <see cref="EnchantmentDefinition{TEnchantment}"/> overrides.
/// </remarks>
internal static class BuiltInDefaults
{
    public static StackDefinition GetDefinition(Type enchantmentType)
    {
        _ = enchantmentType;
        return StackDefinition.Default;
    }

    public static LegacyExecutionPolicy GetExecutionPolicy(Type enchantmentType)
    {
        _ = enchantmentType;
        return new LegacyExecutionPolicy();
    }
}

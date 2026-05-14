using System;
using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Source of default <see cref="StackDefinition"/> / <see cref="LegacyExecutionPolicy"/>
/// values for unknown enchantment types and for the legacy mod-built-in matrix.
/// </summary>
/// <remarks>
/// Step 1 stub: every type maps to <see cref="StackDefinition.Default"/>. Step 4 will populate
/// the table by porting <c>MultiEnchantmentStackSupport.GetBuiltInDefinition</c> /
/// <c>GetBuiltInExecutionPolicy</c> into <c>BuiltInRegistrations.RegisterAll()</c> and routing
/// the lookups here through the v2 registry.
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

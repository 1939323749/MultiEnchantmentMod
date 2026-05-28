using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Optional marker on an <see cref="EnchantmentDefinition{TEnchantment}"/> subclass. The scanner
/// auto-discovers companion classes whether or not this attribute is present; tagging the class
/// explicitly lets the analyzer emit better diagnostics about discovered vs hand-registered
/// definitions.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EnchantmentDefinitionAttribute : Attribute
{
    public StackBehavior Stack { get; init; }
    public StatusAggregation Status { get; init; }
}

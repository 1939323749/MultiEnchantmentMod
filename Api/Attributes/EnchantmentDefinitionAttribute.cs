using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Optional marker on an <see cref="EnchantmentDefinition{TEnchantment}"/> subclass. The scanner
/// auto-discovers companion classes whether or not this attribute is present, but tagging the
/// class explicitly:
///   * Sets a per-class priority for registration ordering (overrides the base class's
///     <c>Priority</c> property when present).
///   * Lets the analyzer emit better diagnostics about discovered vs hand-registered providers.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EnchantmentDefinitionAttribute : Attribute
{
    public int Priority { get; init; }
}

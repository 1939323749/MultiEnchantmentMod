using System;
using HookExecutionMode = MultiEnchantmentMod.HookExecutionMode;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Declares how each Harmony-driven hook should iterate when the enchantment has multiple slices
/// or instances. Each property defaults to <see cref="HookExecutionMode.Default"/>, which means
/// "fall back to <see cref="All"/>"; if <see cref="All"/> is also <see cref="HookExecutionMode.Default"/>
/// the built-in mode for the enchantment's <see cref="StackBehavior"/> kicks in.
/// </summary>
/// <remarks>
/// Can be applied to either the enchantment class or its sibling
/// <see cref="EnchantmentDefinition{TEnchantment}"/> subclass.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EnchantmentExecutionAttribute : Attribute
{
    /// <summary>Default execution mode for any hook that does not specify its own value below.</summary>
    public HookExecutionMode All { get; init; } = HookExecutionMode.Default;

    public HookExecutionMode OnEnchant { get; init; } = HookExecutionMode.Default;
    public HookExecutionMode OnPlay { get; init; } = HookExecutionMode.Default;
    public HookExecutionMode AfterCardPlayed { get; init; } = HookExecutionMode.Default;
    public HookExecutionMode AfterCardDrawn { get; init; } = HookExecutionMode.Default;
    public HookExecutionMode AfterPlayerTurnStart { get; init; } = HookExecutionMode.Default;
    public HookExecutionMode BeforePlayPhaseStart { get; init; } = HookExecutionMode.Default;
    public HookExecutionMode BeforeFlush { get; init; } = HookExecutionMode.Default;
}

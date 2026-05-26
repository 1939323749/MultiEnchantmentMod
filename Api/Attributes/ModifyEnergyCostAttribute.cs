using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Marks a method as a combat energy-cost contribution. Required signature:
/// <c>decimal Method(EnchantmentStackSnapshot snapshot, decimal currentCost)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ModifyEnergyCostAttribute : Attribute
{
}

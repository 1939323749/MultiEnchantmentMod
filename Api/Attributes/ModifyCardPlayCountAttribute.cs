using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Marks a method as a card play-count contribution. Required signature:
/// <c>int Method(EnchantmentStackSnapshot snapshot, int currentPlayCount)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ModifyCardPlayCountAttribute : Attribute
{
}

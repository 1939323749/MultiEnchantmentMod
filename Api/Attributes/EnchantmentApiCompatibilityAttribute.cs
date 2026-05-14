using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Assembly-level declaration of the MultiEnchantmentMod public API version range the assembly
/// was built against. The scanner consults this attribute before discovering any enchantment
/// definitions:
///   * Missing attribute → logs an informational warning, scans anyway (legacy / pre-v2 assemblies).
///   * <see cref="MinVersion"/> &gt; <see cref="MultiEnchantmentApiVersion.Current"/> → scan is
///     refused and an error is logged; the assembly's enchantments will not register.
///   * Otherwise the assembly is scanned normally.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class EnchantmentApiCompatibilityAttribute : Attribute
{
    /// <summary>Smallest API version this mod can run against.</summary>
    public int MinVersion { get; }

    /// <summary>
    /// Largest API version this mod can run against. Defaults to <see cref="MinVersion"/> when not
    /// set explicitly; future API versions outside the range will still scan but emit a warning.
    /// </summary>
    public int MaxVersion { get; init; }

    public EnchantmentApiCompatibilityAttribute(int minVersion)
    {
        MinVersion = minVersion;
        MaxVersion = minVersion;
    }
}

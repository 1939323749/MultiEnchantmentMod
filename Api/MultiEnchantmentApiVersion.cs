namespace MultiEnchantmentMod.Api;

/// <summary>
/// Exposes the current public-API version of MultiEnchantmentMod. Bumps on every breaking
/// change to the surface declared under <c>MultiEnchantmentMod.Api</c>. Third-party mods should
/// declare the range they were built against using <see cref="EnchantmentApiCompatibilityAttribute"/>
/// (or call <see cref="MultiEnchantmentApi.RequireApiVersion"/> from their initializer).
/// </summary>
public static class MultiEnchantmentApiVersion
{
    /// <summary>The currently shipped API version.</summary>
    public const int Current = 2;
}

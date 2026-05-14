using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Non-generic facade of <see cref="EnchantmentDefinition{TEnchantment}"/>. Lets the assembly
/// scanner (and any other code that only has an <see cref="object"/> reference to a definition
/// subclass) install the definition without using reflection on the generic type parameter.
/// </summary>
public interface IEnchantmentDefinition
{
    /// <summary>The enchantment model type that this definition configures.</summary>
    Type EnchantmentType { get; }

    /// <summary>
    /// Materializes this definition into a v2 registry entry and registers the corresponding
    /// adapter shims with the legacy provider tables. Returns a disposable handle that fully
    /// reverses the registration when disposed.
    /// </summary>
    IDisposable Register();
}

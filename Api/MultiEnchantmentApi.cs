using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Public entry point for the v2 enchantment stacking API. Third-party mods register their
/// enchantments here — either via attributes (and an assembly scan) or via the fluent
/// <see cref="Register{TEnchantment}"/> builder.
/// </summary>
/// <remarks>
/// <para>
/// Recommended integration pattern from a third-party mod's <c>[ModInitializer]</c>:
/// </para>
/// <code>
/// public static void Initialize()
/// {
///     if (!MultiEnchantmentApi.RequireApiVersion(2)) return;
///     MultiEnchantmentApi.ScanCallingAssembly();
/// }
/// </code>
/// <para>
/// Scan methods are wired up in Step 3 of the v2 rollout; right now the facade exposes the
/// fluent builder and the version-check helper.
/// </para>
/// </remarks>
public static class MultiEnchantmentApi
{
    /// <summary>The currently shipped API version. Re-export of <see cref="MultiEnchantmentApiVersion.Current"/>.</summary>
    public static int CurrentVersion => MultiEnchantmentApiVersion.Current;

    /// <summary>
    /// Starts a fluent registration for <typeparamref name="TEnchantment"/>. Chain
    /// <see cref="IEnchantmentRegistration"/> setters and finish with
    /// <see cref="IEnchantmentRegistration.Commit"/>.
    /// </summary>
    public static IEnchantmentRegistration Register<TEnchantment>()
        where TEnchantment : EnchantmentModel
    {
        return new EnchantmentRegistration<TEnchantment>();
    }

    /// <summary>
    /// Non-generic flavor of <see cref="Register{TEnchantment}"/> — needed for built-in
    /// migrations where the type is only available as a <see cref="Type"/> reference and for
    /// downstream tools / generators.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="enchantmentType"/> is not
    /// assignable to <see cref="EnchantmentModel"/>.</exception>
    public static IEnchantmentRegistration Register(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"{enchantmentType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(enchantmentType));
        }

        Type registrationType = typeof(EnchantmentRegistration<>).MakeGenericType(enchantmentType);
        object instance = Activator.CreateInstance(registrationType)
            ?? throw new InvalidOperationException(
                $"Failed to instantiate registration builder for {enchantmentType.FullName}.");
        return (IEnchantmentRegistration)instance;
    }

    /// <summary>
    /// Returns <c>true</c> when the runtime's API version is at least <paramref name="minimum"/>.
    /// Third-party mods should call this from their initializer to fail-fast on mismatched
    /// MultiEnchantmentMod versions. Logs an error when the check fails so the user has a
    /// breadcrumb in the game log explaining why a feature went silent.
    /// </summary>
    public static bool RequireApiVersion(int minimum)
    {
        if (CurrentVersion >= minimum)
        {
            return true;
        }

        global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Error(
            $"[StackApi] Caller requires MultiEnchantmentMod API v{minimum} but runtime is v{CurrentVersion}. " +
            "The dependent mod's enchantment registrations will not run; update MultiEnchantmentMod.");
        return false;
    }

    /// <summary>
    /// Scans <paramref name="assembly"/> for v2 enchantment registrations (attribute-tagged
    /// <see cref="EnchantmentModel"/> subclasses and <see cref="EnchantmentDefinition{TEnchantment}"/>
    /// subclasses) and legacy v1 provider implementations. Idempotent: re-scanning the same
    /// assembly does nothing. Returns the number of new registrations performed.
    /// </summary>
    public static int ScanAssembly(Assembly assembly) =>
        AssemblyScanner.ScanAssembly(assembly);

    /// <summary>
    /// Convenience wrapper that scans the caller's assembly. The recommended integration point
    /// for third-party mods: call this from <c>[ModInitializer]</c>.
    /// </summary>
    public static int ScanCallingAssembly([CallerFilePath] string? _ = null)
    {
        // [CallerFilePath] is unused — we resolve the caller via Assembly.GetCallingAssembly().
        // The parameter exists only so signature-based analyzers / docs distinguish this from
        // the explicit ScanAssembly overload.
        return AssemblyScanner.ScanAssembly(Assembly.GetCallingAssembly());
    }

    /// <summary>
    /// Freezes the registry. After this call, <see cref="ScanAssembly"/> logs a warning and
    /// does nothing, and the lazy first-Resolve scan becomes a no-op. Use it once the game has
    /// entered active gameplay and no further mod loading is expected.
    /// </summary>
    public static void SealRegistry() =>
        AssemblyScanner.Seal();

    // --- Advanced read-only snapshot API -----------------------------------------------------

    /// <summary>
    /// Power-user accessors that mirror the legacy <c>MultiEnchantmentStackApi.GetSnapshot</c>
    /// surface. Reserved for tools, debug overlays, and analyzer-driven content. Most consumers
    /// do not need these.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static class Snapshots
    {
        public static global::MultiEnchantmentMod.EnchantmentStackSnapshot Get(EnchantmentModel enchantment) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetSnapshot(enchantment);

        public static System.Collections.Generic.IReadOnlyList<global::MultiEnchantmentMod.EnchantmentStackSnapshot> ForCard(
            MegaCrit.Sts2.Core.Models.CardModel? card) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetSnapshots(card);

        public static global::MultiEnchantmentMod.HookExecutionMode ExecutionMode(
            Type enchantmentType,
            global::MultiEnchantmentMod.EnchantmentHookKind hookKind) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetExecutionMode(enchantmentType, hookKind);

        public static int HookExecutionCount(
            EnchantmentModel enchantment,
            global::MultiEnchantmentMod.EnchantmentHookKind hookKind) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetHookExecutionCount(enchantment, hookKind);
    }
}

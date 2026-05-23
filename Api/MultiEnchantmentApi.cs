using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
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

    public static bool RemoveEnchantment(
        CardModel card,
        EnchantmentModel enchantment,
        RemovalReason reason = RemovalReason.Manual)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);
        return MultiEnchantmentScopeSupport.RemoveEnchantmentWithReason(card, enchantment, reason);
    }

    /// <summary>
    /// Applies <paramref name="enchantment"/> to <paramref name="card"/>, optionally overriding the
    /// registration-time scope for this concrete application only. Predicate-bearing scopes
    /// (<c>ConditionalActive</c> / <c>RemoveWhen</c>) are rejected because they cannot be persisted.
    /// </summary>
    public static EnchantmentModel? Enchant(
        CardModel card,
        EnchantmentModel enchantment,
        decimal amount = 1,
        EnchantmentScope? scopeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);
        if (scopeOverride != null && global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.RejectNonPersistableScopeOverride(scopeOverride, nameof(Enchant), enchantment))
        {
            return null;
        }

        return global::MultiEnchantmentMod.MultiEnchantmentSupport.ApplyEnchantmentWithScopeOverride(
            enchantment,
            card,
            amount,
            scopeOverride);
    }

    /// <summary>
    /// Changes or clears the per-instance scope override on an already-attached enchantment.
    /// Passing <c>null</c> clears the override and returns to the registration-time scope.
    /// </summary>
    public static bool SetScopeOverride(
        CardModel card,
        EnchantmentModel enchantment,
        EnchantmentScope? newScope)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);
        if (newScope != null && global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.RejectNonPersistableScopeOverride(newScope, nameof(SetScopeOverride), enchantment))
        {
            return false;
        }

        if (!global::MultiEnchantmentMod.MultiEnchantmentSupport.GetEnchantments(card).Any(e => ReferenceEquals(e, enchantment)))
        {
            return false;
        }

        global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.SetScopeOverride(card, enchantment, newScope);
        global::MultiEnchantmentMod.MultiEnchantmentSupport.RefreshDerivedStateFor(enchantment);
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="card"/> has an enchantment assignable to
    /// <typeparamref name="TEnchantment"/> in any multi-enchantment slot.
    /// </summary>
    public static bool HasEnchantment<TEnchantment>(CardModel? card)
        where TEnchantment : EnchantmentModel
    {
        return global::MultiEnchantmentMod.MultiEnchantmentSupport.HasEnchantment<TEnchantment>(card);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="card"/> has an enchantment assignable to
    /// <paramref name="enchantmentType"/> in any multi-enchantment slot.
    /// </summary>
    public static bool HasEnchantment(CardModel? card, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"{enchantmentType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(enchantmentType));
        }

        foreach (EnchantmentModel enchantment in global::MultiEnchantmentMod.MultiEnchantmentSupport.GetEnchantments(card))
        {
            if (enchantmentType.IsInstanceOfType(enchantment))
            {
                return true;
            }
        }

        return false;
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
    /// subclasses). Idempotent: re-scanning the same assembly does nothing. Returns the number of
    /// new registrations performed.
    /// </summary>
    public static int ScanAssembly(Assembly assembly) =>
        AssemblyScanner.ScanAssembly(assembly);

    /// <summary>
    /// Convenience wrapper that scans the caller's assembly. The recommended integration point
    /// for third-party mods: call this from <c>[ModInitializer]</c>. Resolution uses
    /// <see cref="Assembly.GetCallingAssembly"/>, which inspects the runtime stack frame — do
    /// not call through reflection / dispatch helpers; pass the assembly explicitly to
    /// <see cref="ScanAssembly"/> instead.
    /// </summary>
    public static int ScanCallingAssembly() =>
        AssemblyScanner.ScanAssembly(Assembly.GetCallingAssembly());

    /// <summary>
    /// Freezes the registry. After this call, <see cref="ScanAssembly"/> logs a warning and
    /// does nothing, and the lazy first-Resolve scan becomes a no-op. Use it once the game has
    /// entered active gameplay and no further mod loading is expected.
    /// </summary>
    public static void SealRegistry() =>
        AssemblyScanner.Seal();

    /// <summary>
    /// Notifies the framework that <paramref name="enchantment"/>'s
    /// <see cref="EnchantmentModel.Props"/> have been mutated outside the normal pipeline (e.g.
    /// author wrote <c>enchantment.Props.strings["xyz"] = "new"</c>). Triggers a full derived-state
    /// refresh: DynamicVars recalculation, keyword re-evaluation, and UI
    /// <c>EnchantmentChanged</c> signal.
    /// </summary>
    /// <remarks>
    /// Without this call, mutations to <see cref="EnchantmentModel.Props"/> are invisible to
    /// DynamicVars, card preview, and tooltip rendering until the next full-card refresh cycle
    /// (which may never happen for cosmetic-only fields). Call this immediately after writing
    /// to Props.
    /// </remarks>
    public static void NotifyPropsChanged(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        global::MultiEnchantmentMod.MultiEnchantmentSupport.RefreshDerivedStateFor(enchantment);
    }

    // --- Advanced query API (power-user / tools) ---------------------------------------------

    /// <summary>
    /// Returns the runtime scope state view for <paramref name="enchantment"/> (activation count,
    /// turns remaining, scope kind). Returns <c>null</c> when the enchantment has no scope state
    /// (e.g. permanent scope with no counters) or when <paramref name="enchantment"/> has no
    /// owning card.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static ScopeRuntimeStateView? GetScopeState(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        CardModel? card = enchantment.Card;
        if (card == null) return null;
        if (!global::MultiEnchantmentMod.MultiEnchantmentSupport.TryGetExistingScopeState(card, enchantment, out var state) || state == null)
            return null;
        return new ScopeRuntimeStateView(state.Scope, state.ActivationCount, state.TurnsRemaining, state.OverrideScope is not null);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="enchantment"/> is currently active (not gated
    /// by a <c>ConditionalActive</c> predicate). Useful from custom <c>WhenActive</c> predicates
    /// and debug overlays.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static bool IsActive(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        CardModel? card = enchantment.Card;
        if (card == null) return true;
        return global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.IsActive(card, enchantment);
    }

    /// <summary>
    /// Returns all enchantments on <paramref name="card"/>, optionally excluding
    /// <paramref name="excludingSelf"/>. Lighter-weight alternative to
    /// <c>Snapshots.ForCard</c> when you only need the sibling list, not full stack metadata.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static IReadOnlyList<EnchantmentModel> GetSiblings(CardModel? card, EnchantmentModel? excludingSelf = null)
    {
        if (card == null) return Array.Empty<EnchantmentModel>();
        IEnumerable<EnchantmentModel> all = global::MultiEnchantmentMod.MultiEnchantmentSupport.GetEnchantments(card);
        if (excludingSelf != null)
        {
            all = all.Where(e => !ReferenceEquals(e, excludingSelf));
        }
        return all.ToList();
    }

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

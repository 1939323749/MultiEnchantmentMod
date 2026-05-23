using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MegaCrit.Sts2.Core.Models;
// MultiEnchantmentMod is both the legacy namespace and the bootstrap class name; alias the
// legacy entry point to dodge the ambiguity.
using LegacyStackApi = MultiEnchantmentMod.MultiEnchantmentStackApi;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Process-wide registry of v2 <see cref="EnchantmentEntry"/> values plus their translation into
/// the legacy <c>MultiEnchantmentStackApi</c> provider tables. The registry is the single
/// authoritative installer: every successful <c>Commit()</c> on a fluent registration ends here.
/// </summary>
internal static class EnchantmentRegistry
{
    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<Type, List<EnchantmentEntry>> EntriesByType = new();
    private static readonly ConcurrentDictionary<Type, byte> AutoRegisteredTypes = new();
    // OrdinalIgnoreCase: vanilla DynamicVar.Name uses PascalCase ("Damage", "Block",
    // "CalculatedDamage", ...), but author-facing convention writes lowercase ("damage").
    // Compare case-insensitively so authors never have to mirror vanilla's exact casing.
    private static readonly HashSet<string> DynamicVarKeysWithContributions =
        new(StringComparer.OrdinalIgnoreCase);

    // Lock-free snapshot of DynamicVarKeysWithContributions used by the hot-path HasContributionsFor
    // check. Rebuilt under Sync whenever Install / Dispose mutates the underlying HashSet. FrozenSet
    // gives O(1) Contains with no per-call lock acquisition — important because the DynamicVar
    // Harmony postfix runs on every card preview recalculation.
    private static FrozenSet<string> _dynamicVarKeysSnapshot =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static FrozenSet<string> BuildDynamicVarKeysSnapshot()
    {
        // Caller must hold Sync.
        return DynamicVarKeysWithContributions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Installs an entry into the registry and registers the corresponding adapter shims with
    /// the legacy provider tables. Returns a disposable handle that fully reverses the
    /// registration when disposed (useful for tests / hot-reload).
    /// </summary>
    internal static IDisposable Install<TEnchantment>(EnchantmentEntry entry)
        where TEnchantment : EnchantmentModel
    {
        if (entry.EnchantmentType != typeof(TEnchantment))
        {
            throw new ArgumentException(
                $"EnchantmentEntry targets {entry.EnchantmentType.FullName} but Install<{typeof(TEnchantment).Name}> was called.",
                nameof(entry));
        }

        if (AssemblyScanner.IsSealed)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Error(
                $"[StackApi] Late registration for {entry.EnchantmentType.FullName} (assembly={entry.EnchantmentType.Assembly.GetName().Name}) rejected: registry is sealed. " +
                "Move the Register*/ScanCallingAssembly call earlier — into the mod's [ModInitializer] — or remove the SealRegistry() call.");
            return EmptyDisposable.Instance;
        }

        InstalledShims<TEnchantment> shims = new();

        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(typeof(TEnchantment), out List<EnchantmentEntry>? list))
            {
                list = new List<EnchantmentEntry>();
                EntriesByType[typeof(TEnchantment)] = list;
            }

            list.Add(entry);

            bool keysChanged = false;
            foreach (DynamicVarContribution contribution in entry.DynamicVarContributions)
            {
                if (DynamicVarKeysWithContributions.Add(contribution.VarKey))
                {
                    keysChanged = true;
                }
            }

            if (keysChanged)
            {
                Volatile.Write(ref _dynamicVarKeysSnapshot, BuildDynamicVarKeysSnapshot());
            }

            if (entry.Definition != null)
            {
                shims.Definition = new AdapterDefinitionProvider<TEnchantment> { Entry = entry };
                LegacyStackApi.RegisterDefinitionProvider(shims.Definition);
            }

            if (entry.ExecutionPolicy != null)
            {
                shims.Execution = new AdapterExecutionPolicyProvider<TEnchantment> { Entry = entry };
                LegacyStackApi.RegisterExecutionPolicyProvider(shims.Execution);
            }

            // Merged-state shim is only meaningful for MergeAmount stacks — the underlying
            // ApplyMergedAmountDelta / RefreshMergedEnchantmentState helpers in the legacy
            // support layer short-circuit non-MergeAmount enchantments before consulting any
            // provider. Use the effective definition instead of only the current entry's
            // Definition so secondary registrations can add merge callbacks on top of an existing
            // Stack(MergeAmount, ...) registration.
            bool hasMergedCallbacks = entry.OnMergedDelta != null || entry.OnMergedRefresh != null;
            StackDefinition effectiveDefinition = entry.Definition
                ?? list.LastOrDefault(static existing => existing.Definition != null)?.Definition
                ?? BuiltInDefaults.GetDefinition(typeof(TEnchantment));
            bool wantsMergedShim =
                hasMergedCallbacks &&
                effectiveDefinition.Behavior == StackBehavior.MergeAmount;
            if (wantsMergedShim)
            {
                shims.Merged = new AdapterMergedStateProvider<TEnchantment> { Entry = entry };
                LegacyStackApi.RegisterMergedStateProvider(shims.Merged);
            }

            if (entry.Keywords.Count > 0)
            {
                shims.Keyword = new AdapterKeywordSourceProvider<TEnchantment> { Entry = entry };
                LegacyStackApi.RegisterKeywordProvider(shims.Keyword);
            }

            if (entry.GetVisualSliceAmounts != null || entry.FormatExtraText != null)
            {
                shims.Presentation = new AdapterPresentationProvider<TEnchantment> { Entry = entry };
                LegacyStackApi.RegisterPresentationProvider(shims.Presentation);
            }

            bool wantsLifecycleShim =
                entry.GetScope != null ||
                entry.OnApplied != null ||
                entry.OnRemoved != null ||
                entry.OnCombatStart != null ||
                entry.OnCombatEnd != null ||
                entry.OnTurnStart != null ||
                entry.OnTurnEnd != null;
            if (wantsLifecycleShim)
            {
                shims.Lifecycle = new AdapterLifecycleProvider<TEnchantment> { Entry = entry };
                LegacyStackApi.RegisterLifecycleProvider(shims.Lifecycle);
            }
        }

        return new RegistrationHandle<TEnchantment>(entry, shims);
    }

    private static void Uninstall<TEnchantment>(EnchantmentEntry entry, InstalledShims<TEnchantment> shims)
        where TEnchantment : EnchantmentModel
    {
        lock (Sync)
        {
            if (shims.Definition != null) LegacyStackApi.UnregisterDefinitionProvider(shims.Definition);
            if (shims.Execution != null) LegacyStackApi.UnregisterExecutionPolicyProvider(shims.Execution);
            if (shims.Merged != null) LegacyStackApi.UnregisterMergedStateProvider(shims.Merged);
            if (shims.Keyword != null) LegacyStackApi.UnregisterKeywordProvider(shims.Keyword);
            if (shims.Presentation != null) LegacyStackApi.UnregisterPresentationProvider(shims.Presentation);
            if (shims.Lifecycle != null) LegacyStackApi.UnregisterLifecycleProvider(shims.Lifecycle);

            if (EntriesByType.TryGetValue(typeof(TEnchantment), out List<EnchantmentEntry>? list))
            {
                list.Remove(entry);
                if (list.Count == 0)
                {
                    EntriesByType.Remove(typeof(TEnchantment));
                }
            }

            // Rebuild the set of dynamic-var keys with live contributions whenever any contribution
            // disappears. The cost is fine — disposal is a test / hot-reload path, not gameplay
            // hot path, so a linear walk of every entry's contributions is acceptable here.
            if (entry.DynamicVarContributions.Count > 0)
            {
                DynamicVarKeysWithContributions.Clear();
                foreach (List<EnchantmentEntry> entries in EntriesByType.Values)
                {
                    foreach (EnchantmentEntry liveEntry in entries)
                    {
                        foreach (DynamicVarContribution contribution in liveEntry.DynamicVarContributions)
                        {
                            DynamicVarKeysWithContributions.Add(contribution.VarKey);
                        }
                    }
                }

                Volatile.Write(ref _dynamicVarKeysSnapshot, BuildDynamicVarKeysSnapshot());
            }
        }
    }

    /// <summary>
    /// Returns every dynamic-variable contribution registered for the given enchantment type, in
    /// registration order. The caller is responsible for iterating the result; this is hot-path
    /// code, so the returned list is the live registry storage — do not mutate it.
    /// </summary>
    internal static IReadOnlyList<DynamicVarContribution> GetContributions(Type enchantmentType, string varKey)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return Array.Empty<DynamicVarContribution>();
            }

            List<DynamicVarContribution>? matches = null;
            foreach (EnchantmentEntry entry in entries)
            {
                foreach (DynamicVarContribution contribution in entry.DynamicVarContributions)
                {
                    if (!string.Equals(contribution.VarKey, varKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches ??= new List<DynamicVarContribution>();
                    matches.Add(contribution);
                }
            }

            return (IReadOnlyList<DynamicVarContribution>?)matches ?? Array.Empty<DynamicVarContribution>();
        }
    }

    /// <summary>
    /// Fast existence check used by the <see cref="MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar"/>
    /// postfix patch to short-circuit out when no enchantment in the registry contributes to the
    /// given key. Reads a lock-free <see cref="FrozenSet{T}"/> snapshot rebuilt under Sync on every
    /// Install / Dispose mutation; the postfix runs on every card preview recalc, so removing the
    /// per-call lock matters for large hands / busy frames.
    /// </summary>
    internal static bool HasContributionsFor(string varKey)
    {
        return Volatile.Read(ref _dynamicVarKeysSnapshot).Contains(varKey);
    }

    /// <summary>
    /// Looks up the optional <see cref="StackDefinition.MaxInstances"/> cap for an enchantment
    /// type. Returns the lowest non-null value across all registered entries (so multiple
    /// registrations contributing different caps converge on the strictest), or <c>null</c> if
    /// no entry sets one. Called from <c>MultiEnchantmentStackSupport.CanStackOnto</c>.
    /// </summary>
    internal static int? GetMaxInstances(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return null;
            }

            int? result = null;
            foreach (EnchantmentEntry entry in entries)
            {
                int? candidate = entry.Definition?.MaxInstances;
                if (candidate == null)
                {
                    continue;
                }

                result = result == null ? candidate : System.Math.Min(result.Value, candidate.Value);
            }

            return result;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if any registry entry exists for the given enchantment type. Used by
    /// <see cref="EnsureRegistered"/> to skip auto-registration when a v2 entry is already present.
    /// </summary>
    internal static bool HasEntryFor(Type enchantmentType)
    {
        lock (Sync)
        {
            return EntriesByType.ContainsKey(enchantmentType);
        }
    }

    /// <summary>
    /// Returns the most permissive registered <see cref="StackOverflowPolicy"/> for the
    /// enchantment type — i.e. anything other than <see cref="StackOverflowPolicy.Reject"/>
    /// wins over Reject. When no entry exists or all entries default to Reject, returns Reject.
    /// </summary>
    internal static StackOverflowPolicy GetOverflowPolicy(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return StackOverflowPolicy.Reject;
            }

            StackOverflowPolicy result = StackOverflowPolicy.Reject;
            foreach (EnchantmentEntry entry in entries)
            {
                StackOverflowPolicy candidate = entry.Definition?.OnOverflow ?? StackOverflowPolicy.Reject;
                if (candidate != StackOverflowPolicy.Reject)
                {
                    result = candidate;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if any registry entry for the given type has at least one
    /// <see cref="DynamicVarContribution"/>. Used to extend
    /// <c>MultiEnchantmentSupport.RequiresMultiEnchantmentLogic</c> so single-enchantment cards
    /// that contribute to dynamic vars still get the mod's UpdateCardPreview prefix path.
    /// </summary>
    internal static bool HasAnyDynamicVarContributions(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return false;
            }

            foreach (EnchantmentEntry entry in entries)
            {
                if (entry.DynamicVarContributions.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Ensures a non-vanilla third-party <see cref="EnchantmentModel"/> subclass that hasn't been
    /// registered through any public API gets a sensible default. Idempotent per type (cached in
    /// <see cref="AutoRegisteredTypes"/>); logs once per type so the third-party author knows the
    /// runtime is filling in defaults on their behalf.
    /// </summary>
    /// <remarks>
    /// <para>Detection rules:</para>
    /// <list type="number">
    ///   <item>vanilla namespace (<c>MegaCrit.Sts2.*</c>) → leave alone (vanilla types are
    ///   pre-registered by <c>BuiltInRegistrations</c>; anything unregistered is by design).</item>
    ///   <item>Overrides one of <c>EnchantDamageAdditive</c> / <c>EnchantDamageMultiplicative</c>
    ///   / <c>EnchantBlockAdditive</c> / <c>EnchantBlockMultiplicative</c> →
    ///   <c>MergeAmount</c> + <c>SharedAcrossStack</c>. Rationale: the author opted into per-card
    ///   value mutation, so stacking the same type should stack the value too. Authors can
    ///   override by registering with <c>StackBehavior.DisallowDuplicate</c> before any card
    ///   reaches this code path.</item>
    ///   <item>Otherwise → leave alone; the v1 fallback (DisallowDuplicate) applies.</item>
    /// </list>
    /// </remarks>
    internal static void EnsureRegistered(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);

        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            return;
        }

        // Hold Sync from the moment we decide to auto-register through Commit, otherwise a
        // racing explicit registration could squeeze in between the HasEntryFor check and our
        // Commit, producing two entries for the same type. Lock is reentrant — Register().Commit()
        // re-acquires Sync inside Install, which is fine.
        lock (Sync)
        {
            if (!AutoRegisteredTypes.TryAdd(enchantmentType, 0))
            {
                return;
            }

            if (EntriesByType.ContainsKey(enchantmentType))
            {
                return;
            }

            string? ns = enchantmentType.Namespace;
            if (ns != null && ns.StartsWith("MegaCrit.Sts2", StringComparison.Ordinal))
            {
                return;
            }

            if (OverridesValueModifierVirtual(enchantmentType))
            {
                try
                {
                    MultiEnchantmentApi.Register(enchantmentType)
                        .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
                        .Commit();

                    global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Info(
                        $"[MultiEnchantment] auto-registered {enchantmentType.FullName} as MergeAmount " +
                        $"(overrides EnchantDamage*/EnchantBlock*). Authors: call " +
                        $"MultiEnchantmentApi.Register<{enchantmentType.Name}>() to opt out.");
                }
                catch (Exception ex)
                {
                    global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                        $"[MultiEnchantment] auto-register attempt for {enchantmentType.FullName} failed: " +
                        $"{ex.GetBaseException().Message}");
                }
                return;
            }

            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Info(
                $"[MultiEnchantment] {enchantmentType.FullName} is not registered; defaulting to " +
                $"DisallowDuplicate. Authors: call MultiEnchantmentApi.Register<{enchantmentType.Name}>() " +
                $"to choose a stack behavior.");
        }
    }

    private static readonly string[] ValueModifierMethodNames =
    {
        "EnchantDamageAdditive",
        "EnchantDamageMultiplicative",
        "EnchantBlockAdditive",
        "EnchantBlockMultiplicative",
    };

    private static bool OverridesValueModifierVirtual(Type enchantmentType)
    {
        foreach (string methodName in ValueModifierMethodNames)
        {
            MethodInfo? method = enchantmentType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null && method.DeclaringType == enchantmentType)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class InstalledShims<TEnchantment>
        where TEnchantment : EnchantmentModel
    {
        public AdapterDefinitionProvider<TEnchantment>? Definition;
        public AdapterExecutionPolicyProvider<TEnchantment>? Execution;
        public AdapterMergedStateProvider<TEnchantment>? Merged;
        public AdapterKeywordSourceProvider<TEnchantment>? Keyword;
        public AdapterPresentationProvider<TEnchantment>? Presentation;
        public AdapterLifecycleProvider<TEnchantment>? Lifecycle;
    }

    private sealed class RegistrationHandle<TEnchantment> : IDisposable
        where TEnchantment : EnchantmentModel
    {
        private readonly EnchantmentEntry _entry;
        private readonly InstalledShims<TEnchantment> _shims;
        private bool _disposed;

        public RegistrationHandle(EnchantmentEntry entry, InstalledShims<TEnchantment> shims)
        {
            _entry = entry;
            _shims = shims;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Uninstall(_entry, _shims);
        }
    }
}

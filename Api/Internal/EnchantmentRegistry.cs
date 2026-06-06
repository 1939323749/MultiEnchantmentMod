using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Process-wide registry of v2 <see cref="EnchantmentEntry"/> values. The registry is the single
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
    /// Installs an entry into the registry. Returns a disposable handle that fully reverses the
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

        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(typeof(TEnchantment), out List<EnchantmentEntry>? list))
            {
                list = new List<EnchantmentEntry>();
                EntriesByType[typeof(TEnchantment)] = list;
            }

            // Multi-registration contract: at most one Definition entry per type. Later
            // registrations may only add Contribution-only payload (dynamic var, energy cost,
            // card play count, keyword, presentation text / visuals, history display). The
            // assembly scanner path swallows the exception via try/catch + warn log, so the
            // second registration becomes a no-op there too.
            if (entry.IsDefinitionEntry)
            {
                foreach (EnchantmentEntry existing in list)
                {
                    if (existing.IsDefinitionEntry)
                    {
                        throw new InvalidOperationException(
                            $"[StackApi] {entry.EnchantmentType.FullName} already has a Definition registration. " +
                            "Each enchantment type allows at most one Definition entry (Stack / scope / " +
                            "active-status / lifecycle / merge / stacked-hook). Subsequent Register<T>() " +
                            "calls must only set Contribution-only fields (TrackKeyword / ModifyDynamicVar / " +
                            "ModifyEnergyCostInCombat / ModifyCardPlayCount / FormatExtraText / WithVisualSlice / " +
                            "HistoryDisplay).");
                    }
                }
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
        }

        return new RegistrationHandle<TEnchantment>(entry);
    }

    private static void Uninstall<TEnchantment>(EnchantmentEntry entry)
        where TEnchantment : EnchantmentModel
    {
        lock (Sync)
        {
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
    /// Returns the (at most one) Definition entry for <paramref name="enchantmentType"/>, i.e.
    /// the registration that supplied stack / scope / active-status / lifecycle / merge /
    /// stacked-hook behavior. Contribution-only registrations are ignored.
    /// </summary>
    internal static EnchantmentEntry? GetDefinitionEntry(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return null;
            }

            foreach (EnchantmentEntry entry in entries)
            {
                if (entry.IsDefinitionEntry)
                {
                    return entry;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Returns the Definition entry only if it satisfies <paramref name="predicate"/> — used by
    /// dispatchers that want to skip the dispatch entirely when no handler is wired up. With the
    /// "at most one Definition entry" contract enforced by <see cref="Install{T}"/>, this is
    /// just a Definition lookup plus a final filter.
    /// </summary>
    internal static EnchantmentEntry? GetDefinitionEntry(Type enchantmentType, Func<EnchantmentEntry, bool> predicate)
    {
        EnchantmentEntry? entry = GetDefinitionEntry(enchantmentType);
        return entry != null && predicate(entry) ? entry : null;
    }

    /// <summary>
    /// Walks every registered entry (Definition + Contribution-only) for
    /// <paramref name="enchantmentType"/> from newest to oldest, returning the first match.
    /// Use this only for Contribution-only field lookups where multiple registrations can each
    /// supply the field and the latest registration wins (e.g. <c>FormatExtraText</c>,
    /// <c>GetVisualSliceAmounts</c>, custom history text formatter). Definition-field lookups
    /// must use <see cref="GetDefinitionEntry(Type, Func{EnchantmentEntry,bool})"/> instead so
    /// that an unrelated Contribution-only registration cannot accidentally satisfy the
    /// predicate.
    /// </summary>
    internal static EnchantmentEntry? GetLastContributionEntry(Type enchantmentType, Func<EnchantmentEntry, bool> predicate)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return null;
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (predicate(entries[i]))
                {
                    return entries[i];
                }
            }

            return null;
        }
    }

    private static readonly Func<EnchantmentEntry, bool> HasLifecycleHandlersPredicate = static entry =>
        entry.GetScope != null ||
        entry.GetActiveStatus != null ||
        entry.OnApplied != null ||
        entry.OnRemoved != null ||
        entry.OnCombatStart != null ||
        entry.OnCombatEnd != null ||
        entry.OnTurnStart != null ||
        entry.OnTurnEnd != null ||
        entry.OnRestored != null;

    internal static bool HasLifecycleHandlers(Type enchantmentType)
    {
        return GetDefinitionEntry(enchantmentType, HasLifecycleHandlersPredicate) != null;
    }

    internal static IReadOnlyList<EnchantmentEntry> GetEntries(Type enchantmentType)
    {
        lock (Sync)
        {
            return EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries)
                ? entries.ToArray()
                : Array.Empty<EnchantmentEntry>();
        }
    }

    internal static HashSet<Type> GetAllRegisteredTypes()
    {
        lock (Sync)
        {
            return new HashSet<Type>(EntriesByType.Keys);
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
    /// Returns the effective <see cref="HistoryDisplayMode"/> for the given enchantment type.
    /// Last explicit non-Auto value wins; falls back to Auto.
    /// </summary>
    internal static HistoryDisplayMode GetHistoryDisplayMode(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return GetDefaultHistoryDisplayMode(enchantmentType);
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].HistoryDisplay != HistoryDisplayMode.Auto)
                {
                    return entries[i].HistoryDisplay;
                }
            }

            return GetDefaultHistoryDisplayMode(enchantmentType);
        }
    }

    /// <summary>
    /// Returns the custom group header registered for the given enchantment type, or <c>null</c>.
    /// </summary>
    internal static string? GetHistoryGroupHeader(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return null;
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].HistoryGroupHeader != null)
                {
                    return entries[i].HistoryGroupHeader;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Returns the custom history text formatter registered for the given enchantment type, or
    /// <c>null</c> for the default format.
    /// </summary>
    internal static HistoryTextFormatter? GetHistoryTextFormatter(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return null;
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].HistoryTextFormatter != null)
                {
                    return entries[i].HistoryTextFormatter;
                }
            }

            return null;
        }
    }

    internal static EnchantmentPresentationStyle GetPresentationStyle(Type enchantmentType)
    {
        AssemblyScanner.EnsureScanned();

        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return GetDefaultPresentationStyle(enchantmentType);
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                EnchantmentPresentationStyle? style = entries[i].PresentationStyle;
                if (style != null)
                {
                    return style;
                }
            }

            return GetDefaultPresentationStyle(enchantmentType);
        }
    }

    private static HistoryDisplayMode GetDefaultHistoryDisplayMode(Type enchantmentType)
    {
        return typeof(ExtraIconEnchantmentModel).IsAssignableFrom(enchantmentType)
            ? HistoryDisplayMode.Hidden
            : HistoryDisplayMode.Auto;
    }

    internal static EnchantmentPresentationStyle GetDefaultPresentationStyle(Type enchantmentType)
    {
        return typeof(ExtraIconEnchantmentModel).IsAssignableFrom(enchantmentType)
            ? ExtraIconPresentation.Default
            : new EnchantmentPresentationStyle();
    }

    /// <summary>
    /// Returns <c>true</c> if the resolved scope for the given enchantment type is permanent
    /// (i.e. <see cref="EnchantmentScope.PermanentScope"/>, <see cref="EnchantmentScope.ConditionalActiveScope"/>,
    /// or <see cref="EnchantmentScope.RemoveWhenScope"/>). Used by <c>RecordEnchantmentHistory</c>
    /// to implement <see cref="HistoryDisplayMode.Auto"/>.
    /// </summary>
    internal static bool IsPermanentScope(Type enchantmentType)
    {
        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(enchantmentType, out List<EnchantmentEntry>? entries))
            {
                return true;
            }

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].GetScope is { })
                {
                    EnchantmentScope scope = entries[i].GetSafeScope();
                    return scope is EnchantmentScope.PermanentScope
                        or EnchantmentScope.ConditionalActiveScope
                        or EnchantmentScope.RemoveWhenScope;
                }
            }

            return true;
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

            if (DeclaresSavedProperties(enchantmentType))
            {
                global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] {enchantmentType.FullName} declares saved properties; " +
                    "defaulting to DisallowDuplicate so the source mod owns its Amount semantics.");
                return;
            }

            if (OverridesValueModifierVirtual(enchantmentType))
            {
                try
                {
                    MultiEnchantmentApi.Register(enchantmentType)
                        .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
                        .Commit();

                    if (EntriesByType.ContainsKey(enchantmentType))
                    {
                        global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Info(
                            $"[MultiEnchantment] auto-registered {enchantmentType.FullName} as MergeAmount " +
                            $"(overrides EnchantDamage*/EnchantBlock*). Authors: call " +
                            $"MultiEnchantmentApi.Register<{enchantmentType.Name}>() to opt out.");
                    }
                }
                catch (Exception ex)
                {
                    global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                        $"[MultiEnchantment] auto-register attempt for {enchantmentType.FullName} failed: " +
                        $"{ex}");
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
        // Enumerate rather than call GetMethod(name, flags): a third-party enchantment may
        // declare overloads of one of these names (e.g. an added parameter), which makes
        // GetMethod throw AmbiguousMatchException. That exception used to escape all the way
        // up to BeforeCombatStartPostfixAsync and abort combat start (first-turn draw failure).
        MethodInfo[] methods = enchantmentType.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (MethodInfo method in methods)
        {
            if (Array.IndexOf(ValueModifierMethodNames, method.Name) < 0)
            {
                continue;
            }

            if (method.DeclaringType != null &&
                method.DeclaringType != typeof(EnchantmentModel) &&
                IsNonVanillaEnchantmentType(method.DeclaringType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonVanillaEnchantmentType(Type type)
    {
        if (!typeof(EnchantmentModel).IsAssignableFrom(type))
        {
            return false;
        }

        string? ns = type.Namespace;
        return ns == null || !ns.StartsWith("MegaCrit.Sts2", StringComparison.Ordinal);
    }

    internal static bool DeclaresSavedProperties(Type enchantmentType)
    {
        for (Type? current = enchantmentType;
             current != null && current != typeof(EnchantmentModel);
             current = current.BaseType)
        {
            foreach (PropertyInfo property in current.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                foreach (Attribute attribute in property.GetCustomAttributes(inherit: false))
                {
                    Type attributeType = attribute.GetType();
                    if (string.Equals(attributeType.Name, "SavedPropertyAttribute", StringComparison.Ordinal) ||
                        string.Equals(attributeType.FullName, "MegaCrit.Sts2.Core.Saves.SavedPropertyAttribute", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private sealed class RegistrationHandle<TEnchantment> : IDisposable
        where TEnchantment : EnchantmentModel
    {
        private readonly EnchantmentEntry _entry;
        private bool _disposed;

        public RegistrationHandle(EnchantmentEntry entry)
        {
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Uninstall<TEnchantment>(_entry);
        }
    }
}

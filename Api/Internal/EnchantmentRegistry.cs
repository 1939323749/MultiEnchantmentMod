using System;
using System.Collections.Generic;
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
    private static readonly object Sync = new();
    private static readonly Dictionary<Type, List<EnchantmentEntry>> EntriesByType = new();

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

        InstalledShims<TEnchantment> shims = new();

        lock (Sync)
        {
            if (!EntriesByType.TryGetValue(typeof(TEnchantment), out List<EnchantmentEntry>? list))
            {
                list = new List<EnchantmentEntry>();
                EntriesByType[typeof(TEnchantment)] = list;
            }

            list.Add(entry);

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
            // provider. Skipping the shim outright on the other behaviors prevents
            // EnchantmentDefinition<T>.Register from installing a no-op shim just because its
            // trampoline delegates are non-null.
            bool wantsMergedShim =
                (entry.OnMergedDelta != null || entry.OnMergedRefresh != null) &&
                entry.Definition?.Behavior == StackBehavior.MergeAmount;
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

            if (EntriesByType.TryGetValue(typeof(TEnchantment), out List<EnchantmentEntry>? list))
            {
                list.Remove(entry);
                if (list.Count == 0)
                {
                    EntriesByType.Remove(typeof(TEnchantment));
                }
            }
        }
    }

    private sealed class InstalledShims<TEnchantment>
        where TEnchantment : EnchantmentModel
    {
        public AdapterDefinitionProvider<TEnchantment>? Definition;
        public AdapterExecutionPolicyProvider<TEnchantment>? Execution;
        public AdapterMergedStateProvider<TEnchantment>? Merged;
        public AdapterKeywordSourceProvider<TEnchantment>? Keyword;
        public AdapterPresentationProvider<TEnchantment>? Presentation;
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

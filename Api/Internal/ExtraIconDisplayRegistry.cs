using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api.Internal;

internal static class ExtraIconDisplayRegistry
{
    // A provider that throws this many times in a row is assumed broken and is disabled so it stops
    // running (and spamming the log) on every card's visual refresh. A later successful call resets
    // the counter, so a transient failure does not permanently disable a healthy provider.
    private const int MaxConsecutiveFailures = 5;

    private static readonly object Sync = new();
    private static readonly List<ProviderEntry> Providers = new();

    // Types we have already warned about (non-marker display type) so the warning fires once, not
    // once per card per frame.
    private static readonly HashSet<Type> WarnedNonMarkerTypes = new();

    // Cheap, lock-free fast-path for the UI hot loop: every card's UpdateVisuals consults the
    // registry, so callers gate on this before paying for GetDisplays' lock + provider scan.
    private static volatile int _providerCount;

    /// <summary>True when at least one display provider is registered.</summary>
    public static bool HasProviders => _providerCount > 0;

    public static IDisposable RegisterProvider(ExtraIconDisplayProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Display-only icon providers live in this UI-only registry and never touch the sealed
        // enchantment registry (GetDisplays just instantiates the type to read its icon), so late
        // registration is safe and must not be silently dropped after SealRegistry().
        ProviderEntry entry = new(provider);
        lock (Sync)
        {
            Providers.Add(entry);
            _providerCount = Providers.Count;
        }

        return new ProviderHandle(entry);
    }

    public static IReadOnlyList<ExtraIconDisplay> GetDisplays(CardModel card)
    {
        if (_providerCount == 0)
        {
            return Array.Empty<ExtraIconDisplay>();
        }

        ProviderEntry[] providers;
        lock (Sync)
        {
            providers = Providers.ToArray();
        }

        if (providers.Length == 0)
        {
            return Array.Empty<ExtraIconDisplay>();
        }

        List<ExtraIconDisplay>? displays = null;
        foreach (ProviderEntry entry in providers)
        {
            if (entry.Disabled)
            {
                continue;
            }

            IEnumerable<ExtraIconDisplay>? provided;
            try
            {
                provided = entry.Provider(card);
            }
            catch (Exception ex)
            {
                NoteProviderFailure(entry, card, ex);
                continue;
            }

            entry.ConsecutiveFailures = 0;
            if (provided == null)
            {
                continue;
            }

            foreach (ExtraIconDisplay display in provided)
            {
                if (display == null)
                {
                    continue;
                }

                if (!typeof(EnchantmentModel).IsAssignableFrom(display.EnchantmentType))
                {
                    global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                        $"[MultiEnchantment] Ignoring extra-icon display for non-enchantment type {display.EnchantmentType.FullName}.");
                    continue;
                }

                WarnIfNotMarkerType(display.EnchantmentType);
                (displays ??= new List<ExtraIconDisplay>()).Add(display);
            }
        }

        return (IReadOnlyList<ExtraIconDisplay>?)displays ?? Array.Empty<ExtraIconDisplay>();
    }

    private static void NoteProviderFailure(ProviderEntry entry, CardModel card, Exception ex)
    {
        entry.ConsecutiveFailures++;
        if (entry.ConsecutiveFailures >= MaxConsecutiveFailures)
        {
            entry.Disabled = true;
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Disabling extra-icon display provider after {entry.ConsecutiveFailures} consecutive failures (last Card={card.Id}): {ex}");
            return;
        }

        global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment] Extra-icon display provider failed for Card={card.Id}: {ex}");
    }

    private static void WarnIfNotMarkerType(Type enchantmentType)
    {
        if (typeof(ExtraIconEnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            return;
        }

        bool firstTime;
        lock (Sync)
        {
            firstTime = WarnedNonMarkerTypes.Add(enchantmentType);
        }

        if (firstTime)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Extra-icon display type {enchantmentType.FullName} is not an {nameof(ExtraIconEnchantmentModel)}. " +
                "Marker presentation defaults (no badge backing, hidden-when-disabled, no amount) are intended for ExtraIconEnchantmentModel subclasses; " +
                "displaying a gameplay enchantment type as a static icon may behave unexpectedly when a live instance of that type also exists.");
        }
    }

    private sealed class ProviderEntry
    {
        public ProviderEntry(ExtraIconDisplayProvider provider)
        {
            Provider = provider;
        }

        public ExtraIconDisplayProvider Provider { get; }

        // Mutated only from GetDisplays / NoteProviderFailure on the (single-threaded) UI refresh
        // path; registration/disposal only add/remove the entry, never touch these fields.
        public int ConsecutiveFailures;
        public bool Disabled;
    }

    private sealed class ProviderHandle : IDisposable
    {
        private ProviderEntry? _entry;

        public ProviderHandle(ProviderEntry entry)
        {
            _entry = entry;
        }

        public void Dispose()
        {
            ProviderEntry? entry = _entry;
            if (entry == null)
            {
                return;
            }

            _entry = null;
            lock (Sync)
            {
                Providers.Remove(entry);
                _providerCount = Providers.Count;
            }
        }
    }
}

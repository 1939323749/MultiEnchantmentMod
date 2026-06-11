using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Small helper for projecting card / relic / ability / enchantment state into a display-only
/// marker.
/// </summary>
/// <remarks>
/// <para>
/// Drive it from lifecycle callbacks such as <c>OnCardDrawn(CardModel, TEnchantment)</c> or vanilla
/// overrides such as <c>Task AfterCardDrawn(PlayerChoiceContext, CardModel, bool)</c>. Keep the
/// authoritative gameplay state on the real card / model; this helper stores only the temporary UI
/// projection per <see cref="CardModel"/> object and refreshes that card's markers after each
/// mutation. Use a stored <see cref="MarkerEnchantmentModel"/> instead when the marker itself must
/// survive save/load as real card state. Calling <see cref="Register"/> is optional — the first
/// mutation auto-registers the display provider.
/// </para>
/// <para>
/// Two ways to project a marker:
/// <list type="bullet">
/// <item><description><b>Amount-gated</b> (<see cref="Set"/>, <see cref="Add"/>): the marker is shown
/// only while the amount is positive; an amount of zero or less removes it.</description></item>
/// <item><description><b>Explicit presence</b> (<see cref="Show"/>): the marker stays shown until
/// <see cref="Remove"/>/<see cref="Clear"/>, the amount is just a label that may be zero, and an
/// <see cref="IconStateOverride"/> can vary this card's icon / hover tip / presentation.</description></item>
/// </list>
/// <see cref="Has"/> tests <em>presence</em> (true even for a <see cref="Show"/>n marker at amount 0);
/// <see cref="Get"/> returns the numeric amount.
/// </para>
/// <para>
/// Use one <see cref="IconState{TMarker}"/> instance per marker type: the render path deduplicates
/// markers by <typeparamref name="TMarker"/>, so two states sharing the same marker type on the
/// same card suppress one another. The same dedup also suppresses this projection when the card
/// already carries a live or stored enchantment of <typeparamref name="TMarker"/> — set
/// <c>showWithLiveEnchantment</c> (ctor) or <see cref="IconStateOverride.ShowWithLiveEnchantment"/>
/// (per card) to coexist instead.
/// </para>
/// <para>
/// <see cref="Dispose"/> is terminal: it unregisters, clears all projections, and makes further
/// mutations throw <see cref="ObjectDisposedException"/>. Mutators are internally synchronized, but
/// are intended to be driven from the game's main thread like other gameplay callbacks.
/// </para>
/// </remarks>
public sealed class IconState<TMarker> : IDisposable
    where TMarker : MarkerEnchantmentModel
{
    private sealed class Entry
    {
        public int Amount;
        public IconStateOverride? Override;
    }

    // Guards every read/write of _states (including the table swap in Clear/Dispose). Individual
    // ConditionalWeakTable operations are internally synchronized, but the read-modify-write in Add
    // is not, so all mutations funnel through this lock. RefreshMarkers is always invoked OUTSIDE
    // the lock to avoid holding it across UI work / provider re-entry.
    private readonly object _sync = new();
    private ConditionalWeakTable<CardModel, Entry> _states = new();
    private readonly Texture2D? _icon;
    private readonly EnchantmentModel? _enchantment;
    private readonly EnchantmentPresentationStyle? _presentationStyle;
    private readonly MarkerDisplayPredicate? _shouldDisplay;
    private readonly bool _showAmount;
    private readonly bool _showWithLiveEnchantment;
    private IDisposable? _registration;
    private bool _disposed;

    public IconState(
        Texture2D? icon = null,
        EnchantmentModel? enchantment = null,
        EnchantmentPresentationStyle? presentationStyle = null,
        MarkerDisplayPredicate? shouldDisplay = null,
        bool showAmount = false,
        bool showWithLiveEnchantment = false)
    {
        _icon = icon;
        _enchantment = enchantment;
        _presentationStyle = presentationStyle;
        _shouldDisplay = shouldDisplay;
        _showAmount = showAmount;
        _showWithLiveEnchantment = showWithLiveEnchantment;
    }

    /// <summary>
    /// True once the display provider is registered (after an explicit <see cref="Register"/> or the
    /// first mutation) and before <see cref="Dispose"/>.
    /// </summary>
    public bool IsRegistered => _registration != null;

    /// <summary>
    /// Registers this state as a display-only marker provider. Optional — the first mutation
    /// (<see cref="Set"/>/<see cref="Add"/>/<see cref="Show"/>) auto-registers — but you can call it
    /// early to register at a deterministic point. Safe to call more than once. Throws once the state
    /// has been disposed.
    /// </summary>
    public void Register()
    {
        lock (_sync)
        {
            EnsureRegisteredLocked();
        }
    }

    /// <summary>
    /// Disposes the provider registration and clears all tracked projections. Terminal: after this,
    /// the mutators throw <see cref="ObjectDisposedException"/>. Idempotent.
    /// </summary>
    public void Dispose()
    {
        IDisposable? registration;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registration = _registration;
            _registration = null;
            _states = new ConditionalWeakTable<CardModel, Entry>();
        }

        registration?.Dispose();
    }

    // Registers the provider on first use. Caller must hold _sync.
    private void EnsureRegisteredLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registration ??= MultiEnchantmentApi.RegisterMarkerDisplayProvider(GetDisplays);
    }

    /// <summary>
    /// <em>Amount-gated</em> projection: shows or updates the marker on <paramref name="card"/>,
    /// where the marker is present iff <paramref name="amount"/> is positive — <paramref name="amount"/>
    /// less than or equal to zero removes it. Preserves any per-card override previously set via
    /// <see cref="Show"/>. Auto-registers on first use. This only changes the display-only projection,
    /// not gameplay state on the card.
    /// </summary>
    public void Set(CardModel card, int amount = 1, bool refresh = true)
    {
        ArgumentNullException.ThrowIfNull(card);
        bool changed;
        lock (_sync)
        {
            EnsureRegisteredLocked();
            changed = ApplyAmountGatedLocked(card, amount);
        }

        if (changed && refresh)
        {
            MultiEnchantmentApi.RefreshMarkers(card);
        }
    }

    /// <summary>
    /// Adds <paramref name="amount"/> to the current projected marker amount (amount-gated, like
    /// <see cref="Set"/>: a result less than or equal to zero removes the marker). The
    /// read-modify-write is atomic. Auto-registers on first use.
    /// </summary>
    public void Add(CardModel card, int amount = 1, bool refresh = true)
    {
        ArgumentNullException.ThrowIfNull(card);
        bool changed;
        lock (_sync)
        {
            EnsureRegisteredLocked();
            int current = _states.TryGetValue(card, out Entry? entry) ? entry.Amount : 0;
            changed = ApplyAmountGatedLocked(card, current + amount);
        }

        if (changed && refresh)
        {
            MultiEnchantmentApi.RefreshMarkers(card);
        }
    }

    /// <summary>
    /// <em>Explicit-presence</em> projection: makes the marker present on <paramref name="card"/>
    /// regardless of <paramref name="amount"/> (so <paramref name="amount"/> 0 renders "0" when the
    /// amount label is shown), and stores <paramref name="overrides"/> to vary this card's icon, hover
    /// tip, presentation, or amount label. Pass <c>null</c> overrides to clear any previous per-card
    /// override. Use <see cref="Remove"/>/<see cref="Clear"/> to end presence. Auto-registers on first
    /// use.
    /// </summary>
    public void Show(CardModel card, int amount = 0, IconStateOverride? overrides = null, bool refresh = true)
    {
        ArgumentNullException.ThrowIfNull(card);
        bool changed;
        lock (_sync)
        {
            EnsureRegisteredLocked();
            bool existed = _states.TryGetValue(card, out Entry? entry);
            entry ??= _states.GetOrCreateValue(card);
            // A brand-new entry is always a change; otherwise only when amount or override differs.
            changed = !existed || entry.Amount != amount || !Equals(entry.Override, overrides);
            entry.Amount = amount;
            entry.Override = overrides;
        }

        if (changed && refresh)
        {
            MultiEnchantmentApi.RefreshMarkers(card);
        }
    }

    /// <summary>Removes the marker from <paramref name="card"/>.</summary>
    public bool Remove(CardModel card, bool refresh = true)
    {
        ArgumentNullException.ThrowIfNull(card);
        bool removed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            removed = _states.Remove(card);
        }

        if (removed && refresh)
        {
            MultiEnchantmentApi.RefreshMarkers(card);
        }

        return removed;
    }

    /// <summary>
    /// Clears all marker state tracked by this helper and refreshes only the cards that were tracked.
    /// </summary>
    public void Clear(bool refresh = true)
    {
        List<CardModel> tracked;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            tracked = SnapshotTrackedCardsLocked();
            _states = new ConditionalWeakTable<CardModel, Entry>();
        }

        if (refresh)
        {
            foreach (CardModel card in tracked)
            {
                MultiEnchantmentApi.RefreshMarkers(card);
            }
        }
    }

    /// <summary>Returns the current projected marker amount for <paramref name="card"/>, or zero.</summary>
    public int Get(CardModel? card)
    {
        if (card == null)
        {
            return 0;
        }

        lock (_sync)
        {
            return _states.TryGetValue(card, out Entry? entry) ? entry.Amount : 0;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="card"/> currently carries this projected marker. This is a
    /// <em>presence</em> check (independent of amount), so it returns true even for a
    /// <see cref="Show"/>n marker whose amount is zero. Use <see cref="Get"/> for the numeric value.
    /// </summary>
    public bool Has(CardModel? card)
    {
        if (card == null)
        {
            return false;
        }

        lock (_sync)
        {
            return _states.TryGetValue(card, out _);
        }
    }

    /// <summary>
    /// A snapshot of the cards this state is currently projecting a marker onto. Useful for diagnostics
    /// or for driving a bulk recompute; the list is a point-in-time copy.
    /// </summary>
    public IReadOnlyList<CardModel> GetTrackedCards()
    {
        lock (_sync)
        {
            return SnapshotTrackedCardsLocked();
        }
    }

    /// <summary>
    /// Re-evaluates and redraws this state's markers on exactly the cards it currently tracks, without
    /// touching unrelated cards (unlike a global refresh). No-op after <see cref="Dispose"/>.
    /// </summary>
    public void RefreshTracked()
    {
        List<CardModel> tracked;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            tracked = SnapshotTrackedCardsLocked();
        }

        foreach (CardModel card in tracked)
        {
            MultiEnchantmentApi.RefreshMarkers(card);
        }
    }

    // Snapshots the tracked CardModels. Caller must hold _sync. net9 ConditionalWeakTable is IEnumerable.
    private List<CardModel> SnapshotTrackedCardsLocked()
    {
        List<CardModel> cards = new();
        foreach (KeyValuePair<CardModel, Entry> pair in _states)
        {
            cards.Add(pair.Key);
        }

        return cards;
    }

    // Amount-gated write under the caller-held _sync lock: amount <= 0 removes the entry, otherwise the
    // amount is updated while preserving any per-card override. Returns whether the stored state changed
    // (so callers can skip a redundant refresh/repaint).
    private bool ApplyAmountGatedLocked(CardModel card, int amount)
    {
        if (amount <= 0)
        {
            return _states.Remove(card);
        }

        Entry entry = _states.GetOrCreateValue(card);
        if (entry.Amount == amount)
        {
            return false;
        }

        entry.Amount = amount;
        return true;
    }

    private IEnumerable<MarkerDisplay> GetDisplays(CardModel card)
    {
        int amount;
        IconStateOverride? overrides;
        lock (_sync)
        {
            if (!_states.TryGetValue(card, out Entry? entry))
            {
                yield break;
            }

            amount = entry.Amount;
            overrides = entry.Override;
        }

        yield return new MarkerDisplay
        {
            EnchantmentType = typeof(TMarker),
            Icon = overrides?.Icon ?? _icon,
            Enchantment = overrides?.Enchantment ?? _enchantment,
            PresentationStyle = overrides?.PresentationStyle ?? _presentationStyle,
            ShouldDisplay = _shouldDisplay,
            ShowAmount = overrides?.ShowAmount ?? _showAmount,
            Amount = amount,
            ShowWithLiveEnchantment = overrides?.ShowWithLiveEnchantment ?? _showWithLiveEnchantment,
        };
    }
}

using System;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Marker-terminology surface of <see cref="MultiEnchantmentApi"/>.
/// </summary>
/// <remarks>
/// <para>
/// "Marker" is the unified name (since v2.4.1) for what was previously called "extra icon":
/// card-attached state and badges that are not gameplay enchantments. The old
/// <c>*ExtraIcon*</c> member names were renamed in place — see <c>MIGRATION_V3.md</c> for the
/// rename table.
/// </para>
/// <para>
/// This file also hosts the friendly per-card stored-marker CRUD: <see cref="GetOrAddMarker{TMarker}"/>,
/// <see cref="SetMarker{TMarker}"/>, <see cref="AddMarkerAmount{TMarker}"/>,
/// <see cref="ModifyMarker{TMarker}"/>, and <see cref="RemoveMarker{TMarker}"/>. They operate on
/// stored <see cref="MarkerEnchantmentModel"/> instances (real, persisted card state) and take
/// care of instance creation, mutation notification, and UI refresh so callers never touch the
/// low-level enchant pipeline.
/// </para>
/// </remarks>
public static partial class MultiEnchantmentApi
{
    // --- Invisible enchantment query ------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when <paramref name="enchantmentType"/> is registered as invisible
    /// (no badge icon, never occupies the vanilla primary slot). See
    /// <see cref="EnchantmentAttribute.Invisible"/>.
    /// </summary>
    public static bool IsInvisibleEnchantment(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return Internal.EnchantmentRegistry.IsInvisible(enchantmentType);
    }

    // --- Friendly stored-marker CRUD ------------------------------------------------------------

    /// <summary>
    /// Returns the stored marker of type <typeparamref name="TMarker"/> on <paramref name="card"/>,
    /// creating and attaching one with <paramref name="amount"/> when missing. The marker type must
    /// be registered with the game's <c>ModelDb</c> (a requirement it already has for save/load).
    /// </summary>
    /// <returns>The live marker instance, or <c>null</c> when attaching failed (e.g. vetoed).</returns>
    public static TMarker? GetOrAddMarker<TMarker>(CardModel card, int amount = 1)
        where TMarker : MarkerEnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(card);
        TMarker? existing = GetMarker<TMarker>(card);
        if (existing != null)
        {
            return existing;
        }

        return Enchant(card, CreateMutableMarker<TMarker>(), amount) as TMarker;
    }

    /// <summary>
    /// Sets the stored marker of type <typeparamref name="TMarker"/> on <paramref name="card"/> to
    /// exactly <paramref name="amount"/>, creating the marker when missing. Dependent state and the
    /// icon row refresh automatically.
    /// </summary>
    /// <returns>The live marker instance, or <c>null</c> when attaching failed.</returns>
    public static TMarker? SetMarker<TMarker>(CardModel card, int amount = 1)
        where TMarker : MarkerEnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(card);
        TMarker? existing = GetMarker<TMarker>(card);
        if (existing != null)
        {
            if (existing.Amount != amount)
            {
                existing.SetAmount(amount);
            }

            return existing;
        }

        return Enchant(card, CreateMutableMarker<TMarker>(), amount) as TMarker;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the stored marker's <c>Amount</c>, creating the marker at
    /// <paramref name="delta"/> when missing. Use a negative delta to count down; the marker is NOT
    /// auto-removed at zero — call <see cref="RemoveMarker{TMarker}"/> for that.
    /// </summary>
    /// <returns>The marker's resulting amount, or <c>0</c> when attaching failed.</returns>
    public static int AddMarkerAmount<TMarker>(CardModel card, int delta)
        where TMarker : MarkerEnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(card);
        TMarker? existing = GetMarker<TMarker>(card);
        if (existing != null)
        {
            existing.AddAmount(delta);
            return existing.Amount;
        }

        return (Enchant(card, CreateMutableMarker<TMarker>(), delta) as TMarker)?.Amount ?? 0;
    }

    /// <summary>
    /// Runs <paramref name="mutate"/> against the stored marker of type
    /// <typeparamref name="TMarker"/> when present, then notifies the change (state re-derive +
    /// icon-row refresh). Does nothing when the card has no such marker.
    /// </summary>
    /// <returns><c>true</c> when a marker was found and mutated.</returns>
    public static bool ModifyMarker<TMarker>(CardModel card, Action<TMarker> mutate)
        where TMarker : MarkerEnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(mutate);
        TMarker? existing = GetMarker<TMarker>(card);
        if (existing == null)
        {
            return false;
        }

        mutate(existing);
        existing.NotifyChanged();
        return true;
    }

    /// <summary>
    /// Removes the stored marker of type <typeparamref name="TMarker"/> from
    /// <paramref name="card"/>. Does nothing when the card has no such marker.
    /// </summary>
    /// <returns><c>true</c> when a marker was removed.</returns>
    public static bool RemoveMarker<TMarker>(CardModel card, RemovalReason reason = RemovalReason.Manual)
        where TMarker : MarkerEnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(card);
        TMarker? existing = GetMarker<TMarker>(card);
        return existing != null && RemoveEnchantment(card, existing, reason);
    }

    /// <summary>
    /// Resolves a mutable instance for a marker type from its canonical <c>ModelDb</c> model.
    /// Models must never be constructed directly (the game throws <c>DuplicateModelException</c>
    /// from model constructors), and markers must be <c>ModelDb</c>-registered anyway for
    /// save/load to work.
    /// </summary>
    private static TMarker CreateMutableMarker<TMarker>()
        where TMarker : MarkerEnchantmentModel
    {
        EnchantmentModel? canonical;
        try
        {
            canonical = ModelDb.GetById<EnchantmentModel>(ModelDb.GetId(typeof(TMarker)));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{typeof(TMarker).FullName} is not registered in ModelDb, so no instance can be " +
                "created (and it could not save/load either). Ship the type as mod content so " +
                "the game registers it.",
                ex);
        }

        if (canonical == null)
        {
            throw new InvalidOperationException(
                $"{typeof(TMarker).FullName} resolved to no canonical ModelDb model.");
        }

        return (TMarker)canonical.ToMutable();
    }
}

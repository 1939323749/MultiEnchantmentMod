using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Base class for stored card markers: real enchantment instances that persist with the card
/// (save/load, clone) and carry per-card data via <c>Amount</c>/<c>Props</c>, but are NOT
/// gameplay — no lifecycle hooks, no damage/block/dynamic-var participation, not counted by
/// <c>HasAnyEnchantment</c>. (Formerly <c>ExtraIconEnchantmentModel</c>; renamed in v2.4.1.)
/// </summary>
public abstract class MarkerEnchantmentModel : EnchantmentModel
{
    public override bool HasExtraCardText => false;

    public override bool ShowAmount => false;

    /// <summary>
    /// Call after mutating this marker's <c>Amount</c> or <c>Props</c> directly: re-derives
    /// dependent state and refreshes the icon row on every card UI showing this instance.
    /// Equivalent to <see cref="MultiEnchantmentApi.NotifyPropsChanged"/>.
    /// </summary>
    public void NotifyChanged() => MultiEnchantmentApi.NotifyPropsChanged(this);

    /// <summary>
    /// Sets this marker's <c>Amount</c> and refreshes dependent state/UI in one call.
    /// The instance must be attached to a mutable card.
    /// </summary>
    public void SetAmount(int value)
    {
        Amount = value;
        NotifyChanged();
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to this marker's <c>Amount</c> and refreshes dependent
    /// state/UI in one call. The instance must be attached to a mutable card.
    /// </summary>
    public void AddAmount(int delta)
    {
        Amount += delta;
        NotifyChanged();
    }
}

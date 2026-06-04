using Godot;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Read-only snapshot of an extra-icon marker that is currently visible on a card's icon row.
/// Includes both stored <see cref="ExtraIconEnchantmentModel"/> instances and display-only icons
/// returned by registered providers.
/// </summary>
public sealed record ShownExtraIcon(
    Type EnchantmentType,
    Texture2D Icon,
    int DisplayAmount,
    bool ShowAmount,
    EnchantmentStatus Status,
    EnchantmentPresentationStyle PresentationStyle,
    bool IsDisplayOnly,
    ExtraIconEnchantmentModel? StoredMarker,
    EnchantmentModel? IconSource)
{
    /// <summary>
    /// True when this row came from a live <see cref="ExtraIconEnchantmentModel"/> instance stored
    /// on the card, rather than from a display-only provider.
    /// </summary>
    public bool IsStoredMarker => StoredMarker != null;
}

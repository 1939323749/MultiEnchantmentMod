using Godot;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Per-card display overrides for <see cref="IconState{TMarker}.Show"/>. Each property defaults to
/// <c>null</c>, which means "fall back to the <see cref="IconState{TMarker}"/> constructor value" —
/// you cannot override a field back to <c>null</c>, because <c>null</c> already selects the default.
/// Use this to vary a marker's icon, hover tip, presentation, or amount label per card while sharing
/// a single <see cref="IconState{TMarker}"/> instance.
/// </summary>
public sealed record IconStateOverride
{
    /// <summary>
    /// Custom texture for this card, overriding the state's default icon. As with
    /// <see cref="MarkerDisplay.Icon"/>, this is the only way to use arbitrary art
    /// (<c>EnchantmentModel.Icon</c> is non-virtual).
    /// </summary>
    public Texture2D? Icon { get; init; }

    /// <summary>
    /// Icon/hover source for this card. The render path resolves both the icon fallback and the
    /// marker's hover tips from this enchantment, so supplying a per-card <see cref="Enchantment"/>
    /// yields a per-card tooltip.
    /// </summary>
    public EnchantmentModel? Enchantment { get; init; }

    /// <summary>Presentation style for this card, overriding the state's default.</summary>
    public EnchantmentPresentationStyle? PresentationStyle { get; init; }

    /// <summary>
    /// When set, overrides whether this card's marker renders an amount label. Leave <c>null</c> to
    /// use the state's constructor value.
    /// </summary>
    public bool? ShowAmount { get; init; }

    /// <summary>
    /// When set, overrides whether this card's marker coexists with a live enchantment of the same
    /// type. Leave <c>null</c> to use the state's constructor value.
    /// </summary>
    public bool? ShowWithLiveEnchantment { get; init; }
}

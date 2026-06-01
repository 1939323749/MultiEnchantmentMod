using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Describes an extra icon that should be shown for matching card UI instances even when no live
/// enchantment instance exists on the card.
/// </summary>
public sealed record ExtraIconDisplay
{
    public required Type EnchantmentType { get; init; }

    /// <summary>
    /// The texture to draw, overriding everything else. Use this to supply custom art directly:
    /// <c>EnchantmentModel.Icon</c> is <b>not</b> overridable (it is non-virtual and resolves from a
    /// convention path), so this field is the only way to use an arbitrary texture for a marker
    /// without shipping a file at the model's icon path or Harmony-patching. When null the icon
    /// comes from <see cref="Enchantment"/>, else from <see cref="EnchantmentType"/>'s canonical
    /// model.
    /// </summary>
    public Texture2D? Icon { get; init; }

    public EnchantmentModel? Enchantment { get; init; }

    public EnchantmentPresentationStyle? PresentationStyle { get; init; }

    public ExtraIconDisplayPredicate? ShouldDisplay { get; init; }

    /// <summary>
    /// When <c>true</c>, the icon renders an amount label using <see cref="Amount"/>. Defaults to
    /// <c>false</c>: marker icons are normally amount-less. (<see cref="ExtraIconEnchantmentModel"/>
    /// hard-disables its own <c>ShowAmount</c>, so this is the only way a display-only marker shows
    /// a number.)
    /// </summary>
    public bool ShowAmount { get; init; }

    /// <summary>The number drawn when <see cref="ShowAmount"/> is <c>true</c>. Defaults to 1.</summary>
    public int Amount { get; init; } = 1;

    /// <summary>
    /// By default a display-only icon is suppressed when the card already carries a live enchantment
    /// (or another marker) of <see cref="EnchantmentType"/>, so the real badge wins its slot. Set
    /// this to <c>true</c> to render the marker regardless — e.g. a decorative overlay that should
    /// coexist with the live enchantment of the same type.
    /// </summary>
    public bool ShowWithLiveEnchantment { get; init; }
}

public delegate IEnumerable<ExtraIconDisplay> ExtraIconDisplayProvider(CardModel card);

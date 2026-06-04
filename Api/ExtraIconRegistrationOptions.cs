using Godot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Options for static extra icons registered through
/// <see cref="MultiEnchantmentApi.RegisterExtraIcon{TEnchantment}(System.Func{MegaCrit.Sts2.Core.Models.CardModel,bool},ExtraIconRegistrationOptions?)"/>.
/// Use this when the convenience path is enough, but the marker still needs a custom icon,
/// presentation style, amount label, or same-type live-enchantment coexistence.
/// </summary>
public sealed record ExtraIconRegistrationOptions
{
    public Texture2D? Icon { get; init; }

    public EnchantmentPresentationStyle? PresentationStyle { get; init; }

    public ExtraIconDisplayPredicate? ShouldDisplay { get; init; }

    public bool ShowAmount { get; init; }

    public int Amount { get; init; } = 1;

    public bool ShowWithLiveEnchantment { get; init; }
}

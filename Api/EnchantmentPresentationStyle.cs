namespace MultiEnchantmentMod.Api;

using Godot;

/// <summary>
/// Controls card-UI presentation details for an enchantment.
/// </summary>
public sealed record EnchantmentPresentationStyle
{
    public bool ShowBadgeBacking { get; init; } = true;
    public bool PreserveExtraTextBbCode { get; init; }

    /// <summary>
    /// When <c>true</c>, this enchantment renders in a right-side column instead of the default
    /// left column. The first right-side badge is mirrored across the card's vertical centerline so
    /// it sits symmetric to the energy / star-cost icon, its badge backing is flipped horizontally,
    /// and subsequent right-side badges stack downward. The right column ignores the vanilla
    /// no-star-cost vertical shift (<c>NCard</c>'s 45px star-label offset), so it stays put
    /// regardless of the card's star cost. Left- and right-side badges form two independent columns.
    /// </summary>
    public bool RightAligned { get; init; }
    public float IconScale { get; init; } = 1f;
    public Vector2 IconOffset { get; init; } = Vector2.Zero;
    public Color? IconTint { get; init; }
    public Color? DisabledIconTint { get; init; }
    public Texture2D? BadgeBackingTexture { get; init; }
    public bool HideWhenDisabled { get; init; }
    public int DisplayPriority { get; init; }
}

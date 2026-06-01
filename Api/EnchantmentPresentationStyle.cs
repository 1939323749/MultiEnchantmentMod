namespace MultiEnchantmentMod.Api;

using Godot;

/// <summary>
/// Controls card-UI presentation details for an enchantment.
/// </summary>
public sealed record EnchantmentPresentationStyle
{
    public bool ShowBadgeBacking { get; init; } = true;
    public bool PreserveExtraTextBbCode { get; init; }
    public float IconScale { get; init; } = 1f;
    public Vector2 IconOffset { get; init; } = Vector2.Zero;
    public Color? IconTint { get; init; }
    public Color? DisabledIconTint { get; init; }
    public Texture2D? BadgeBackingTexture { get; init; }
    public bool HideWhenDisabled { get; init; }
    public int DisplayPriority { get; init; }
}

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Shared defaults for marker-style enchantments that render as lightweight card extra icons.
/// </summary>
public static class ExtraIconPresentation
{
    public const int DefaultDisplayPriority = 1000;

    public static EnchantmentPresentationStyle Default => new()
    {
        ShowBadgeBacking = false,
        HideWhenDisabled = true,
        DisplayPriority = DefaultDisplayPriority,
    };
}

using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Base class for enchantment-backed card markers that should behave like lightweight extra icons.
/// </summary>
public abstract class ExtraIconEnchantmentModel : EnchantmentModel
{
    public override bool HasExtraCardText => false;

    public override bool ShowAmount => false;
}

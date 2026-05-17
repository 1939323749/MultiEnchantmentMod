using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace MultiEnchantmentMod.Samples;

// Compatibility — third-party enchantment that never calls MultiEnchantmentApi.Register.
//
// Goal: demonstrate the auto-registration safety net. SampleUnregistered has NO [Enchantment]
// attribute, NO companion EnchantmentDefinition<T>, and NO fluent Register<T>() call. It does,
// however, override one of vanilla's value-modifier virtuals (EnchantDamageAdditive). The first
// time MultiEnchantmentSupport encounters an instance of this type on a card, the registry's
// auto-detection kicks in:
//
//   1. Namespace check — class is not under MegaCrit.Sts2.* → eligible for auto-detection.
//   2. Reflection check — overrides EnchantDamage* / EnchantBlock* → register as MergeAmount.
//   3. One-time info log records the auto-registration so the author sees it.
//
// Result: applying SampleUnregistered to a card twice merges into one badge with Amount = 2,
// and the card's damage increases by 5 * Amount = 10. To opt out of the auto-default, the author
// (or a downstream mod) can call MultiEnchantmentApi.Register<SampleUnregistered>().Stack(
// StackBehavior.DisallowDuplicate, ...).Commit() before any card sees the type.

public sealed class SampleUnregistered : EnchantmentModel
{
    public override bool ShowAmount => true;

    public override decimal EnchantDamageAdditive(decimal damage, ValueProp props)
    {
        _ = damage;
        _ = props;
        return 5m * Amount;
    }
}

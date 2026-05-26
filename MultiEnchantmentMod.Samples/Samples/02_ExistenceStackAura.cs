using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A — attribute-only, ExistenceStack flavor.
//
// Goal: show how to model a one-shot "presence aura" enchantment.
//
// StackBehavior.ExistenceStack means: the second-and-onward application keeps a bookkeeping
// instance around (so re-enchanting doesn't refuse) but only the first instance's OnEnchant
// fires. Combine with StatusAggregation.AnyInstanceCountsAsOne so the UI shows the badge as
// long as any instance is active.
[Enchantment(Stack = StackBehavior.ExistenceStack, Status = StatusAggregation.AnyInstanceCountsAsOne)]
public sealed class SampleRetainAura : EnchantmentModel
{
    protected override void OnEnchant()
    {
        // This mutation runs exactly once even if the player re-enchants the card with the
        // same type later. The mod's stacking registry handles the "second copy is a no-op"
        // accounting in the background.
        Card.AddKeyword(CardKeyword.Retain);
    }
}

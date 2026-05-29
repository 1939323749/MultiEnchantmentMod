using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — companion EnchantmentDefinition<T> subclass.
//
// Goal: show how to react to merge applications with custom side effects.
//
// This sample lowers the card's energy cost by 1 every time it's merged
// again. Without a custom OnMergedDelta, the merge would just bump Amount and never replay
// OnEnchant — so the energy cost wouldn't keep dropping. We override OnMergedDelta to apply
// the per-application side effect directly.
//
// The companion class is the canonical place for this kind of logic because it's strongly
// typed: the override receives a SampleCostReducer instance instead of EnchantmentModel.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleCostReducer : EnchantmentModel
{
    protected override void OnEnchant()
    {
        // First application lowers the cost once; merges go through OnMergedDelta below.
        Card.EnergyCost.UpgradeBy(-1);
    }
}

public sealed class SampleCostReducerDefinition : EnchantmentDefinition<SampleCostReducer>
{
    protected override void OnMergedDelta(SampleCostReducer enchantment, int addedAmount)
    {
        for (int i = 0; i < addedAmount; i++)
        {
            enchantment.Card.EnergyCost.UpgradeBy(-1);
        }
    }
}

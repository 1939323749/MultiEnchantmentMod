using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A / B — keyword tracking.
//
// Goal: show two equivalent ways to declare that an enchantment adds (or removes) a card
// keyword for the duration of its presence.
//
// Path 1 — attribute only. KeywordEvalMode.PerInstance contributes the snapshot's live
// instance count to the keyword's running total; the mod's keyword-refresh pass adds the
// keyword to the card whenever the sum is positive.

[Enchantment(Stack = StackBehavior.DuplicateInstance, Status = StatusAggregation.PerInstanceOwned)]
[EnchantmentKeyword(CardKeyword.Exhaust, Mode = KeywordEvalMode.PerInstance)]
public sealed class SampleExhaustAdder : EnchantmentModel
{
    // No code needed — the [EnchantmentKeyword] attribute does the wiring.
}

// Path 2 — companion class override. Use when the contribution depends on something more
// nuanced than "instance count" / "merged amount" / "constant". The companion class's
// KeywordSourceAmount can inspect the snapshot freely.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
[EnchantmentKeyword(CardKeyword.Exhaust, Mode = KeywordEvalMode.Custom)]
public sealed class SampleConditionalExhaust : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public sealed class SampleConditionalExhaustDefinition : EnchantmentDefinition<SampleConditionalExhaust>
{
    protected override int KeywordSourceAmount(
        global::MultiEnchantmentMod.EnchantmentStackSnapshot snapshot,
        CardKeyword keyword)
    {
        if (keyword != CardKeyword.Exhaust)
        {
            return 0;
        }

        // Only contribute Exhaust once the merged total reaches 3.
        return snapshot.ActiveTotalAmount >= 3 ? 1 : 0;
    }
}

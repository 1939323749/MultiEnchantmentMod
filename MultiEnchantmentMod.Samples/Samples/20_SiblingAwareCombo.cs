using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — companion EnchantmentDefinition<T> using the sibling-event hooks.
//
// Goal: demonstrate the OnSiblingApplied / OnSiblingRemoved callbacks, which fire when an
// enchantment's *neighbor on the same card* is added or removed. These hooks let two
// enchantments form a "combo" without the second one having to know about the first via a
// global registry — discovery is local to the card.
//
// Behavior:
//   • OnSiblingApplied fires AFTER the new sibling has been attached (so calling
//     MultiEnchantmentApi.GetSiblings(...) sees it).
//   • OnSiblingRemoved fires BEFORE the sibling is detached, and only if the OnRemoved
//     veto pipeline has accepted the removal — so a vetoed removal does NOT fire this.
//   • Self-events do NOT echo: SampleResonator does not see its own OnApplied / OnRemoved
//     forwarded as a sibling event on itself.
//   • Like all lifecycle callbacks, these are gated by IsActive on the receiver — a dormant
//     ConditionalActive enchantment will not be notified.
//
// Use cases:
//   • Combo enchantments ("if this card also has Frost Shard, gain extra block").
//   • Mutual-exclusion patterns ("the moment Curse is applied, self-remove").
//   • Aggregate UI: a "summary" enchantment that recomputes its tooltip based on neighbors.
//
// This sample shows the simplest combo pattern: SampleResonator buffs itself by +1 stack
// every time another enchantment lands on the same card, and refunds the buff when a
// neighbor leaves.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleResonator : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

public sealed class SampleResonatorDefinition : EnchantmentDefinition<SampleResonator>
{
    protected override void OnSiblingApplied(
        CardModel card,
        SampleResonator self,
        EnchantmentModel newSibling)
    {
        _ = card;

        // Don't react to the same family — the Resonator only resonates with FOREIGN
        // enchantments, not other Resonator instances (which would inflate a
        // DuplicateInstance scenario).
        if (newSibling is SampleResonator)
        {
            return;
        }

        self.Amount++;

        // If this card's tooltip / visual slices read self.Amount, refresh now so the UI
        // reflects the new value immediately. (For MergeAmount enchantments the runtime
        // already does this on the application path; the call below is the explicit
        // pattern when an author adjusts Amount from a callback.)
        MultiEnchantmentApi.NotifyPropsChanged(self);
    }

    protected override void OnSiblingRemoved(
        CardModel card,
        SampleResonator self,
        EnchantmentModel removedSibling,
        RemovalReason reason)
    {
        _ = card;
        _ = reason;

        if (removedSibling is SampleResonator)
        {
            return;
        }

        // Refund the buff. Floor at zero so we never go negative if the resonator was
        // applied AFTER its neighbors (in which case the OnSiblingApplied count was zero
        // at the time and we'd otherwise underflow).
        self.Amount = System.Math.Max(0, self.Amount - 1);
        MultiEnchantmentApi.NotifyPropsChanged(self);
    }

    protected override void OnApplied(CardModel card, SampleResonator enchantment)
    {
        // Cold-start case: when the resonator is applied to a card that already carries
        // other enchantments, the OnSiblingApplied stream fires only for FUTURE neighbors.
        // Seed the count from the existing neighbors so the buff matches the steady state.
        int existingNeighbors = 0;
        foreach (EnchantmentModel sibling in MultiEnchantmentApi.GetSiblings(card, enchantment))
        {
            if (sibling is not SampleResonator)
            {
                existingNeighbors++;
            }
        }

        enchantment.Amount = System.Math.Max(enchantment.Amount, existingNeighbors);
    }
}

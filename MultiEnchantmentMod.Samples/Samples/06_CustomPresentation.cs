using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — custom presentation overrides.
//
// Goal: show how to control the extra card-text rendering and visual slice layout for an
// enchantment.
//
// When the default amount-driven extra text doesn't match the enchantment's gameplay (e.g.
// the merged Amount is fed into a non-linear formula), override TryFormatExtraText to provide
// the displayed string directly. GetVisualSliceAmounts is the same idea for the per-badge
// breakdown rendered on the card.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleNonlinearEnchantment : EnchantmentModel
{
    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;
}

[EnchantmentPresentation(HasExtraText = true, HasVisualSliceOverride = true)]
public sealed class SampleNonlinearDefinition : EnchantmentDefinition<SampleNonlinearEnchantment>
{
    protected override bool TryFormatExtraText(
        global::MultiEnchantmentMod.EnchantmentStackSnapshot snapshot,
        string defaultText,
        out string formattedText)
    {
        int total = snapshot.ActiveTotalAmount;
        // Quadratic scaling — display the computed effective value instead of the raw count.
        formattedText = $"Effective bonus: {total * total}";
        return true;
    }

    protected override IReadOnlyList<int>? GetVisualSliceAmounts(
        global::MultiEnchantmentMod.EnchantmentStackSnapshot snapshot)
    {
        // Render one fat badge per "tier" (every 3 applications). Anything that doesn't
        // perfectly divide the total returns null to let the default per-slice computation
        // take over.
        int total = snapshot.ActiveTotalAmount;
        if (total <= 0 || total % 3 != 0)
        {
            return null;
        }

        int tiers = total / 3;
        var slices = new int[tiers];
        for (int i = 0; i < tiers; i++)
        {
            slices[i] = 3;
        }

        return slices;
    }
}

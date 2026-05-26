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
// breakdown rendered on the card. If each badge also needs an independent active/disabled state
// or icon, override GetVisualSlices instead.

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
        EnchantmentStackSnapshot snapshot,
        string defaultText,
        out string formattedText)
    {
        int total = snapshot.ActiveTotalAmount;
        // Quadratic scaling — display the computed effective value instead of the raw count.
        formattedText = $"Effective bonus: {total * total}";
        return true;
    }

    protected override IReadOnlyList<int>? GetVisualSliceAmounts(
        EnchantmentStackSnapshot snapshot)
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

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleAlternatingBadges : EnchantmentModel
{
    // Two badges still render even though the label is hidden. ShowAmount only controls whether
    // the numeric amount is drawn inside each badge; it does not control slice creation.
    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;
}

[EnchantmentPresentation(HasVisualSliceOverride = true)]
public sealed class SampleAlternatingBadgesDefinition : EnchantmentDefinition<SampleAlternatingBadges>
{
    protected override IReadOnlyList<EnchantmentVisualSlice>? GetVisualSlices(
        EnchantmentStackSnapshot snapshot)
    {
        int round = snapshot.Card?.CombatState?.RoundNumber ?? 1;
        bool oddRound = round % 2 == 1;

        return new[]
        {
            oddRound
                ? EnchantmentVisualSlice.Active(1).WithIcon<SampleNonlinearEnchantment>()
                : EnchantmentVisualSlice.Disabled(1).WithIcon<SampleNonlinearEnchantment>(),
            oddRound
                ? EnchantmentVisualSlice.Disabled(1)
                : EnchantmentVisualSlice.Active(1),
        };
    }

    protected override bool TryFormatExtraText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText)
    {
        int round = snapshot.Card?.CombatState?.RoundNumber ?? 1;
        bool oddRound = round % 2 == 1;
        formattedText = oddRound ? "获得1点能量" : "抽1张牌";
        return true;
    }
}

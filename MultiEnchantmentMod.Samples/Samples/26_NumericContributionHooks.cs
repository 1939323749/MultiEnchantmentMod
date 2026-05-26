using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A/C — numeric contribution hooks for combat cost and card play count.
//
// Goal: replace ad-hoc RecalculateValues/SetCustomBaseCost and old ModifyCardPlayCount overrides
// with fold-style contribution channels.
//
// Why this matters:
//   ShieldPlating/SwordArt-style "cost +1" effects should compose. If two enchantments both call
//   SetCustomBaseCost(canonical + 1), the second can overwrite the first. The contribution hook
//   receives the running cost and returns the next running cost, so +1 and +1 become +2.
//
//   ExtraHit-style "play count +1" effects should also compose. Returning current + stacks keeps
//   multiple mods in the same fold instead of racing over a vanilla override.
//
// Do not also override vanilla TryModifyEnergyCostInCombat/ModifyCardPlayCount on the same
// enchantment; both channels run and would double-count.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleHeavyPlating : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    [ModifyEnergyCost]
    public decimal AddCombatCost(EnchantmentStackSnapshot snapshot, decimal currentCost)
    {
        // +1 cost per active stack. The callback receives the running cost after vanilla and
        // earlier enchantments have contributed.
        return currentCost + snapshot.ActiveTotalAmount;
    }
}

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleExtraSwing : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    [ModifyCardPlayCount]
    public int AddPlayCount(EnchantmentStackSnapshot snapshot, int currentPlayCount)
    {
        // +1 play per active stack. This mirrors an "extra hit / replay" style enchantment.
        return currentPlayCount + snapshot.ActiveTotalAmount;
    }
}

public sealed class SampleNumericContributionRegistrationOnly : EnchantmentModel
{
    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;
}

public static class SampleNumericContributionRegistration
{
    private static System.IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleNumericContributionRegistrationOnly>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .ModifyEnergyCostInCombat((snapshot, currentCost) => currentCost - snapshot.ActiveTotalAmount)
            .ModifyCardPlayCount((snapshot, currentPlayCount) => currentPlayCount + snapshot.ActiveTotalAmount)
            .FormatExtraText((EnchantmentStackSnapshot snapshot, string defaultText, out string formatted) =>
            {
                _ = defaultText;
                formatted = $"Combat cost -{snapshot.ActiveTotalAmount}; play count +{snapshot.ActiveTotalAmount}.";
                return true;
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

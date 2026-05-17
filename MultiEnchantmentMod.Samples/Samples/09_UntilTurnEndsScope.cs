using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A — attribute-only, UntilTurnEnds scope.
//
// Goal: declare an enchantment that lives for exactly one player turn.
//
// Scope = ScopeKind.UntilTurnEnds: the mod's Hook.AfterTurnEnd patch (filtered on
// CombatSide.Player) removes the enchantment with RemovalReason.TurnEnded right after the
// player turn wraps up. Pairs naturally with MergeAmount so re-applications mid-turn fold
// into the same instance instead of stacking separate one-turn copies.
[Enchantment(
    Stack = StackBehavior.MergeAmount,
    Status = StatusAggregation.SharedAcrossStack,
    Scope = ScopeKind.UntilTurnEnds)]
public sealed class SampleSingleTurnSharpen : EnchantmentModel
{
    public override bool ShowAmount => true;
}

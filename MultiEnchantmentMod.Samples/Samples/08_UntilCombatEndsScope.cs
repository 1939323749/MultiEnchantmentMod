using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A — attribute-only, UntilCombatEnds scope.
//
// Goal: declare an enchantment that is auto-removed at the end of the current combat.
//
// Scope = ScopeKind.UntilCombatEnds wires the registration so that the mod's
// Hook.AfterCombatEnd patch removes this enchantment from every affected card at end-of-
// combat, with RemovalReason.CombatEnded. Use it for "this fight only" buffs. Permanent and
// ConditionalActive scopes survive end-of-combat cleanup; everything else is wiped.
[Enchantment(
    Stack = StackBehavior.MergeAmount,
    Status = StatusAggregation.SharedAcrossStack,
    Scope = ScopeKind.UntilCombatEnds)]
public sealed class SampleCombatGuard : EnchantmentModel
{
    public override bool ShowAmount => true;
}

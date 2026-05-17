using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — companion EnchantmentDefinition<T>, full lifecycle override surface.
//
// Goal: show how a companion class subscribes to every lifecycle callback the mod exposes,
// declares a non-default scope, and uses the OnRemoved return value to veto a removal.
//
// The base EnchantmentDefinition<TEnchantment> provides no-op defaults for all six callbacks
// plus a Permanent scope. Subclasses override only the members of interest:
//   - Scope               → declares the lifetime (Permanent / UntilCombatEnds / ...).
//   - OnApplied           → runs once when the enchantment is freshly attached. Not invoked
//                           for save-restore / clone paths, which preserve runtime state.
//   - OnRemoved           → return true to allow, false to veto. CardCleared bypasses veto.
//   - OnCombatStart / End → fired by the mod's Hook.BeforeCombatStart / Hook.AfterCombatEnd
//                           patches for every enchantment, regardless of scope.
//   - OnTurnStart / End   → fired by SetupPlayerTurn and Hook.AfterTurnEnd (player turn only).
//
// All callback exceptions are caught by the mod and logged so a buggy handler cannot kill the
// combat loop.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleLifecycleEnchantment : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public sealed class SampleLifecycleEnchantmentDefinition
    : EnchantmentDefinition<SampleLifecycleEnchantment>
{
    public override EnchantmentScope Scope => EnchantmentScope.UntilCombatEnds;

    protected override void OnApplied(CardModel card, SampleLifecycleEnchantment enchantment)
    {
        _ = card;
        _ = enchantment;
    }

    protected override bool OnRemoved(
        CardModel card,
        SampleLifecycleEnchantment enchantment,
        RemovalReason reason)
    {
        _ = card;
        _ = enchantment;
        // Veto manual removals; everything else (including the scope-driven CombatEnded
        // sweep at end-of-combat) proceeds normally.
        return reason != RemovalReason.Manual;
    }

    protected override void OnCombatStart(
        CardModel card,
        SampleLifecycleEnchantment enchantment)
    {
        _ = card;
        _ = enchantment;
    }

    protected override void OnCombatEnd(
        CardModel card,
        SampleLifecycleEnchantment enchantment)
    {
        // Last chance to inspect state before the end-of-combat scope sweep removes the
        // UntilCombatEnds enchantment.
        _ = card;
        _ = enchantment;
    }

    protected override void OnTurnStart(
        CardModel card,
        SampleLifecycleEnchantment enchantment)
    {
        _ = card;
        _ = enchantment;
    }

    protected override void OnTurnEnd(
        CardModel card,
        SampleLifecycleEnchantment enchantment)
    {
        _ = card;
        _ = enchantment;
    }
}

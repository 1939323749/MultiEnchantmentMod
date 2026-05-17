using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A / B — declaring a contribution to a real card dynamic variable via [ModifyDynamicVar].
//
// Goal: two enchantments both modify the SAME card-level dynamic variable, the values compose in
// "card application order × per-enchantment registration order", and the order is sensitive —
// "PlusFive then Doubler" = (base+5)*2 ≠ (base*2)+5 = "Doubler then PlusFive".
//
// These samples target the {damage} dynamic variable so the effect shows up immediately on a
// vanilla Strike (or any other attack card) without needing to ship a custom card. Try:
//
//   dev console (Strike has base damage 6):
//     enchant SAMPLE_PLUS_FIVE 1        → 11   (6 + 5)
//     enchant SAMPLE_DOUBLER 1          → 12   (6 × 2)
//     enchant SAMPLE_DOUBLER 2          → 24   (per-slice: 6 × 2 × 2 — equivalent to two enchants)
//     enchant SAMPLE_PLUS_FIVE 1 then enchant SAMPLE_DOUBLER 1     → 22   ((6 + 5) × 2)
//     enchant SAMPLE_DOUBLER 1 then enchant SAMPLE_PLUS_FIVE 1     → 17   ((6 × 2) + 5)
//     enchant SAMPLE_DOUBLER 1 then enchant SAMPLE_PLUS_FIVE 1 then enchant SAMPLE_DOUBLER 1
//         → 34  (((6 × 2) + 5) × 2)
//
// MergeAmount stacking is per-slice: applying the same enchantment twice (`enchant ... 2` or
// two separate `enchant` calls) invokes the contribution twice with single-slice snapshots whose
// TotalAmount = 1 each. That's why `current * 2m` doubles per stack rather than scaling once by
// the total. Authors don't need Math.Pow(2, n) — the pipeline iterates for you.
//
// SamplePlusFive uses the Tier A path: the [ModifyDynamicVar] attribute on a method of the
// [Enchantment]-tagged model class. The assembly scanner picks the method up, validates its
// signature, and registers the contribution.
//
// SampleDoubler uses the Tier B path: a companion EnchantmentDefinition<T> hosts the
// [ModifyDynamicVar] method. The definition's DynamicVarContributions virtual scans both the
// definition class and the enchantment-model class for tagged methods.
//
// Warning to mod authors: do NOT pair ModifyDynamicVar("damage", …) with an EnchantDamageAdditive
// override on the same enchantment — the two channels stack and produce double-counted output.
// Pick exactly one for any given key. Damage / block contributions via ModifyDynamicVar layer
// AFTER the legacy EnchantDamage*/EnchantBlock* pipeline, on top of the value those produce.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SamplePlusFive : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    // [ModifyDynamicVar] requires return type `decimal` and parameters
    // `(EnchantmentStackSnapshot, decimal)`. The scanner converts the instance method into an
    // open delegate that ignores `this` — treat the method as pure-stateless code over the
    // snapshot + current value.
    //
    // Per-slice semantics: with MergeAmount, the pipeline calls this method once per merged
    // stack. The simplest formula `current + 5m` therefore adds 5 per stack — no manual scaling
    // by TotalAmount needed. The snapshot is the per-slice view, so its TotalAmount equals the
    // slice amount (typically 1) — it's a useful tag when you need per-stack metadata, not a
    // multiplier the author has to apply.
    [ModifyDynamicVar("damage")]
    public decimal AddFive(EnchantmentStackSnapshot snapshot, decimal current)
    {
        _ = snapshot;
        return current + 5m;
    }
}

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleDoubler : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

public sealed class SampleDoublerDefinition : EnchantmentDefinition<SampleDoubler>
{
    [ModifyDynamicVar("damage")]
    public decimal Double(EnchantmentStackSnapshot snapshot, decimal current)
    {
        _ = snapshot;
        return current * 2m;
    }
}

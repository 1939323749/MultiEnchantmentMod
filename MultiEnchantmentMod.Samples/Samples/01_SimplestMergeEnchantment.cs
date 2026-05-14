using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A — attribute-only.
//
// Goal: show the minimum code for a mergeable enchantment.
//
// The [Enchantment] attribute encodes the stacking contract. The scanner picks the type up
// during MultiEnchantmentApi.ScanCallingAssembly() (call it from your [ModInitializer]).
// No companion EnchantmentDefinition<T>, no manual MultiEnchantmentApi.Register<...>() — the
// attribute IS the registration.
//
// This sample defines a throwaway enchantment that grants 2× Amount block. It is NOT
// registered with the game's ModelDb; the file is here purely as a compilable reference.
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleFrostShard : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
        new[] { new BlockVar(2m, ValueProp.Move) };

    public override void RecalculateValues()
    {
        // Each merged stack adds 2 block.
        DynamicVars.Block.BaseValue = Amount * 2;
    }
}

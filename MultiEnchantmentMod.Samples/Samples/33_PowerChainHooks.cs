using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B/C — power-chain bridges: ModifyPowerAmountGiven + OnCardAppliedPower.
//
// Goal: let an enchantment amplify and observe the powers its host card applies, without a
// Harmony patch on Hook.ModifyPowerAmountGiven / PowerCmd.
//
// Why this matters:
//   "This card applies +1 Vulnerable per stack" was previously impossible through the API —
//   vanilla's power hook iterates combat listeners (creatures / relics / powers) and never
//   consults card enchantments. The contribution channel folds over the running amount AFTER
//   vanilla's additive/multiplicative pipeline, so relic effects still apply first and multiple
//   enchantments compose (+1 and +1 become +2).
//
//   OnCardAppliedPower then fires once per resolved power application (delta != 0) with the
//   final amount, applier, and target — useful for charge counters or combo bookkeeping.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleHexAmplifier : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    /// <summary>Total debuff stacks this enchantment has amplified — shown via extra text.</summary>
    public int AmplifiedTotal { get; set; }
}

public sealed class SampleHexAmplifierDefinition : EnchantmentDefinition<SampleHexAmplifier>
{
    protected override System.Collections.Generic.IEnumerable<PowerAmountGivenContribution> PowerAmountGivenContributions
    {
        get
        {
            // +1 debuff layer per active stack whenever the host card applies a debuff to an
            // enemy. Buffs (e.g. the card granting the player Strength) pass through untouched.
            yield return (snapshot, context, current) =>
                context.Power.Type == PowerType.Debuff && context.Target != context.Giver
                    ? current + snapshot.ActiveTotalAmount
                    : current;
        }
    }

    protected override void OnCardAppliedPower(
        MegaCrit.Sts2.Core.Models.CardModel card,
        SampleHexAmplifier enchantment,
        PowerAppliedContext context)
    {
        // Fires after the application fully resolved; context.Amount is the final delta
        // (including our own contribution above).
        if (context.Power.Type != PowerType.Debuff)
        {
            return;
        }

        enchantment.AmplifiedTotal += (int)context.Amount;
        SampleRegistration.Logger.Info(
            $"[Samples] HexAmplifier saw {card.Id} apply {context.Power.GetType().Name} " +
            $"x{context.Amount} to {context.Target.Name} (running total {enchantment.AmplifiedTotal}).");
    }

    protected override bool TryFormatExtraText(
        EnchantmentStackSnapshot snapshot,
        string defaultText,
        out string formattedText)
    {
        _ = defaultText;
        formattedText = $"Debuffs this card applies gain +{snapshot.ActiveTotalAmount} stack(s).";
        return true;
    }
}

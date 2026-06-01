using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — stack-aware async hooks.
//
// Goal: show the hooks that run once per enchantment type with an EnchantmentStackSnapshot.
//
// Use these instead of legacy OnPlay execution modes when the effect needs one deliberate
// decision over the whole stack:
//   - one prompt instead of N prompts
//   - one animation, then an amount scaled by ActiveTotalAmount
//   - one random target decision, or N random decisions chosen explicitly by the author
//   - reacting to true DamageResult values after damage has resolved
//   - listening to "any card was drawn" after this card turned the listener on
//   - reacting immediately when this card is re-enchanted through EnchantAsync / CopyEnchantment
//
// The sample stores counters rather than issuing game commands so it remains safe in every test
// environment. Replace the marked comments with CreatureCmd/CardSelectCmd/PowerCmd calls in a
// real downstream enchantment.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleStackedTactics : EnchantmentModel
{
    public bool ListeningForDrawsThisTurn { get; set; }
    public int PlaysResolved { get; set; }
    public int DrawsObservedThisTurn { get; set; }
    public decimal DamageObservedThisCombat { get; set; }
    public int ReenchantEventsSeen { get; set; }

    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

[EnchantmentPresentation(HasExtraText = true)]
public sealed class SampleStackedTacticsDefinition : EnchantmentDefinition<SampleStackedTactics>
{
    protected override Task BeforeCardPlayedStacked(StackedBeforeCardPlayedContext context)
    {
        if (context.CardPlay.Card != context.Snapshot.Card)
        {
            return Task.CompletedTask;
        }

        var enchantment = (SampleStackedTactics)context.Snapshot.AnchorInstance;

        // EagerPerAttackEnergy-style hook: inspect the pending CardPlay before costs/effects
        // resolve, then apply one aggregated result. This sample records intent only.
        enchantment.PlaysResolved += context.Snapshot.ActiveTotalAmount;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
        return Task.CompletedTask;
    }

    protected override Task OnPlayStacked(StackedOnPlayContext context)
    {
        var enchantment = (SampleStackedTactics)context.Snapshot.AnchorInstance;

        // SurvivorDiscard-style pattern:
        //   var count = Math.Min(context.Snapshot.ActiveTotalAmount, hand.Count);
        //   prompt ONCE for count cards, then discard/exhaust those choices.
        //
        // SwordArt-style pattern:
        //   choose whether ActiveTotalAmount means one target with N damage, or N random rolls.
        enchantment.ListeningForDrawsThisTurn = true;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
        return Task.CompletedTask;
    }

    protected override Task AfterCardPlayedStacked(StackedAfterCardPlayedContext context)
    {
        var enchantment = (SampleStackedTactics)context.Snapshot.AnchorInstance;

        // CorrosiveWave-style setup point: the host card has finished playing, so enable a
        // short-lived listener for later draw events in the same turn.
        enchantment.ListeningForDrawsThisTurn = true;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
        return Task.CompletedTask;
    }

    protected override Task AfterAnyCardDrawnStacked(StackedAfterCardDrawnContext context)
    {
        var enchantment = (SampleStackedTactics)context.Snapshot.AnchorInstance;
        if (!enchantment.ListeningForDrawsThisTurn)
        {
            return Task.CompletedTask;
        }

        if (context.DrawnCard == context.Snapshot.Card)
        {
            return Task.CompletedTask;
        }

        // CorrosiveWave/ForgeWave-style hook: this fires for every card drawn in combat, not only
        // when the host card is drawn. Apply one aggregated effect per drawn card.
        enchantment.DrawsObservedThisTurn += context.Snapshot.ActiveTotalAmount;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
        return Task.CompletedTask;
    }

    protected override Task AfterSiblingAppliedStacked(StackedAfterSiblingAppliedContext context)
    {
        var enchantment = (SampleStackedTactics)context.Snapshot.AnchorInstance;
        if (context.NewSibling == enchantment)
        {
            return Task.CompletedTask;
        }

        // Re-enchant/trigger-style hook: only pre-existing siblings are notified, so this is a
        // good place to auto-play or schedule commands when a card gains another enchantment.
        enchantment.ReenchantEventsSeen += context.Snapshot.ActiveTotalAmount;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
        return Task.CompletedTask;
    }

    protected override Task BeforeFlushStacked(StackedBeforeFlushContext context)
    {
        var enchantment = (SampleStackedTactics)context.Snapshot.AnchorInstance;

        // ChoiceContext is currently null in the vanilla bridge. Treat BeforeFlushStacked as
        // cleanup/state-reset only; do not open selection UI or run commands that need a
        // PlayerChoiceContext here.
        enchantment.ListeningForDrawsThisTurn = false;
        enchantment.DrawsObservedThisTurn = 0;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
        return Task.CompletedTask;
    }

    protected override Task AfterDamageGivenStacked(StackedAfterDamageGivenContext context)
    {
        if (context.CardSource != context.Snapshot.Card ||
            !context.Props.IsPoweredAttack() ||
            context.Result.TotalDamage <= 0)
        {
            return Task.CompletedTask;
        }

        var enchantment = (SampleStackedTactics)context.Snapshot.AnchorInstance;

        // Reaper/Feed-style hook: this sees the actual DamageResult, including final damage and
        // whether the target died. Scale by the stack snapshot only after reading the true result.
        enchantment.DamageObservedThisCombat +=
            context.Result.TotalDamage * context.Snapshot.ActiveTotalAmount;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
        return Task.CompletedTask;
    }

    protected override bool TryFormatExtraText(
        EnchantmentStackSnapshot snapshot,
        string defaultText,
        out string formattedText)
    {
        _ = defaultText;
        var enchantment = (SampleStackedTactics)snapshot.AnchorInstance;
        formattedText =
            $"plays={enchantment.PlaysResolved}, reenchant={enchantment.ReenchantEventsSeen}, drawn={enchantment.DrawsObservedThisTurn}, damage={enchantment.DamageObservedThisCombat}";
        return true;
    }
}

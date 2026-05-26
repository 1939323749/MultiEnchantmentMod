using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier A/B — HookExecutionMode for legacy EnchantmentModel hooks.
//
// Goal: show when Execution(...) is still the right tool.
//
// Prefer the newer stack-aware hooks (sample 27) for prompts, random targets, animations, or
// async commands that should be aggregated once per enchantment type. Execution modes are mainly
// for existing/vanilla-style overrides such as EnchantmentModel.OnPlay(), AfterCardDrawn(), or
// BeforeFlush(), where the framework must decide how many times to invoke the old hook when one
// card has multiple logical stacks.
//
// Mental model:
//   MergeAmount default       -> MergedTotal          (Amount 3 runs old OnPlay 3 times)
//   DuplicateInstance default -> PerLiveInstance      (three real instances run once each)
//   ExistenceStack default    -> FirstActiveInstance  (presence effect runs once)
//
// If your old hook already reads Amount and scales itself, use PerLiveInstance to avoid Amount^2.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
[EnchantmentExecution(OnPlay = global::MultiEnchantmentMod.HookExecutionMode.PerLiveInstance)]
public sealed class SampleScaledOnPlayOnce : EnchantmentModel
{
    public int TotalTriggers { get; private set; }

    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    public override Task OnPlay(PlayerChoiceContext choiceContext, CardPlay? cardPlay)
    {
        _ = choiceContext;
        _ = cardPlay;

        // Because this method already scales by Amount, it must run once for the merged anchor.
        // With the MergeAmount default (MergedTotal), Amount=3 would add 9 instead of 3.
        TotalTriggers += Amount;
        return Task.CompletedTask;
    }
}

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
[EnchantmentExecution(OnPlay = global::MultiEnchantmentMod.HookExecutionMode.MergedTotal)]
public sealed class SamplePerStackOnPlay : EnchantmentModel
{
    public int TotalTriggers { get; private set; }

    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    public override Task OnPlay(PlayerChoiceContext choiceContext, CardPlay? cardPlay)
    {
        _ = choiceContext;
        _ = cardPlay;

        // This method is a true per-stack effect: one invocation contributes one unit.
        // MergedTotal is therefore correct, and is also the MergeAmount default.
        TotalTriggers++;
        return Task.CompletedTask;
    }
}

[EnchantmentPresentation(HasExtraText = true)]
public sealed class SampleExecutionModeDefinition : EnchantmentDefinition<SamplePerStackOnPlay>
{
    protected override bool TryFormatExtraText(
        EnchantmentStackSnapshot snapshot,
        string defaultText,
        out string formattedText)
    {
        _ = defaultText;
        var enchantment = (SamplePerStackOnPlay)snapshot.AnchorInstance;
        formattedText = $"Legacy OnPlay has fired {enchantment.TotalTriggers} time(s).";
        return true;
    }
}

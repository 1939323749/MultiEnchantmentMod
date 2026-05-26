using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — lower-frequency public surfaces in one place.
//
// Goal: cover the framework edges that authors do not need every day but should know exist:
//   - battle-history display customization
//   - OnRestored for save/multiplayer rehydration
//   - broadcast hooks for any drawn/exhausted/discarded card
//   - block-gain hooks
//
// This sample intentionally logs/counters only. Real mods can swap the counters for cache rebuild,
// tooltip state, or command scheduling as appropriate.

[Enchantment(
    Stack = StackBehavior.DisallowDuplicate,
    Status = StatusAggregation.AnyInstanceCountsAsOne,
    HistoryDisplay = HistoryDisplayMode.CustomGroup,
    HistoryGroupHeader = "Sample Framework Events")]
public sealed class SampleFrameworkEvents : EnchantmentModel
{
    public int DrawnCardsSeen { get; set; }
    public int ExhaustedCardsSeen { get; set; }
    public int DiscardedCardsSeen { get; set; }
    public int BlockEventsSeen { get; set; }
    public bool RestoredFromSaveOrPacket { get; set; }

    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;
}

[EnchantmentPresentation(HasExtraText = true)]
public sealed class SampleFrameworkEventsDefinition : EnchantmentDefinition<SampleFrameworkEvents>
{
    public override HistoryDisplayMode HistoryDisplay => HistoryDisplayMode.CustomGroup;
    public override string? HistoryGroupHeader => "Sample Framework Events";

    protected override string? FormatHistoryText(string cardTitle, string enchantmentTitle)
    {
        return $"{cardTitle}: {enchantmentTitle} tracked framework events";
    }

    protected override void OnRestored(CardModel card, SampleFrameworkEvents enchantment)
    {
        _ = card;

        // Rebuild runtime-only caches here. OnApplied is not called for save restore or
        // multiplayer packet reattachment because the instance already exists logically.
        enchantment.RestoredFromSaveOrPacket = true;
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
    }

    protected override void OnAnyCardDrawn(
        CardModel drawnCard,
        CardModel selfCard,
        SampleFrameworkEvents enchantment)
    {
        _ = drawnCard;
        _ = selfCard;
        enchantment.DrawnCardsSeen++;
    }

    protected override void OnAnyCardExhausted(
        CardModel exhaustedCard,
        CardModel selfCard,
        SampleFrameworkEvents enchantment)
    {
        _ = exhaustedCard;
        _ = selfCard;
        enchantment.ExhaustedCardsSeen++;
    }

    protected override void OnAnyCardDiscarded(
        CardModel discardedCard,
        CardModel selfCard,
        SampleFrameworkEvents enchantment)
    {
        _ = discardedCard;
        _ = selfCard;
        enchantment.DiscardedCardsSeen++;
    }

    protected override void OnBeforeBlockGained(
        CardModel card,
        SampleFrameworkEvents enchantment,
        BlockGainContext context)
    {
        _ = card;
        _ = context;
        enchantment.BlockEventsSeen++;
    }

    protected override void OnBlockGained(
        CardModel card,
        SampleFrameworkEvents enchantment,
        BlockGainContext context)
    {
        _ = card;
        _ = context;
        enchantment.BlockEventsSeen++;
    }

    protected override bool TryFormatExtraText(
        EnchantmentStackSnapshot snapshot,
        string defaultText,
        out string formattedText)
    {
        _ = defaultText;
        var enchantment = (SampleFrameworkEvents)snapshot.AnchorInstance;
        formattedText =
            $"draw={enchantment.DrawnCardsSeen}, exhaust={enchantment.ExhaustedCardsSeen}, discard={enchantment.DiscardedCardsSeen}, block={enchantment.BlockEventsSeen}";
        return true;
    }
}

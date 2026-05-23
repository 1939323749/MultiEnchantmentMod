using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — companion EnchantmentDefinition<T> using the broadcast OnAnyCard* hook.
//
// Goal: demonstrate the difference between the per-card OnCardPlayed hook (fires only when
// the host card itself is played) and the broadcast OnAnyCardPlayed hook (fires for every
// card played in the combat, regardless of which card carries the enchantment).
//
// Use cases for the broadcast variant:
//   • "While this card is in play, every OTHER card you play also gains +1 damage."
//   • "Every time any attack card is played, this card's stack grows."
//   • Counters that observe the entire combat tempo (cards played per turn, etc.).
//
// API note: the broadcast hooks are *opt-in*. They are NOT triggered by overriding the
// per-card OnCardPlayed virtual — you must override OnAnyCardPlayed (or its sibling
// OnAnyCardDrawn / OnAnyCardExhausted / OnAnyCardDiscarded) explicitly. This keeps the
// dispatch cost off the hot path for the 90% of enchantments that only care about their
// own card.
//
// IsActive gating still applies: an inactive (e.g. ConditionalActive predicate failed,
// dormant scope) enchantment receives no broadcasts.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleTempoTracker : EnchantmentModel
{
    /// <summary>
    /// Number of cards played in the current combat (broadcast-counted, including self).
    /// Reset per combat by OnCombatStart.
    /// </summary>
    public int CardsPlayedThisCombat { get; set; }

    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

public sealed class SampleTempoTrackerDefinition : EnchantmentDefinition<SampleTempoTracker>
{
    protected override void OnCombatStart(CardModel card, SampleTempoTracker enchantment)
    {
        _ = card;
        enchantment.CardsPlayedThisCombat = 0;
    }

    protected override void OnAnyCardPlayed(
        CardModel playedCard,
        CardModel selfCard,
        SampleTempoTracker enchantment)
    {
        // playedCard — the card that just resolved.
        // selfCard   — the card carrying this enchantment (always the same as enchantment.Card).
        //
        // We deliberately do NOT short-circuit on ReferenceEquals(playedCard, selfCard) — the
        // broadcast hook intentionally fires for every play, including self, so authors can
        // count "total cards played" without having to also wire OnCardPlayed.
        _ = playedCard;
        _ = selfCard;

        enchantment.CardsPlayedThisCombat++;

        // The counter feeds presentation (extra text below) and could feed any number of
        // gameplay derivations — e.g. EnchantDamageAdditive returning Amount * tempoCount,
        // or an OnPlay handler that triggers a bonus once a threshold is reached.
        //
        // If the value drives any *cached* derivation (visual slice amounts, dynamic var
        // contributions read by the UI), call MultiEnchantmentApi.NotifyPropsChanged here
        // so the next render picks up the new tempo. See sample 22 for that pattern.
    }
}

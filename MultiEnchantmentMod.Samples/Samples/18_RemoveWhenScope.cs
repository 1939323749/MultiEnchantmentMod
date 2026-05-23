using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent builder, RemoveWhen (predicate-triggered permanent removal).
//
// Goal: demonstrate how RemoveWhen differs from ConditionalActive (Sample 12).
//
//   ConditionalActive  — the enchantment *stays attached* but goes inactive while the
//                        predicate is false; it reactivates when conditions flip back.
//   RemoveWhen         — when the predicate returns true on a designated trigger, the
//                        enchantment is *permanently removed* (RemovalReason.ConditionMet).
//                        It will not reappear unless explicitly re-applied.
//
// Predicates are pure functions — they are not serialised and are re-hydrated from the
// registry on load / multiplayer sync. Do not close over mutable state or DateTime.Now.
//
// File layout:
//   18a. SampleFragileBoost   — bonus damage, removed when card is exhausted (single trigger)
//   18b. SampleOverconfidence — bonus damage, removed when HP drops to ≤ 50 % (multi-trigger)

// ─────────────────────────────────────────────────────────────────────────────
// 18a. SampleFragileBoost
//      "Gain +Amount×3 damage. Removed permanently when this card is exhausted."
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SampleFragileBoost : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public static class SampleFragileBoostRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleFragileBoost>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            // Remove permanently when the host card is exhausted.
            // Contrast with WhenActive(...): that would only suppress the effect while the
            // card sits in the exhaust pile — this RemoveWhen ensures it is gone for good.
            .RemoveWhen(
                (card, enchantment) =>
                {
                    _ = enchantment;
                    return card.Pile?.Type == PileType.Exhaust;
                },
                ActivationTrigger.AfterCardExhausted)
            .ModifyDynamicVar("damage", (snapshot, current) => current + snapshot.TotalAmount * 3)
            .OnRemoved((card, enchantment, reason) =>
            {
                _ = enchantment;
                if (reason == RemovalReason.ConditionMet)
                {
                    SampleRegistration.Logger.Info(
                        $"[FragileBoost] Removed from {card.Id}: card was exhausted (ConditionMet).");
                }
                return true; // always allow removal
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 18b. SampleOverconfidence
//      "+5 damage while owner is above half HP. Removed permanently when HP ≤ 50 %."
//
// Uses two ActivationTrigger values so the predicate is checked both right after
// damage is received and again at the end of each player turn (catching DoT / bleed
// HP loss that does not fire AfterDamageReceived on the card's owner).
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SampleOverconfidence : EnchantmentModel
{
    public override bool ShowAmount => false;
}

public static class SampleOverconfidenceRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleOverconfidence>()
            .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
            // Two triggers: re-evaluate after direct damage and at each player turn end.
            // Passing multiple triggers is equivalent to registering the same predicate on
            // each trigger independently — whichever fires first and returns true removes
            // the enchantment; subsequent triggers are never evaluated for that instance.
            .RemoveWhen(
                (card, enchantment) =>
                {
                    _ = enchantment;
                    var creature = card.Owner?.Creature;
                    return creature != null && creature.CurrentHp <= creature.MaxHp / 2;
                },
                ActivationTrigger.AfterDamageReceived,
                ActivationTrigger.AfterPlayerTurnEnd)
            .ModifyDynamicVar("damage", (snapshot, current) =>
            {
                _ = snapshot;
                return current + 5;
            })
            .OnRemoved((card, enchantment, reason) =>
            {
                _ = enchantment;
                if (reason == RemovalReason.ConditionMet)
                {
                    SampleRegistration.Logger.Info(
                        $"[Overconfidence] Removed from {card.Id}: owner HP fell to ≤ 50 %.");
                }
                return true;
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

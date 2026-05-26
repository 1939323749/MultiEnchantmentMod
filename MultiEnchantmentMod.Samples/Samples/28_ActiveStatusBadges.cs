using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — WhenActiveStatus versus WhenActive.
//
// Goal: show how to make an enchantment sleep AND dim its badge without consuming the lifetime
// scope slot.
//
// WhenActive(...) is a scope shorthand: it gates gameplay, but it owns the scope slot. Use it for
// simple "active only while in hand" effects when you do not also need UntilCombatEnds,
// LingerForTurns, RemoveWhen, etc.
//
// WhenActiveStatus(...) only controls active/disabled status. It composes with normal scopes and
// drives visual dimming, keyword/dynamic-var gating, and lifecycle gating.

public sealed class SampleHandStatusSharpen : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

public static class SampleHandStatusSharpenRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleHandStatusSharpen>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .LingerForTurns(2)
            .WhenActiveStatus((card, enchantment) =>
            {
                _ = enchantment;
                return card.Pile?.Type == PileType.Hand;
            })
            .ModifyDynamicVar("damage", (snapshot, current) => current + snapshot.ActiveTotalAmount)
            .FormatExtraText((EnchantmentStackSnapshot snapshot, string defaultText, out string formatted) =>
            {
                _ = defaultText;
                formatted = snapshot.ActiveTotalAmount > 0
                    ? $"While in hand: [gold]damage[/gold] +{snapshot.ActiveTotalAmount}."
                    : "Dormant outside the hand.";
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

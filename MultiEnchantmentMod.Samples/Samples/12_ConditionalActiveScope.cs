using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent builder, ConditionalActive (predicate-gated activity).
//
// Goal: keep the enchantment attached permanently but make it contribute to gameplay only
// while a predicate returns true.
//
// WhenActive(predicate) installs an EnchantmentScope.ConditionalActive. The mod short-
// circuits IsActive(card, enchantment) inside ApplyDamageEnchantments, ApplyBlockEnchantments
// and the OnPlay dispatch, so an inactive enchantment is invisible to the runtime — yet it
// stays attached and re-activates the moment the predicate flips back to true. There is no
// removal: the predicate is the only gate.
//
// The predicate runs on every check, so it must be cheap. Exceptions inside the predicate are
// caught and logged by the mod and the enchantment falls back to "active" so a buggy
// predicate cannot silently disable a working enchantment.

public sealed class SampleHandOnlySharpen : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public static class SampleHandOnlySharpenRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleHandOnlySharpen>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .WhenActive((card, enchantment) =>
            {
                _ = enchantment;
                // Active only while the host card sits in the player's hand. Off-hand piles
                // (draw, discard, exhaust, ...) suppress the enchantment without removing it.
                return card.Pile?.Type == PileType.Hand;
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

using System;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent builder, LingerForTurns + OnRemoved diagnostic.
//
// Goal: demonstrate the multi-turn linger lifetime and distinguish between expiry-by-turn-
// limit vs expiry-by-combat-end inside the removal callback.
//
// LingerForTurns(2) gives the enchantment a TurnsRemaining counter that ticks on
// Hook.AfterTurnEnd (player turn only). When it reaches zero the mod removes the enchantment
// with RemovalReason.TurnLimitReached. If combat ends sooner the reason is CombatEnded
// instead. The OnRemoved callback receives the reason so the mod author can branch on it
// (e.g. play a different VFX, refund a resource only on natural expiry, etc.).

public sealed class SampleLingeringSharpen : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public static class SampleLingeringSharpenRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleLingeringSharpen>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .LingerForTurns(2)
            .OnRemoved<SampleLingeringSharpen>((card, enchantment, reason) =>
            {
                // Return true to allow removal. Returning false vetoes — except for
                // RemovalReason.CardCleared, which always proceeds because the host card is
                // being torn down.
                _ = card;
                _ = enchantment;
                _ = reason;
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

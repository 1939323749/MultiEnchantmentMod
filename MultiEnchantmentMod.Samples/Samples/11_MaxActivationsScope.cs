using System;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent builder, MaxActivations + one-shot removal veto.
//
// Goal: demonstrate a finite use-budget and how to veto a single removal attempt.
//
// MaxActivations(3, OnPlay): the mod's OnPlay loop calls NoteActivation after each play and
// bumps an internal ActivationCount on the runtime scope state. When the count reaches the
// configured cap, the enchantment is queued for removal with
// RemovalReason.ActivationLimitReached and flushed at the end of the OnPlay pass.
//
// The OnRemoved handler below demonstrates a single-use veto: the first time the cap is hit
// the handler returns false, which keeps the enchantment alive. Subsequent removals are
// allowed. Veto is honored for every RemovalReason except CardCleared.

public sealed class SampleChargedSharpen : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public static class SampleChargedSharpenRegistration
{
    private static IDisposable? _registration;
    private static bool _vetoedOnce;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleChargedSharpen>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .MaxActivations(3, ActivationTrigger.OnPlay)
            .OnRemoved<SampleChargedSharpen>((card, enchantment, reason) =>
            {
                _ = card;
                _ = enchantment;
                if (reason == RemovalReason.ActivationLimitReached && !_vetoedOnce)
                {
                    _vetoedOnce = true;
                    return false; // refuse this removal, give the enchantment another life
                }

                return true;
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
        _vetoedOnce = false;
    }
}

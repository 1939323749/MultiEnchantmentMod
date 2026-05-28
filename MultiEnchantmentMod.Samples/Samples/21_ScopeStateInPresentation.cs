using System;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent registration consuming ScopeRuntimeStateView from a snapshot.
//
// Goal: demonstrate how to surface scope state (activation counts, turns remaining) in the
// card's tooltip without touching internal scope plumbing. The snapshot's
// EnchantmentStackSnapshot.ScopeStates dictionary (populated whenever a snapshot is taken)
// exposes a read-only ScopeRuntimeStateView per enchantment instance — the same shape the
// runtime uses for its own decisions, but immutable.
//
// What this enchantment does:
//   • MaxActivations(3, OnPlay) — auto-removed after the third play.
//   • Each play adds +5 to "damage" via [ModifyDynamicVar].
//   • The tooltip says "Charges remaining: N" — N comes from ScopeStates / IsLimitReached.
//
// Reading scope state at presentation time:
//   1. FormatExtraText receives the snapshot.
//   2. snapshot.StateOf(snapshot.AnchorInstance) returns a ScopeRuntimeStateView (or null
//      for permanent-scope enchantments).
//   3. For MaxActivationsScope, view.ActivationCount and view.Scope's Max give us the
//      remaining-charge count.
//
// The snapshot is recomputed each time the UI redraws the card (cheap — see
// MultiEnchantmentStackSupport.GetSnapshot), so the tooltip always reflects current state.

public sealed class SampleChargedSurge : EnchantmentModel
{
    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;
}

public static class SampleChargedSurgeRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleChargedSurge>()
            .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.AnyInstanceCountsAsOne)
            .MaxActivations(3, ActivationTrigger.OnPlay)
            .ModifyDynamicVar("damage", (snapshot, current) =>
            {
                _ = snapshot;
                return current + 5m;
            })
            .FormatExtraText((EnchantmentStackSnapshot snapshot, string defaultText, out string formatted) =>
            {
                _ = defaultText;

                // The anchor instance is the "primary" enchantment of this slice (for
                // DisallowDuplicate that's the only instance). Look up its scope state.
                ScopeRuntimeStateView? view = snapshot.StateOf(snapshot.AnchorInstance);

                int remaining;
                if (view?.Scope is EnchantmentScope.MaxActivationsScope max)
                {
                    // Negative results clamp to zero — once IsLimitReached, the runtime is
                    // already queueing removal, but the UI may still render one frame.
                    remaining = Math.Max(0, max.Max - view.ActivationCount);
                }
                else
                {
                    // ScopeStates was null (older snapshot path) or the registered scope
                    // changed at runtime. Fall back to a safe value.
                    remaining = 3;
                }

                formatted = $"伤害 +5。剩余 {remaining} 次释放。";
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

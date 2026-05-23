using System;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent registration demonstrating MaxInstances + StackOverflowPolicy.
//
// Goal: cap a DuplicateInstance / ExistenceStack enchantment at N live instances and
// configure what happens when the cap is exceeded.
//
// Why this exists:
//   Without a cap, a card that gets re-enchanted from a frequently-firing relic (every
//   play, every draw, every block) will accrue thousands of EnchantmentModel instances over
//   a single combat. Each one consumes memory, costs a slot in the dynamic-var contribution
//   list, and bloats UI rendering. MaxInstances + StackOverflowPolicy gives authors three
//   defensive options:
//
//     • Reject (default)       — the new application is dropped. Existing instances unchanged.
//     • ReplaceOldest (FIFO)   — the oldest instance is removed first, then the new one is
//                                attached. Reason on the evicted: RemovalReason.OverflowEvicted.
//     • ReplaceNewest (LIFO)   — the most recently added instance is removed before the
//                                new one attaches. Useful for "always refresh the top stack".
//
// MaxInstances applies only to behaviors where instance count grows per application:
// DuplicateInstance and ExistenceStack. It is ignored by DisallowDuplicate (always at
// most 1) and MergeAmount (single instance, slices grow via Amount delta).
//
// Configuration here uses the new Stack(StackDefinition) overload, which takes a full
// StackDefinition record so MaxInstances and OnOverflow can be set alongside the basic
// Behavior / Status pair. The classic Stack(behavior, status) overload still works for
// the common case where neither is needed.

public sealed class SampleBoundedQueue : EnchantmentModel
{
    public override bool ShowAmount => false;
}

public static class SampleBoundedQueueRegistration
{
    private const int MaxLiveInstances = 5;

    private static IDisposable? _registration;

    public static void Install()
    {
        StackDefinition definition = new(
            StackBehavior.DuplicateInstance,
            StatusAggregation.PerInstanceOwned)
        {
            // Hard cap at 5 live instances per card.
            MaxInstances = MaxLiveInstances,
            // FIFO eviction: when a 6th application comes in, the oldest live instance is
            // removed (with RemovalReason.OverflowEvicted) so the count stays at 5.
            OnOverflow = StackOverflowPolicy.ReplaceOldest,
        };

        _registration ??= MultiEnchantmentApi.Register<SampleBoundedQueue>()
            .Stack(definition)
            .OnRemoved<SampleBoundedQueue>((card, enchantment, reason) =>
            {
                _ = card;
                _ = enchantment;

                // Distinguish overflow eviction from "natural" removals (combat end, scope
                // expiry, etc.) — useful for emitting an animation only on FIFO churn, or
                // for refusing the eviction in a special case.
                if (reason == RemovalReason.OverflowEvicted)
                {
                    SampleRegistration.Logger.Info(
                        "[SampleBoundedQueue] FIFO eviction; an older instance was replaced by a new application.");
                }

                return true; // never veto
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

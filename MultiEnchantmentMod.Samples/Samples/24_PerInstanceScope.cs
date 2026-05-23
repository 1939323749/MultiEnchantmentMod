using System;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — per-application scope override.
//
// Goal: demonstrate that the same enchantment type can keep a permanent registry default while
// individual applications opt into a shorter lifetime through MultiEnchantmentApi.Enchant(...,
// scopeOverride: ...), and already-attached instances can be retargeted with SetScopeOverride.
//
// Usage from another sample / console helper:
//   MultiEnchantmentApi.Enchant(card, new SampleFlexibleScope(), 1, EnchantmentScope.UntilCombatEnds);
//   MultiEnchantmentApi.SetScopeOverride(card, card.Enchantment!, EnchantmentScope.UntilTurnEnds);
//
// Predicate-bearing scopes (ConditionalActive / RemoveWhen) are intentionally rejected by those
// APIs because they cannot be serialized across save/load or multiplayer peers.

public sealed class SampleFlexibleScope : EnchantmentModel
{
    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;
}

public static class SampleFlexibleScopeRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleFlexibleScope>()
            .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.AnyInstanceCountsAsOne)
            // Registry default stays permanent. Per-instance applications may override this.
            .WithScope(EnchantmentScope.Permanent)
            .FormatExtraText((EnchantmentStackSnapshot snapshot, string defaultText, out string formatted) =>
            {
                _ = defaultText;
                ScopeRuntimeStateView? view = snapshot.StateOf(snapshot.AnchorInstance);
                if (view == null)
                {
                    formatted = "Permanent by default; no runtime scope state yet.";
                    return true;
                }

                string suffix = view.HasOverride ? "override" : "registry default";
                formatted = view.Scope switch
                {
                    EnchantmentScope.UntilCombatEndsScope => $"Scope: until combat ends ({suffix}).",
                    EnchantmentScope.UntilTurnEndsScope => $"Scope: until turn ends ({suffix}).",
                    EnchantmentScope.LingerForTurnsScope => $"Scope: {view.TurnsRemaining} turn(s) remaining ({suffix}).",
                    EnchantmentScope.MaxActivationsScope max => $"Scope: {Math.Max(0, max.Max - view.ActivationCount)} activation(s) remaining ({suffix}).",
                    _ => $"Scope: permanent ({suffix}).",
                };
                return true;
            })
            .OnApplied<SampleFlexibleScope>((card, enchantment) =>
            {
                ScopeRuntimeStateView? view = MultiEnchantmentApi.GetScopeState(enchantment);
                SampleRegistration.Logger.Info(
                    $"[SampleFlexibleScope] Applied to {card.Id}; hasOverride={view?.HasOverride ?? false}.");
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

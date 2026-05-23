using System;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent registration that mutates internal state from a callback and explicitly
// requests a UI refresh via MultiEnchantmentApi.NotifyPropsChanged.
//
// Goal: demonstrate the explicit-refresh pattern for derived state.
//
// Background:
//   The framework rebuilds visual slices, extra-card-text, and dynamic-var contributions
//   automatically on the *application path* — i.e. whenever an enchantment is added,
//   removed, merged, or replaced. But callbacks like OnAfterDamageReceived, OnTurnEnd, and
//   OnAnyCardPlayed mutate state IN PLACE without going through the application path, so
//   the UI keeps showing yesterday's value until the next render-triggering event.
//
// MultiEnchantmentApi.NotifyPropsChanged(enchantment) is the explicit "I changed something
// the UI cares about — recompute now" handle. It's a no-op for permanent-scope MergeAmount
// enchantments where Amount feeds directly into the snapshot (the runtime already ticks),
// but for the patterns below it's required:
//
//   1. Custom backing fields that feed FormatExtraText / VisualSlices / ModifyDynamicVar.
//   2. Derived state computed from external models (HP, energy, deck composition).
//   3. State changed inside non-application callbacks (OnAfterDamageReceived shown here).
//
// This sample shows a "berserk" enchantment that scales damage with how much HP the owner
// has lost. After taking damage, it bumps an internal counter and notifies the framework
// so the tooltip's "+N damage" text updates the same frame.

public sealed class SampleBerserk : EnchantmentModel
{
    /// <summary>
    /// Cached "missing HP" delta the enchantment was last computed against. Drives the
    /// damage bonus surfaced through EnchantDamageAdditive and the tooltip via
    /// FormatExtraText. Authoritative source is the owner's creature; this value is only a
    /// snapshot updated from OnAfterDamageReceived.
    /// </summary>
    public int LastObservedMissingHp { get; set; }

    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;

    public override decimal EnchantDamageAdditive(decimal originalDamage, MegaCrit.Sts2.Core.ValueProps.ValueProp props)
    {
        _ = originalDamage;
        _ = props;
        // +1 base damage for every 2 missing HP. The cached value is updated from the
        // OnAfterDamageReceived hook below (see registration).
        return LastObservedMissingHp / 2;
    }
}

public static class SampleBerserkRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleBerserk>()
            .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.AnyInstanceCountsAsOne)
            .OnAfterDamageReceived<SampleBerserk>((card, enchantment, ctx) =>
            {
                // ctx.Target is the creature that took damage; we only react when our
                // OWNER took the damage (otherwise other creatures' hits would mutate our
                // state). For the player-owned sample card this is the player.
                if (card.Owner?.Creature != ctx.Target)
                {
                    return;
                }

                int missingHp = (int)Math.Max(0m, ctx.Target.MaxHp - ctx.Target.CurrentHp);
                if (missingHp == enchantment.LastObservedMissingHp)
                {
                    return; // no observable change → no UI work needed
                }

                enchantment.LastObservedMissingHp = missingHp;

                // Tell the framework the derived state changed. Without this call, the
                // "+N damage" tooltip will continue to show the value from the most recent
                // application-path event (OnApplied, OnCombatStart, etc.).
                //
                // NotifyPropsChanged refreshes:
                //   • Visual slice amounts (for ShowAmount-bearing enchantments).
                //   • Extra card text (FormatExtraText).
                //   • Dynamic var contributions (ModifyDynamicVar) — though these are read
                //     on demand and don't strictly need the notification, the framework
                //     uses it to bust any per-frame cache that downstream UI maintains.
                MultiEnchantmentApi.NotifyPropsChanged(enchantment);
            })
            .OnCombatStart<SampleBerserk>((card, enchantment) =>
            {
                _ = card;
                enchantment.LastObservedMissingHp = 0;
                MultiEnchantmentApi.NotifyPropsChanged(enchantment);
            })
            .FormatExtraText((EnchantmentStackSnapshot snapshot, string defaultText, out string formatted) =>
            {
                _ = defaultText;
                var berserk = (SampleBerserk)snapshot.AnchorInstance;
                int bonus = berserk.LastObservedMissingHp / 2;
                formatted = $"已损失 {berserk.LastObservedMissingHp} 点生命：[gold]伤害[/gold] +{bonus}。";
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

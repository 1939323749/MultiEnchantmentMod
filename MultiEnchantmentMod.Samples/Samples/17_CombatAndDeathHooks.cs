using System;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B & C — combat-flow and death-guard lifecycle hooks.
//
// Goal: demonstrate OnSideTurnStart (combat side detection), OnShouldDie (death veto), and
// ConditionalActive (predicate-gated activity). These hooks cover the "combat flow" category
// that the mod exposes through its Hook.BeforeSideTurnStart / Hook.AfterSideTurnStart /
// Hook.ShouldDie patches. All are gated by IsActive — inactive enchantments are skipped.
//
// File layout:
//   2a. SampleChargeUp     — Tier B, OnSideTurnStart(Enemy) + MergeAmount
//   2b. SamplePhoenix      — Tier C, OnShouldDie (death veto + self-removal)
//   2c. SampleFullHpShield — Tier C, ConditionalActive + [ModifyDynamicVar]

// ─────────────────────────────────────────────────────────────────────────────
// 2a. SampleChargeUp — OnSideTurnStart(CombatSide.Enemy) + MergeAmount
//     "Each enemy turn, gain +1 stack (+2 base damage per stack)."
// ─────────────────────────────────────────────────────────────────────────────

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleChargeUp : EnchantmentModel
{
    private const decimal DamagePerStack = 2m;

    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    public override decimal EnchantDamageAdditive(decimal originalDamage, ValueProp props)
    {
        if (!props.IsPoweredAttack())
            return 0m;

        // Each stack adds +2 damage. Amount auto-increments via OnSideTurnStart below.
        return Amount * DamagePerStack;
    }
}

public sealed class SampleChargeUpDefinition : EnchantmentDefinition<SampleChargeUp>
{
    protected override void OnSideTurnStart(
        CardModel card,
        SampleChargeUp enchantment,
        CombatSide side)
    {
        _ = card;

        if (side == CombatSide.Enemy)
        {
            // Each enemy turn adds a permanent stack. The next attack's
            // EnchantDamageAdditive will pick up the new Amount automatically.
            enchantment.Amount++;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2b. SamplePhoenix — OnShouldDie (death veto + self-removal)
//     "A lethal hit is prevented; heal 50% MaxHp. Enchantment removes itself."
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SamplePhoenix : EnchantmentModel { }

public static class SamplePhoenixRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SamplePhoenix>()
            .OnShouldDie<SamplePhoenix>((card, enchantment, creature) =>
            {
                _ = creature;

                // Heal the owner for 50% of their maximum HP.
                int healAmount = (int)(card.Owner?.Creature?.MaxHp ?? 0) / 2;
                // Integration point — exact heal API.
                // Example: CreatureCmd.Heal(card.Owner!.Creature, healAmount);

                // Remove this enchantment — the phoenix burns up saving the owner.
                MultiEnchantmentApi.RemoveEnchantment(
                    card, enchantment, RemovalReason.ActivationLimitReached);

                return false; // veto: prevent death
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
// 2c. SampleFullHpShield — ConditionalActive + [ModifyDynamicVar]
//     "+5 Block while at full HP; deactivates (not removed) when HP drops."
// ─────────────────────────────────────────────────────────────────────────────

[Enchantment(Stack = StackBehavior.DisallowDuplicate)]
public sealed class SampleFullHpShield : EnchantmentModel
{
    [ModifyDynamicVar("block")]
    public decimal AddBlock(EnchantmentStackSnapshot snapshot, decimal current)
    {
        _ = snapshot;
        return current + 5m;
    }
}

public sealed class SampleFullHpShieldDefinition : EnchantmentDefinition<SampleFullHpShield>
{
    protected override bool ShouldBeActive(CardModel card, SampleFullHpShield enchantment)
    {
        _ = enchantment;
        return (card.Owner?.Creature?.CurrentHp ?? 0) >= (card.Owner?.Creature?.MaxHp ?? 1);
    }
}

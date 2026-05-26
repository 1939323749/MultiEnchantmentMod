using System;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Lifecycle / vanilla-event-bridge capability surface of <see cref="IEnchantmentRegistration"/>.
/// Covers attachment lifecycle (<c>OnApplied</c> / <c>OnRemoved</c> / <c>OnRestored</c>), per-card
/// vanilla event bridges (<c>OnCardPlayed</c> / <c>OnCardDrawn</c> / etc.), broadcast variants
/// (<c>OnAnyCard*</c>), combat-flow hooks (<c>OnTurnStart</c>, <c>OnSideTurnStart</c>,
/// <c>OnBeforeAttack</c>), damage / block / death guards, and sibling-attachment notifications.
/// </summary>
public interface ILifecycleRegistration
{
    IEnchantmentRegistration OnApplied(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnRemoved(Func<CardModel, EnchantmentModel, RemovalReason, bool> handler);
    IEnchantmentRegistration OnRestored(Action<CardModel, EnchantmentModel> handler);

    IEnchantmentRegistration OnCombatStart(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCombatEnd(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnTurnStart(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnTurnEnd(Action<CardModel, EnchantmentModel> handler);

    IEnchantmentRegistration OnCardPlayed(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCardDrawn(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCardExhausted(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCardDiscarded(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCardEnteredCombat(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCardChangedPiles(Action<CardModel, EnchantmentModel, PileType, AbstractModel?> handler);
    IEnchantmentRegistration OnCardRetained(Action<CardModel, EnchantmentModel> handler);

    IEnchantmentRegistration OnAnyCardPlayed(Action<CardModel, CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnAnyCardDrawn(Action<CardModel, CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnAnyCardExhausted(Action<CardModel, CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnAnyCardDiscarded(Action<CardModel, CardModel, EnchantmentModel> handler);

    IEnchantmentRegistration OnSiblingApplied(Action<CardModel, EnchantmentModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnSiblingRemoved(Action<CardModel, EnchantmentModel, EnchantmentModel, RemovalReason> handler);

    IEnchantmentRegistration OnAfterDamageReceived(Action<CardModel, EnchantmentModel, DamageReceivedContext> handler);
    IEnchantmentRegistration OnSideTurnStart(Action<CardModel, EnchantmentModel, CombatSide> handler);
    IEnchantmentRegistration OnBeforeSideTurnStart(Action<CardModel, EnchantmentModel, CombatSide> handler);
    IEnchantmentRegistration OnBeforeAttack(Action<CardModel, EnchantmentModel, AttackCommand> handler);
    IEnchantmentRegistration OnAfterAttack(Action<CardModel, EnchantmentModel, AttackCommand> handler);
    IEnchantmentRegistration OnBeforeBlockGained(Action<CardModel, EnchantmentModel, BlockGainContext> handler);
    IEnchantmentRegistration OnBlockGained(Action<CardModel, EnchantmentModel, BlockGainContext> handler);
    IEnchantmentRegistration OnShouldDie(Func<CardModel, EnchantmentModel, Creature, bool> handler);
}

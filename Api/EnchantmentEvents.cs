using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Published on ALL enchant paths (sync and async) after an enchantment is successfully attached
/// to a card. Subscribe via <see cref="MultiEnchantmentApi.Subscribe{TEvent}(System.Action{TEvent})"/>.
/// </summary>
public sealed record EnchantmentAppliedEvent(
    CardModel Card,
    EnchantmentModel Enchantment,
    int Amount);

/// <summary>
/// Published when an enchantment is removed from a card (via <see cref="MultiEnchantmentApi.RemoveEnchantment"/>
/// or scope expiry). Subscribe via <see cref="MultiEnchantmentApi.Subscribe{TEvent}(System.Action{TEvent})"/>.
/// </summary>
public sealed record EnchantmentRemovedEvent(
    CardModel Card,
    EnchantmentModel Enchantment,
    RemovalReason Reason);

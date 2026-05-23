using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Payload delivered to lifecycle handlers bridging vanilla
/// <c>Hook.AfterDamageReceived</c> via <c>OnAfterDamageReceived</c>. Bundles the four fields a
/// handler typically needs:
/// <list type="bullet">
///   <item><description><see cref="Target"/> — the creature that just took damage (the owner of
///   the receiving enchantment's card when the lifecycle fires).</description></item>
///   <item><description><see cref="Result"/> — vanilla's resolved damage breakdown
///   (blocked / unblocked / total / overkill).</description></item>
///   <item><description><see cref="Dealer"/> — creature that delivered the damage, when one
///   exists (status damage etc. may have no dealer).</description></item>
///   <item><description><see cref="Source"/> — the card responsible, when one exists (relic /
///   trait damage may have no source card).</description></item>
/// </list>
/// Passed as <c>sealed record</c> so handlers can use positional destructuring and the
/// dispatcher can extend the contract in the future without touching every handler signature.
/// </summary>
public sealed record DamageReceivedContext(
    Creature Target,
    DamageResult Result,
    Creature? Dealer,
    CardModel? Source);

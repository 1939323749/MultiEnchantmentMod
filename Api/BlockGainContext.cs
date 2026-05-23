using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Payload for <c>OnBeforeBlockGained</c> / <c>OnBlockGained</c>. Bridges vanilla
/// <c>Hook.BeforeBlockGained</c> / <c>Hook.AfterBlockGained</c>. The vanilla hooks deliver
/// <c>(CombatState, Creature, decimal amount, ValueProp props, CardModel? source)</c>;
/// we omit <c>ValueProp</c> (an internal pipeline marker rarely useful at the lifecycle layer)
/// and pass the remaining three as a record so the handler signature stays at three parameters
/// regardless of future Vanilla additions.
/// </summary>
public sealed record BlockGainContext(Creature Creature, decimal Amount, CardModel? Source);

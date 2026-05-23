using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// In-memory representation of one v2 registration. Built up by <see cref="EnchantmentRegistration"/>
/// during fluent <c>.Stack(...).OnMergedDelta(...).Commit()</c> chains, then translated by the
/// adapter into the legacy <c>MultiEnchantmentStackApi</c> provider tables.
/// </summary>
internal sealed class EnchantmentEntry
{
    public required Type EnchantmentType { get; init; }
    public StackDefinition? Definition { get; set; }
    public LegacyExecutionPolicy? ExecutionPolicy { get; set; }
    public Action<EnchantmentModel, int>? OnMergedDelta { get; set; }
    public Action<EnchantmentModel>? OnMergedRefresh { get; set; }
    public List<KeywordContribution> Keywords { get; } = new();
    public List<DynamicVarContribution> DynamicVarContributions { get; } = new();
    public PresentationTextFormatter? FormatExtraText { get; set; }
    public Func<EnchantmentStackSnapshot, IReadOnlyList<int>?>? GetVisualSliceAmounts { get; set; }
    public Func<EnchantmentScope>? GetScope { get; set; }
    public Action<CardModel, EnchantmentModel>? OnApplied { get; set; }
    public Func<CardModel, EnchantmentModel, RemovalReason, bool>? OnRemoved { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCombatStart { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCombatEnd { get; set; }
    public Action<CardModel, EnchantmentModel>? OnTurnStart { get; set; }
    public Action<CardModel, EnchantmentModel>? OnTurnEnd { get; set; }
    public Action<CardModel, EnchantmentModel>? OnRestored { get; set; }

    // Phase 3a — vanilla card-event hook bridges. Each callback is dispatched only for
    // enchantments that pass MultiEnchantmentScopeSupport.IsActive at the moment the event fires,
    // matching the gating already applied to OnPlay / damage / block pipelines (Phase 1).
    public Action<CardModel, EnchantmentModel>? OnCardPlayed { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardDrawn { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardExhausted { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardDiscarded { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardEnteredCombat { get; set; }

    /// <summary>
    /// Phase 3a T3a.6: bridge to vanilla <c>Hook.AfterDamageReceived</c>. Dispatched to every
    /// active enchantment whose owning card belongs to the target player, with a context bundle
    /// covering target / result / dealer / source. Inactive enchantments are skipped.
    /// </summary>
    public Action<CardModel, EnchantmentModel, DamageReceivedContext>? OnAfterDamageReceived { get; set; }

    // Phase 3b — combat-flow bridges. Each fans out across every active enchantment on every
    // card in both players' PlayerCombatState so authors can react to side-turn boundaries and
    // attack events regardless of which side owns the card.
    public Action<CardModel, EnchantmentModel, CombatSide>? OnSideTurnStart { get; set; }
    public Action<CardModel, EnchantmentModel, CombatSide>? OnBeforeSideTurnStart { get; set; }
    public Action<CardModel, EnchantmentModel, AttackCommand>? OnBeforeAttack { get; set; }
    public Action<CardModel, EnchantmentModel, AttackCommand>? OnAfterAttack { get; set; }

    // Phase 3c — pile / guard / block bridges. OnShouldDie carries a return value (false vetoes
    // death); the rest are void.
    public Action<CardModel, EnchantmentModel, PileType, AbstractModel?>? OnCardChangedPiles { get; set; }
    public Action<CardModel, EnchantmentModel>? OnCardRetained { get; set; }
    public Action<CardModel, EnchantmentModel, BlockGainContext>? OnBeforeBlockGained { get; set; }
    public Action<CardModel, EnchantmentModel, BlockGainContext>? OnBlockGained { get; set; }
    public Func<CardModel, EnchantmentModel, Creature, bool>? OnShouldDie { get; set; }

    // Phase 4 — broadcast card-event hooks. Unlike the per-card OnCardPlayed / OnCardDrawn /
    // OnCardExhausted / OnCardDiscarded (which only fire for the card carrying the enchantment),
    // these fire for EVERY card event in combat. Opt-in: null-check in the adapter means
    // enchantments that don't register these hooks pay zero cost. Parameters are
    // (playedCard, selfCard, selfEnchantment) — playedCard first so the event subject is prominent.
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardPlayed { get; set; }
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardDrawn { get; set; }
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardExhausted { get; set; }
    public Action<CardModel, CardModel, EnchantmentModel>? OnAnyCardDiscarded { get; set; }

    // Phase 5 — sibling lifecycle hooks. Fires when another enchantment is attached to or
    // removed from the same card. Parameters: (selfCard, selfEnchantment, siblingEnchantment).
    // OnSiblingRemoved additionally carries the RemovalReason.
    public Action<CardModel, EnchantmentModel, EnchantmentModel>? OnSiblingApplied { get; set; }
    public Action<CardModel, EnchantmentModel, EnchantmentModel, RemovalReason>? OnSiblingRemoved { get; set; }
}

/// <summary>
/// One <c>TrackKeyword</c> entry per registration call.
/// </summary>
internal sealed record KeywordContribution(
    CardKeyword Keyword,
    Func<EnchantmentStackSnapshot, int> AmountFn);

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
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
}

/// <summary>
/// One <c>TrackKeyword</c> entry per registration call.
/// </summary>
internal sealed record KeywordContribution(
    CardKeyword Keyword,
    Func<EnchantmentStackSnapshot, int> AmountFn);

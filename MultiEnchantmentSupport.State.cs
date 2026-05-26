using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

internal static partial class MultiEnchantmentSupport
{
    private sealed class CardEnchantmentState
    {
        public List<EnchantmentModel> ExtraEnchantments { get; } = new();
        public List<ModelId> ApplicationOrder { get; } = new();
        public Dictionary<EnchantmentModel, ScopeRuntimeState> ScopeStates { get; } = new(ReferenceEqualityComparer.Instance);
        public List<PendingRemovalEntry> PendingRemovals { get; } = new();
        public EnchantmentModel? LastAppliedEnchantment { get; set; }
    }

    private sealed class CardUiState
    {
        public List<Control> ExtraTabs { get; } = new();
        public Dictionary<EnchantmentModel, Action> StatusHandlers { get; } = new(ReferenceEqualityComparer.Instance);
        public int? LastVisualStateFingerprint { get; set; }
        public CardModel? LastSyncCardModel { get; set; }
        public int LastExpectedExtraTabCount { get; set; }
    }

    private sealed class EnchantmentVfxSnapshotState
    {
        public List<EnchantmentVisualState> VisualStates { get; set; } = new();
    }

    internal sealed record EnchantmentVisualState(
        Texture2D Icon,
        int DisplayAmount,
        bool ShowAmount,
        EnchantmentStatus Status);

    private readonly record struct EnchantmentSlotLayout(
        Vector2 Position,
        Vector2 Scale,
        float Rotation,
        Vector2 PivotOffset,
        int ZIndex,
        bool TopLevel);

    private readonly record struct OrderedEnchantmentEntry(
        EnchantmentModel Enchantment,
        int EffectiveAmount);

    private readonly record struct OrderedDynamicVarEnchantmentEntry(
        EnchantmentModel Enchantment,
        EnchantmentStackSnapshot Snapshot);

    private readonly record struct OrderedVisualEntry(
        ModelId EnchantmentId,
        EnchantmentVisualState VisualState);

    private readonly record struct PendingRemovalEntry(
        EnchantmentModel Enchantment,
        RemovalReason Reason);

    private sealed class MultiEnchantmentSaveCarrier
    {
        [SavedProperty]
        public string MultiEnchantmentData { get; set; } = string.Empty;

        [SavedProperty]
        public string MultiEnchantmentOrderData { get; set; } = string.Empty;

        [SavedProperty]
        public int[] MultiEnchantmentMergedStackAmounts { get; set; } = Array.Empty<int>();

        [SavedProperty]
        public string MultiEnchantmentScopeData { get; set; } = string.Empty;
    }
}

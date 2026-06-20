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

        // Turn-scoped mirror of <see cref="LastAppliedEnchantment"/>. Reset at the start of every
        // player turn (see MultiEnchantmentScopeSupport.OnPlayerTurnStarted) so downstream cards can
        // answer "the enchantment I last injected *this turn*". Transient — never persisted to the
        // save sidecar.
        public EnchantmentModel? LastAppliedEnchantmentThisTurn { get; set; }
    }

    private sealed class CardUiState
    {
        public List<Control> ExtraTabs { get; } = new();
        public Dictionary<EnchantmentModel, Action> StatusHandlers { get; } = new(ReferenceEqualityComparer.Instance);
        public int? LastVisualStateFingerprint { get; set; }
        public CardModel? LastSyncCardModel { get; set; }
        public int LastExpectedExtraTabCount { get; set; }
    }

    private sealed class EnchantmentBadgeRestoreState
    {
        public EnchantmentBadgeRestoreState(Control tab)
        {
            SelfModulate = tab.SelfModulate;
            Scale = tab.Scale;
            PivotOffset = tab.PivotOffset;

            if (tab is TextureRect textureRect)
            {
                HasTextureRectTexture = true;
                TextureRectTexture = textureRect.Texture;
                FlipH = textureRect.FlipH;
            }

            if (tab is NinePatchRect ninePatchRect)
            {
                HasNinePatchTexture = true;
                NinePatchTexture = ninePatchRect.Texture;
            }
        }

        public bool HasTextureRectTexture { get; }
        public Texture2D? TextureRectTexture { get; }
        public bool HasNinePatchTexture { get; }
        public Texture2D? NinePatchTexture { get; }
        public Color SelfModulate { get; }
        public Vector2 Scale { get; }
        public Vector2 PivotOffset { get; }
        public bool FlipH { get; }

        public void Restore(Control tab)
        {
            tab.SelfModulate = SelfModulate;
            tab.Scale = Scale;
            tab.PivotOffset = PivotOffset;

            if (HasTextureRectTexture && tab is TextureRect textureRect)
            {
                textureRect.Texture = TextureRectTexture;
                textureRect.FlipH = FlipH;
            }

            if (HasNinePatchTexture && tab is NinePatchRect ninePatchRect)
            {
                ninePatchRect.Texture = NinePatchTexture;
            }
        }
    }

    // Captures the foreground-presentation transform of a badge child (Icon or Label) so an
    // IconScale/IconOffset edit can be reverted before re-applying. Keyed by Control (not just
    // TextureRect) because IconOffset moves the amount Label alongside the Icon.
    private sealed class EnchantmentIconRestoreState
    {
        public EnchantmentIconRestoreState(Control node)
        {
            Position = node.Position;
            Scale = node.Scale;
            PivotOffset = node.PivotOffset;
            SelfModulate = node.SelfModulate;
            UseParentMaterial = node.UseParentMaterial;
        }

        public Vector2 Position { get; }
        public Vector2 Scale { get; }
        public Vector2 PivotOffset { get; }
        public Color SelfModulate { get; }
        public bool UseParentMaterial { get; }

        public void Restore(Control node)
        {
            node.Position = Position;
            node.Scale = Scale;
            node.PivotOffset = PivotOffset;
            node.SelfModulate = SelfModulate;
            node.UseParentMaterial = UseParentMaterial;
        }
    }

    private sealed class EnchantmentVfxSnapshotState
    {
        public List<EnchantmentVisualState> VisualStates { get; set; } = new();
    }

    internal sealed record EnchantmentVisualState(
        Texture2D? Icon,
        int DisplayAmount,
        bool ShowAmount,
        EnchantmentStatus Status,
        EnchantmentPresentationStyle PresentationStyle,
        bool IsDisplayOnly = false,
        Type? MarkerType = null,
        MarkerEnchantmentModel? StoredMarker = null,
        EnchantmentModel? IconSource = null);

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

        // Persists EnchantmentModel.Status (Normal/Disabled). Status is otherwise runtime-only —
        // SerializableEnchantment carries Id/Amount/Props, and EnchantmentModel has no [SavedProperty]
        // of its own — so a card rebuilt from a packet (multiplayer per-combat resync) or a save
        // defaults every enchantment back to Normal. When an enchantment drives a checksummed card
        // keyword off its status (e.g. a TrackKeyword enchantment whose KeywordSourceAmount keys on
        // ActiveInstanceCount), that reset makes the rebuilt peer's card.Keywords diverge from the
        // live owner's, tripping the multiplayer lockstep checksum (NetFullCombatState includes
        // card.Keywords) → StateDivergence. Round-tripping Status keeps both peers identical.
        [SavedProperty]
        public int MultiEnchantmentEnchantmentStatus { get; set; }

        [SavedProperty]
        public string MultiEnchantmentInstanceId { get; set; } = string.Empty;

        [SavedProperty]
        public string MultiEnchantmentCardInstanceId { get; set; } = string.Empty;
    }
}

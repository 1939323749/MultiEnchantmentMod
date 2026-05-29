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
    public static void UpdateAdditionalEnchantmentPreviews(NCard cardNode, CardPreviewMode previewMode)
    {
        CardModel? model = cardNode.Model;
        if (model == null)
        {
            return;
        }

        bool forceUnpoweredPreview = NCardForceUnpoweredPreviewField?.GetValue(cardNode) is bool value && value;
        if (forceUnpoweredPreview)
        {
            return;
        }

        Creature? previewTarget = NCardPreviewTargetField?.GetValue(cardNode) as Creature;
        Creature? target = previewTarget ?? model.CurrentTarget;
        // Snapshot the extra enchantment list: IsActive predicates and DynamicVar updates can
        // invoke user-defined code (ConditionalActive lambdas, [ModifyDynamicVar] methods) that
        // may chain into mod APIs and mutate state.ExtraEnchantments. Defensive snapshot.
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(model).ToList())
        {
            // Always clear the previous preview first so stale numbers don't leak when an
            // enchantment toggles from active → inactive between hover refreshes.
            enchantment.DynamicVars.ClearPreview();
            // Phase 1.5 T1.5.2: skip preview recomputation for inactive enchantments so the
            // numbers a player sees on hover match what will actually happen on play. Without
            // this guard, .WhenActive(false) enchantments still contribute to displayed damage /
            // block totals — inconsistent with the IsActive gating in the actual damage pipeline.
            if (!MultiEnchantmentScopeSupport.IsActive(model, enchantment))
            {
                continue;
            }
            model.UpdateDynamicVarPreview(previewMode, target, enchantment.DynamicVars);
        }
    }

    public static void SyncExtraEnchantmentTabs(NCard cardNode)
    {
        if (!GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        CardModel? model = cardNode.Model;
        if (model == null)
        {
            ClearCardUi(cardNode);
            return;
        }

        IReadOnlyList<EnchantmentModel> extras = GetAdditionalEnchantments(model);
        CardUiState uiState = CardUiStates.GetOrCreateValue(cardNode);
        SubscribeExtraStatusHandlers(cardNode, uiState, extras);

        Control? primaryTab = NCardEnchantmentTabField?.GetValue(cardNode) as Control;
        Vector2 defaultPosition = NCardDefaultEnchantmentPositionField?.GetValue(cardNode) is Vector2 position
            ? position
            : Vector2.Zero;

        if (primaryTab == null || primaryTab.GetParent() == null)
        {
            ClearCardUi(cardNode);
            return;
        }

        List<EnchantmentVisualState> visualStates = MultiEnchantmentStackSupport.ExpandVisualStates(model).ToList();
        if (visualStates.Count == 0)
        {
            ClearCardUi(cardNode);
            return;
        }

        Node badgeRoot = primaryTab.GetParent();
        int expectedExtraTabCount = Math.Max(0, visualStates.Count - 1);
        int fingerprint = ComputeVisualStateFingerprint(model, visualStates, primaryTab, defaultPosition);
        if (uiState.LastSyncCardModel == model &&
            uiState.LastVisualStateFingerprint == fingerprint &&
            AreExtraTabsStillSynced(badgeRoot, primaryTab, uiState, expectedExtraTabCount, expectedVisible: true))
        {
            ApplyExistingEnchantmentTabs(cardNode, uiState, primaryTab, badgeRoot, model, visualStates, defaultPosition);
            return;
        }

        RemoveOrphanedExtraEnchantmentTabs(badgeRoot, uiState.ExtraTabs);

        // Base-game source: NCard.UpdateEnchantmentVisuals.
        // Reconstruct the primary tab layout exactly like vanilla, then reuse the resulting slot
        // geometry everywhere else so centered/queued cards and enchant VFX all agree on which row
        // each enchantment occupies.
        List<EnchantmentSlotLayout> slotLayouts = BuildEnchantmentSlotLayouts(
            cardNode,
            primaryTab,
            visualStates.Count,
            defaultPosition);

        ApplyEnchantmentSlotLayout(primaryTab, slotLayouts[0], visible: true);
        ApplyEnchantmentVisualState(primaryTab, visualStates[0]);

        while (uiState.ExtraTabs.Count > expectedExtraTabCount)
        {
            int lastIndex = uiState.ExtraTabs.Count - 1;
            Control tab = uiState.ExtraTabs[lastIndex];
            uiState.ExtraTabs.RemoveAt(lastIndex);
            if (GodotObject.IsInstanceValid(tab))
            {
                tab.QueueFreeSafely();
            }
        }

        while (uiState.ExtraTabs.Count < expectedExtraTabCount)
        {
            Control tab = (Control)primaryTab.Duplicate();
            tab.Name = $"{ExtraEnchantmentTabPrefix}{uiState.ExtraTabs.Count + 1}";
            tab.UniqueNameInOwner = false;
            if (tab.Material != null)
            {
                tab.Material = (Material)tab.Material.Duplicate();
            }

            badgeRoot.AddChildSafely(tab);
            uiState.ExtraTabs.Add(tab);
        }

        for (int i = 0; i < uiState.ExtraTabs.Count; i++)
        {
            Control tab = uiState.ExtraTabs[i];
            if (i >= expectedExtraTabCount)
            {
                tab.Visible = false;
                continue;
            }

            EnchantmentVisualState visualState = visualStates[i + 1];
            ApplyEnchantmentSlotLayout(tab, slotLayouts[i + 1], visible: true);
            ApplyEnchantmentVisualState(tab, visualState);
        }

        EnsureExtraTabSiblingOrder(badgeRoot, primaryTab, uiState.ExtraTabs);
        UpdateCardUiCache(uiState, model, visualStates, primaryTab, defaultPosition, expectedExtraTabCount);
    }

    public static void ClearCardUi(NCard cardNode)
    {
        ClearTransientEnchantVfxUi(cardNode);

        Control? primaryTab = NCardEnchantmentTabField?.GetValue(cardNode) as Control;
        Node? badgeRoot = primaryTab?.GetParent();
        if (badgeRoot != null)
        {
            ClearNamedChildren(badgeRoot, ExtraEnchantmentTabPrefix);
        }

        if (!CardUiStates.TryGetValue(cardNode, out CardUiState? state))
        {
            return;
        }

        foreach ((EnchantmentModel enchantment, Action handler) in state.StatusHandlers.ToArray())
        {
            enchantment.StatusChanged -= handler;
        }

        foreach (Control tab in state.ExtraTabs)
        {
            if (GodotObject.IsInstanceValid(tab))
            {
                tab.QueueFreeSafely();
            }
        }

        state.ExtraTabs.Clear();
        state.StatusHandlers.Clear();
        state.LastVisualStateFingerprint = null;
        state.LastSyncCardModel = null;
        state.LastExpectedExtraTabCount = 0;
        CardUiStates.Remove(cardNode);
    }

    public static void HideExtraEnchantmentTabs(NCard? cardNode)
    {
        if (cardNode == null || !GodotObject.IsInstanceValid(cardNode))
        {
            return;
        }

        Control? primaryTab = NCardEnchantmentTabField?.GetValue(cardNode) as Control;
        Node? badgeRoot = primaryTab?.GetParent();
        if (badgeRoot != null)
        {
            foreach (Node child in badgeRoot.GetChildren())
            {
                if (child is Control tab &&
                    tab.Name.ToString().StartsWith(ExtraEnchantmentTabPrefix, StringComparison.Ordinal))
                {
                    tab.Visible = false;
                }
            }
        }

        if (!CardUiStates.TryGetValue(cardNode, out CardUiState? state))
        {
            return;
        }

        foreach (Control tab in state.ExtraTabs.Where(GodotObject.IsInstanceValid))
        {
            tab.Visible = false;
        }
    }

    public static void RefreshExtraEnchantmentTabs(NCard? cardNode)
    {
        if (cardNode == null || !GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        InvalidateCardUiCache(cardNode);
        SyncExtraEnchantmentTabs(cardNode);
    }

    public static void RefreshExtraTabTransformOnly(NCard? cardNode)
    {
        RefreshExtraTabsPreferInPlace(cardNode);
    }

    public static void RefreshExtraTabsPreferInPlace(NCard? cardNode)
    {
        if (cardNode == null || !GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        if (TryReapplyExtraTabVisualsInPlace(cardNode))
        {
            return;
        }

        CardModel? model = cardNode.Model;
        bool hasTrackedTabs = CardUiStates.TryGetValue(cardNode, out CardUiState? state) && state.ExtraTabs.Count > 0;
        if (NeedsExtraEnchantmentTabs(model) || hasTrackedTabs)
        {
            RefreshExtraEnchantmentTabs(cardNode);
        }
    }

    public static void RefreshExtraTabsInPlaceOnly(NCard? cardNode)
    {
        if (cardNode == null || !GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        TryReapplyExtraTabVisualsInPlace(cardNode);
    }

    private static bool TryReapplyExtraTabVisualsInPlace(NCard cardNode)
    {
        if (!CardUiStates.TryGetValue(cardNode, out CardUiState? state))
        {
            return false;
        }

        Control? primaryTab = NCardEnchantmentTabField?.GetValue(cardNode) as Control;
        Node? badgeRoot = primaryTab?.GetParent();
        if (primaryTab == null || badgeRoot == null)
        {
            return false;
        }

        return ReapplyExtraTabVisualsInPlace(cardNode, state, primaryTab, badgeRoot);
    }

    private static bool ReapplyExtraTabVisualsInPlace(
        NCard cardNode,
        CardUiState state,
        Control primaryTab,
        Node badgeRoot)
    {
        CardModel? model = cardNode.Model;
        if (model == null)
        {
            return false;
        }

        Vector2 defaultPosition = NCardDefaultEnchantmentPositionField?.GetValue(cardNode) is Vector2 position
            ? position
            : Vector2.Zero;
        List<EnchantmentVisualState> visualStates = MultiEnchantmentStackSupport.ExpandVisualStates(model).ToList();
        int expectedExtraTabCount = Math.Max(0, visualStates.Count - 1);
        if (visualStates.Count == 0 ||
            state.ExtraTabs.Count != expectedExtraTabCount ||
            state.ExtraTabs.Any(tab => !GodotObject.IsInstanceValid(tab) || tab.GetParent() != badgeRoot))
        {
            return false;
        }

        SubscribeExtraStatusHandlers(cardNode, state, GetAdditionalEnchantments(model));
        ApplyExistingEnchantmentTabs(cardNode, state, primaryTab, badgeRoot, model, visualStates, defaultPosition);
        return true;
    }

    private static void InvalidateCardUiCache(NCard cardNode)
    {
        if (!CardUiStates.TryGetValue(cardNode, out CardUiState? state))
        {
            return;
        }

        state.LastVisualStateFingerprint = null;
        state.LastSyncCardModel = null;
    }

    public static void CaptureEnchantVfxSnapshot(Node? vfxNode, CardModel? card)
    {
        if (vfxNode == null || card == null)
        {
            return;
        }

        EnchantmentVfxSnapshotState state = PendingEnchantVfxSnapshots.GetOrCreateValue(vfxNode);
        state.VisualStates = BuildEnchantVfxVisualStates(card);
    }

    public static IEnumerable<AbstractModel> AppendRunStateExtraEnchantments(RunState runState, IEnumerable<AbstractModel> original)
    {
        foreach (AbstractModel model in original)
        {
            yield return model;
        }

        foreach (Player player in runState.Players.Where(static player => player.IsActiveForHooks))
        {
            foreach (CardModel card in player.Deck.Cards.Where(static card => !card.HasBeenRemovedFromState))
            {
                // Snapshot the extra enchantment list: a downstream virtual (e.g.
                // AfterCardChangedPiles) may call RemoveEnchantment, which mutates the
                // live ExtraEnchantments list and would otherwise crash the enumerator.
                foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card).ToList())
                {
                    // Honor WhenActive / ConditionalActive on the listener path. Without this,
                    // an enchantment whose IsActive predicate is false still fires its
                    // AbstractModel-virtual hooks (AfterCardPlayed, ModifyDamageAdditive,
                    // AfterDamageReceived, …) because Hook.* iterates the listener list directly
                    // and skips the per-call IsActive gate that the value-modifier pipelines
                    // (ApplyDamageEnchantments etc.) apply.
                    if (!MultiEnchantmentScopeSupport.IsActive(card, enchantment))
                    {
                        continue;
                    }

                    yield return enchantment;
                }
            }
        }
    }

    public static IEnumerable<AbstractModel> AppendCombatStateExtraEnchantments(CombatState combatState, IEnumerable<AbstractModel> original)
    {
        foreach (AbstractModel model in original)
        {
            yield return model;
        }

        foreach (Player player in combatState.Players.Where(static player => player.IsActiveForHooks && player.PlayerCombatState != null))
        {
            foreach (CardModel card in player.PlayerCombatState!.AllCards.Where(static card => !card.HasBeenRemovedFromState))
            {
                // Snapshot the extra enchantment list: a downstream virtual (e.g.
                // AfterCardChangedPiles) may call RemoveEnchantment, which mutates the
                // live ExtraEnchantments list and would otherwise crash the enumerator.
                foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card).ToList())
                {
                    // See AppendRunStateExtraEnchantments for why IsActive gates the listener
                    // path as well as the value-modifier pipelines.
                    if (!MultiEnchantmentScopeSupport.IsActive(card, enchantment))
                    {
                        continue;
                    }

                    yield return enchantment;
                }
            }
        }
    }

    private static List<EnchantmentVisualState> ConsumeEnchantVfxSnapshot(Node vfxNode, CardModel card)
    {
        if (PendingEnchantVfxSnapshots.TryGetValue(vfxNode, out EnchantmentVfxSnapshotState? state) &&
            state.VisualStates.Count > 0)
        {
            List<EnchantmentVisualState> snapshot = state.VisualStates;
            PendingEnchantVfxSnapshots.Remove(vfxNode);
            return snapshot;
        }

        return BuildEnchantVfxVisualStates(card);
    }

    private static List<EnchantmentVisualState> BuildEnchantVfxVisualStates(CardModel card)
    {
        return GetOrderedVisualStates(card).ToList();
    }

    private static void ApplyEnchantmentVisualState(Control tab, EnchantmentVisualState visualState)
    {
        TextureRect? icon = tab.GetNodeOrNull<TextureRect>("Icon");
        MegaLabel? label = tab.GetNodeOrNull<MegaLabel>("Label");
        if (icon != null)
        {
            icon.Texture = visualState.Icon;
        }

        if (label != null)
        {
            label.SetTextAutoSize(visualState.DisplayAmount.ToString());
            label.Visible = visualState.ShowAmount;
        }

        ApplyStatusToTab(tab, icon, label, visualState.Status);
    }

    private static List<EnchantmentSlotLayout> BuildEnchantmentSlotLayouts(
        NCard cardNode,
        Control primaryTab,
        int slotCount,
        Vector2 defaultPosition)
    {
        List<EnchantmentSlotLayout> layouts = new(slotCount);
        if (slotCount <= 0)
        {
            return layouts;
        }

        CardModel? model = cardNode.Model;
        Vector2 expectedPrimaryPosition = model != null && (model.HasStarCostX || model.CurrentStarCost >= 0)
            ? defaultPosition
            : defaultPosition + Vector2.Up * 45f;
        Vector2 primaryPosition = primaryTab.Position == Vector2.Zero && expectedPrimaryPosition != Vector2.Zero
            ? expectedPrimaryPosition
            : primaryTab.Position;
        float rowOffset = GetExtraEnchantmentRowOffset(primaryTab);

        for (int i = 0; i < slotCount; i++)
        {
            layouts.Add(new EnchantmentSlotLayout(
                primaryPosition + Vector2.Down * (i * rowOffset),
                primaryTab.Scale,
                primaryTab.Rotation,
                primaryTab.PivotOffset,
                primaryTab.ZIndex,
                primaryTab.TopLevel));
        }

        return layouts;
    }

    private static void ApplyEnchantmentSlotLayout(Control tab, EnchantmentSlotLayout layout, bool visible)
    {
        tab.Visible = visible;
        tab.Position = layout.Position;
        tab.Scale = layout.Scale;
        tab.Rotation = layout.Rotation;
        tab.PivotOffset = layout.PivotOffset;
        tab.ZIndex = layout.ZIndex;
        tab.TopLevel = layout.TopLevel;
    }

    private static void ApplyExistingEnchantmentTabs(
        NCard cardNode,
        CardUiState state,
        Control primaryTab,
        Node badgeRoot,
        CardModel model,
        IReadOnlyList<EnchantmentVisualState> visualStates,
        Vector2 defaultPosition)
    {
        if (visualStates.Count == 0)
        {
            return;
        }

        int expectedExtraTabCount = Math.Max(0, visualStates.Count - 1);
        List<EnchantmentSlotLayout> slotLayouts = BuildEnchantmentSlotLayouts(
            cardNode,
            primaryTab,
            visualStates.Count,
            defaultPosition);

        ApplyEnchantmentSlotLayout(primaryTab, slotLayouts[0], visible: true);
        ApplyEnchantmentVisualState(primaryTab, visualStates[0]);
        for (int i = 0; i < state.ExtraTabs.Count; i++)
        {
            Control tab = state.ExtraTabs[i];
            bool shouldShow = i < expectedExtraTabCount;
            if (!shouldShow)
            {
                tab.Visible = false;
                continue;
            }

            ApplyEnchantmentSlotLayout(tab, slotLayouts[i + 1], visible: true);
            ApplyEnchantmentVisualState(tab, visualStates[i + 1]);
        }

        EnsureExtraTabSiblingOrder(badgeRoot, primaryTab, state.ExtraTabs);
        UpdateCardUiCache(state, model, visualStates, primaryTab, defaultPosition, expectedExtraTabCount);
    }

    private static void EnsureExtraTabSiblingOrder(Node badgeRoot, Control primaryTab, IReadOnlyList<Control> extraTabs)
    {
        if (!GodotObject.IsInstanceValid(primaryTab) || primaryTab.GetParent() != badgeRoot)
        {
            return;
        }

        List<Control> validTabs = extraTabs
            .Where(tab => GodotObject.IsInstanceValid(tab) && tab.GetParent() == badgeRoot)
            .ToList();

        for (int i = 0; i < validTabs.Count; i++)
        {
            Control tab = validTabs[i];
            int targetIndex = Math.Min(primaryTab.GetIndex() + 1 + i, Math.Max(0, badgeRoot.GetChildCount() - 1));
            if (tab.GetIndex() != targetIndex)
            {
                badgeRoot.MoveChild(tab, targetIndex);
            }
        }
    }

    private static void UpdateCardUiCache(
        CardUiState state,
        CardModel model,
        IReadOnlyList<EnchantmentVisualState> visualStates,
        Control primaryTab,
        Vector2 defaultPosition,
        int expectedExtraTabCount)
    {
        state.LastSyncCardModel = model;
        state.LastVisualStateFingerprint = ComputeVisualStateFingerprint(model, visualStates, primaryTab, defaultPosition);
        state.LastExpectedExtraTabCount = expectedExtraTabCount;
    }

    private static void SubscribeExtraStatusHandlers(NCard cardNode, CardUiState uiState, IReadOnlyList<EnchantmentModel> extras)
    {
        foreach ((EnchantmentModel enchantment, Action handler) in uiState.StatusHandlers.ToArray())
        {
            if (extras.Any(extra => ReferenceEquals(extra, enchantment)))
            {
                continue;
            }

            enchantment.StatusChanged -= handler;
            uiState.StatusHandlers.Remove(enchantment);
        }

        foreach (EnchantmentModel enchantment in extras)
        {
            if (uiState.StatusHandlers.ContainsKey(enchantment))
            {
                continue;
            }

            void Handler()
            {
                if (GodotObject.IsInstanceValid(cardNode))
                {
                    SyncExtraEnchantmentTabs(cardNode);
                }
            }

            enchantment.StatusChanged += Handler;
            uiState.StatusHandlers[enchantment] = Handler;
        }
    }

    private static void ApplyStatusToTab(Control tab, TextureRect? icon, MegaLabel? label, EnchantmentStatus status)
    {
        if (status == EnchantmentStatus.Disabled)
        {
            tab.Modulate = new Color(1f, 1f, 1f, 0.9f);
            if (tab.Material is ShaderMaterial shader)
            {
                shader.SetShaderParameter(ShaderH, 0.25);
                shader.SetShaderParameter(ShaderS, 0.1);
                shader.SetShaderParameter(ShaderV, 0.6);
            }

            if (icon != null)
            {
                icon.UseParentMaterial = true;
            }

            if (label != null)
            {
                label.SelfModulate = StsColors.gray;
            }
        }
        else
        {
            tab.Modulate = Colors.White;
            if (tab.Material is ShaderMaterial shader)
            {
                shader.SetShaderParameter(ShaderH, 0.25);
                shader.SetShaderParameter(ShaderS, 0.4);
                shader.SetShaderParameter(ShaderV, 0.6);
            }

            if (icon != null)
            {
                icon.UseParentMaterial = false;
            }

            if (label != null)
            {
                label.SelfModulate = Colors.White;
            }
        }
    }

    private static bool AreExtraTabsStillSynced(
        Node badgeRoot,
        Control primaryTab,
        CardUiState state,
        int expectedExtraTabCount,
        bool expectedVisible)
    {
        if (state.ExtraTabs.Count != expectedExtraTabCount ||
            state.LastExpectedExtraTabCount != expectedExtraTabCount)
        {
            return false;
        }

        foreach (Control tab in state.ExtraTabs)
        {
            if (!GodotObject.IsInstanceValid(tab) ||
                tab.GetParent() != badgeRoot ||
                tab.Visible != expectedVisible)
            {
                return false;
            }
        }

        return true;
    }

    private static bool NeedsExtraEnchantmentTabs(CardModel? model)
    {
        if (model == null)
        {
            return false;
        }

        if (GetAdditionalEnchantments(model).Count > 0)
        {
            return true;
        }

        EnchantmentModel? primary = model.Enchantment;
        return primary != null && MultiEnchantmentStackSupport.GetVisualStackCount(primary) > 1;
    }

    private static int ComputeVisualStateFingerprint(
        CardModel model,
        IReadOnlyList<EnchantmentVisualState> visualStates,
        Control primaryTab,
        Vector2 defaultPosition)
    {
        HashCode hash = new();
        hash.Add(model.HasStarCostX);
        hash.Add(model.CurrentStarCost >= 0);
        hash.Add(defaultPosition);
        hash.Add(primaryTab.Position);
        hash.Add(primaryTab.Size);
        hash.Add(primaryTab.Scale);
        hash.Add(primaryTab.Rotation);
        hash.Add(primaryTab.PivotOffset);
        hash.Add(primaryTab.ZIndex);
        hash.Add(primaryTab.TopLevel);
        hash.Add(visualStates.Count);
        foreach (EnchantmentVisualState visualState in visualStates)
        {
            AddFingerprint(ref hash, visualState);
        }

        return hash.ToHashCode();
    }

    private static void AddFingerprint(ref HashCode hash, EnchantmentVisualState visualState)
    {
        hash.Add(visualState.Icon);
        hash.Add(visualState.Icon?.ResourcePath);
        hash.Add(visualState.DisplayAmount);
        hash.Add(visualState.ShowAmount);
        hash.Add((int)visualState.Status);
    }

    private static void ClearNamedChildren(Node parent, string prefix)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child.Name.ToString().StartsWith(prefix, StringComparison.Ordinal))
            {
                child.QueueFreeSafely();
            }
        }
    }

    private static void RemoveOrphanedExtraEnchantmentTabs(Node parent, IReadOnlyCollection<Control> trackedTabs)
    {
        HashSet<Control> trackedTabSet = trackedTabs.Where(GodotObject.IsInstanceValid).ToHashSet();
        foreach (Node child in parent.GetChildren())
        {
            if (child is Control tab &&
                tab.Name.ToString().StartsWith(ExtraEnchantmentTabPrefix, StringComparison.Ordinal) &&
                !trackedTabSet.Contains(tab))
            {
                tab.QueueFreeSafely();
            }
        }
    }

    private static void ClearTransientEnchantVfxUi(NCard cardNode)
    {
        if (!GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        Control? enchantmentTab = NCardEnchantmentTabField?.GetValue(cardNode) as Control;
        TextureRect? vfxOverride = cardNode.GetNodeOrNull<TextureRect>("%EnchantmentVfxOverride");

        Node? badgeRoot = enchantmentTab?.GetParent();
        if (badgeRoot != null)
        {
            ClearNamedChildren(badgeRoot, EnchantVfxStaticBadgePrefix);
        }

        if (vfxOverride != null)
        {
            RestoreEnchantVfxOverrideDefaults(vfxOverride);
        }
    }

    private static void CaptureEnchantVfxOverrideRestoreState(TextureRect vfxOverride)
    {
        if (vfxOverride.HasMeta(EnchantVfxOverrideRestoreActiveMeta))
        {
            return;
        }

        vfxOverride.SetMeta(EnchantVfxOverrideRestorePositionMeta, vfxOverride.Position);
        vfxOverride.SetMeta(EnchantVfxOverrideRestoreSizeMeta, vfxOverride.Size);
        vfxOverride.SetMeta(EnchantVfxOverrideRestoreActiveMeta, true);
    }

    private static void RestoreEnchantVfxOverrideDefaults(TextureRect vfxOverride)
    {
        if (!vfxOverride.HasMeta(EnchantVfxOverrideRestoreActiveMeta))
        {
            return;
        }

        if (vfxOverride.HasMeta(EnchantVfxOverrideRestorePositionMeta))
        {
            vfxOverride.Position = vfxOverride.GetMeta(EnchantVfxOverrideRestorePositionMeta).AsVector2();
        }

        if (vfxOverride.HasMeta(EnchantVfxOverrideRestoreSizeMeta))
        {
            vfxOverride.Size = vfxOverride.GetMeta(EnchantVfxOverrideRestoreSizeMeta).AsVector2();
        }

        vfxOverride.RemoveMeta(EnchantVfxOverrideRestorePositionMeta);
        vfxOverride.RemoveMeta(EnchantVfxOverrideRestoreSizeMeta);
        vfxOverride.RemoveMeta(EnchantVfxOverrideRestoreActiveMeta);
    }

    private static void SyncEnchantVfxSparkles(Node vfxNode, Vector2 baseSlotPosition, Vector2 animatedSlotPosition)
    {
        GpuParticles2D? sparkles = vfxNode.GetNodeOrNull<GpuParticles2D>("%EnchantmentAppearSparkles");
        if (sparkles == null)
        {
            return;
        }

        Vector2 basePosition = sparkles.HasMeta(EnchantVfxSparklesBasePositionMeta)
            ? sparkles.GetMeta(EnchantVfxSparklesBasePositionMeta).AsVector2()
            : sparkles.Position;
        if (!sparkles.HasMeta(EnchantVfxSparklesBasePositionMeta))
        {
            sparkles.SetMeta(EnchantVfxSparklesBasePositionMeta, basePosition);
        }

        sparkles.Position = basePosition + (animatedSlotPosition - baseSlotPosition);
    }

    private static float GetExtraEnchantmentRowOffset(Control primaryTab)
    {
        return Math.Max(ExtraSlotYOffset, primaryTab.Size.Y * primaryTab.Scale.Y);
    }

    private static void ResizeEnchantVfxViewport(
        Node vfxNode,
        NCard cardNode,
        TextureRect templateBadge,
        EnchantmentSlotLayout slotLayout)
    {
        SubViewport? viewport = vfxNode.GetNodeOrNull<SubViewport>("%EnchantmentViewport");
        if (viewport == null)
        {
            return;
        }

        int targetWidth = Mathf.CeilToInt(templateBadge.Size.X * templateBadge.Scale.X);
        int targetHeight = Mathf.CeilToInt(templateBadge.Size.Y * templateBadge.Scale.Y);

        viewport.Size = new Vector2I(targetWidth, targetHeight);
        TextureRect vfxOverride = cardNode.EnchantmentVfxOverride;
        CaptureEnchantVfxOverrideRestoreState(vfxOverride);
        // Base-game source: NCard.OnReturnedFromPool does not restore EnchantmentVfxOverride's
        // rect, and NCard is pooled. Always assign an absolute position from the current card tab
        // instead of accumulating offsets on reused card nodes.
        vfxOverride.Position = slotLayout.Position;
        vfxOverride.Size = new Vector2(targetWidth, targetHeight);
    }
}

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
using MegaCrit.Sts2.Core.Models.Relics;
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
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(model)
                     .Where(IsGameplayEnchantment)
                     .ToList())
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
            visualStates,
            defaultPosition);

        ApplyEnchantmentSlotLayout(primaryTab, slotLayouts[0], visible: true);

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
            Control tab = DuplicateEnchantmentTab(primaryTab);
            tab.Name = $"{ExtraEnchantmentTabPrefix}{uiState.ExtraTabs.Count + 1}";
            badgeRoot.AddChildSafely(tab);
            uiState.ExtraTabs.Add(tab);
        }

        ApplyEnchantmentVisualState(primaryTab, visualStates[0]);

        for (int i = 0; i < uiState.ExtraTabs.Count; i++)
        {
            Control tab = uiState.ExtraTabs[i];
            if (i >= expectedExtraTabCount)
            {
                RestoreEnchantmentBadgePresentation(tab);
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
        if (primaryTab != null)
        {
            RestoreEnchantmentBadgePresentation(primaryTab);
        }

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

        // Snapshot: QueueFreeSafely can re-enter our NCard patches via tree-exit notifications,
        // which mutate ExtraTabs mid-enumeration (seen in the wild as InvalidOperationException).
        foreach (Control tab in state.ExtraTabs.ToArray())
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
        if (primaryTab != null)
        {
            RestoreEnchantmentBadgePresentation(primaryTab);
        }

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
        if (NeedsExtraEnchantmentTabs(model) || NeedsPresentationRefresh(model) || hasTrackedTabs)
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
        foreach (AbstractModel model in original.ToList())
        {
            if (ShouldYieldHookListener(model, "RunState", expectedCombatState: null))
            {
                yield return model;
            }
        }

        foreach (Player player in runState.Players.Where(static player => player.IsActiveForHooks).ToList())
        {
            foreach (CardModel card in player.Deck.Cards.Where(static card => !card.HasBeenRemovedFromState).ToList())
            {
                // Snapshot the extra enchantment list: a downstream virtual (e.g.
                // AfterCardChangedPiles) may call RemoveEnchantment, which mutates the
                // live ExtraEnchantments list and would otherwise crash the enumerator.
                foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card)
                             .Where(IsGameplayEnchantment)
                             .ToList())
                {
                    // Honor WhenActive / ConditionalActive on the listener path. Without this,
                    // an enchantment whose IsActive predicate is false still fires its
                    // AbstractModel-virtual hooks (AfterCardPlayed, ModifyDamageAdditive,
                    // AfterDamageReceived, …) because Hook.* iterates the listener list directly
                    // and skips the per-call IsActive gate that the value-modifier pipelines
                    // (ApplyDamageEnchantments etc.) apply.
                    if (!ShouldAppendListenerEnchantment(card, enchantment, "RunState", expectedCombatState: null))
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
        foreach (AbstractModel model in original.ToList())
        {
            if (ShouldYieldHookListener(model, "CombatState", combatState))
            {
                yield return model;
            }
        }

        foreach (Player player in combatState.Players.Where(static player => player.IsActiveForHooks && player.PlayerCombatState != null).ToList())
        {
            foreach (CardModel card in player.PlayerCombatState!.AllCards.Where(static card => !card.HasBeenRemovedFromState).ToList())
            {
                // Snapshot the extra enchantment list: a downstream virtual (e.g.
                // AfterCardChangedPiles) may call RemoveEnchantment, which mutates the
                // live ExtraEnchantments list and would otherwise crash the enumerator.
                foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card)
                             .Where(IsGameplayEnchantment)
                             .ToList())
                {
                    // See AppendRunStateExtraEnchantments for why IsActive gates the listener
                    // path as well as the value-modifier pipelines.
                    if (!ShouldAppendListenerEnchantment(card, enchantment, "CombatState", combatState))
                    {
                        continue;
                    }

                    yield return enchantment;
                }
            }
        }
    }

    private static bool ShouldAppendListenerEnchantment(
        CardModel card,
        EnchantmentModel enchantment,
        string listenerSource,
        CombatState? expectedCombatState)
    {
        CardModel? ownerCard = enchantment.Card;
        if (ownerCard == null)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Skipping dangling extra enchantment on {listenerSource} hook listeners. " +
                $"ExpectedCard={DescribeCard(card)} Enchantment={SafeModelId(enchantment)} Type={enchantment.GetType().FullName}");
            return false;
        }

        if (!ReferenceEquals(ownerCard, card))
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Skipping mismatched extra enchantment on {listenerSource} hook listeners. " +
                $"ExpectedCard={DescribeCard(card)} ActualCard={DescribeCard(ownerCard)} Enchantment={SafeModelId(enchantment)} " +
                $"Type={enchantment.GetType().FullName}");
            return false;
        }

        if (ownerCard.HasBeenRemovedFromState)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Skipping removed-card extra enchantment on {listenerSource} hook listeners. " +
                $"Card={DescribeCard(card)} Enchantment={SafeModelId(enchantment)} Type={enchantment.GetType().FullName}");
            return false;
        }

        Player? owner = ownerCard.Owner;
        if (owner == null)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Skipping ownerless-card extra enchantment on {listenerSource} hook listeners. " +
                $"Card={DescribeCard(card)} Enchantment={SafeModelId(enchantment)} Type={enchantment.GetType().FullName}");
            return false;
        }

        if (!owner.IsActiveForHooks)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Skipping inactive-owner extra enchantment on {listenerSource} hook listeners. " +
                $"Card={DescribeCard(card)} Owner={SafePlayerId(owner)} Enchantment={SafeModelId(enchantment)} Type={enchantment.GetType().FullName}");
            return false;
        }

        if (expectedCombatState != null && !ReferenceEquals(ownerCard.CombatState, expectedCombatState))
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Skipping off-combat extra enchantment on {listenerSource} hook listeners. " +
                $"Card={DescribeCard(card)} Pile={SafePileType(ownerCard)} Enchantment={SafeModelId(enchantment)} " +
                $"Type={enchantment.GetType().FullName}");
            return false;
        }

        return ShouldYieldHookListener(enchantment, listenerSource, expectedCombatState) &&
               MultiEnchantmentScopeSupport.IsActive(card, enchantment);
    }

    private static bool ShouldYieldHookListener(
        AbstractModel? model,
        string listenerSource,
        CombatState? expectedCombatState)
    {
        if (model == null)
        {
            LogSkippedInvalidHookListener(null, listenerSource, "listener is null");
            return false;
        }

        try
        {
            if (TryGetInvalidHookListenerReason(model, expectedCombatState, out string? reason))
            {
                LogSkippedInvalidHookListener(model, listenerSource, reason ?? "unknown invalid hook listener");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogSkippedInvalidHookListener(
                model,
                listenerSource,
                $"validation threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetInvalidHookListenerReason(
        AbstractModel model,
        CombatState? expectedCombatState,
        out string? reason)
    {
        reason = null;
        switch (model)
        {
            case CardModel card:
                return TryGetInvalidCardHookListenerReason(card, expectedCombatState, out reason);
            case EnchantmentModel enchantment:
                return TryGetInvalidCardOwnedHookListenerReason(
                    enchantment.Card,
                    expectedCombatState,
                    "enchantment has no card",
                    "enchantment card",
                    out reason);
            case AfflictionModel affliction:
                return TryGetInvalidCardOwnedHookListenerReason(
                    affliction.Card,
                    expectedCombatState,
                    "affliction has no card",
                    "affliction card",
                    out reason);
            case PowerModel power:
                Creature? powerOwner = power.Owner;
                if (powerOwner == null)
                {
                    reason = "power has no owner";
                    return true;
                }

                if (powerOwner.CombatState == null)
                {
                    reason = "power owner has no combat state";
                    return true;
                }

                if (expectedCombatState != null && !ReferenceEquals(powerOwner.CombatState, expectedCombatState))
                {
                    reason = "power owner belongs to a different combat state";
                    return true;
                }

                if (powerOwner.Player is { } powerPlayer && !powerPlayer.IsActiveForHooks)
                {
                    reason = "power owner player is inactive for hooks";
                    return true;
                }

                return false;
            case RelicModel relic:
                return TryGetInvalidPlayerOwnedHookListenerReason(
                    relic.Owner,
                    relic.HasBeenRemovedFromState,
                    "relic",
                    out reason);
            case PotionModel potion:
                return TryGetInvalidPlayerOwnedHookListenerReason(
                    potion.Owner,
                    potion.HasBeenRemovedFromState,
                    "potion",
                    out reason);
            case OrbModel orb:
                return TryGetInvalidPlayerOwnedHookListenerReason(
                    orb.Owner,
                    orb.HasBeenRemovedFromState,
                    "orb",
                    out reason);
            case MonsterModel monster:
                Creature? monsterCreature = monster.Creature;
                if (monsterCreature == null)
                {
                    reason = "monster has no creature";
                    return true;
                }

                if (monsterCreature.CombatState == null)
                {
                    reason = "monster creature has no combat state";
                    return true;
                }

                if (expectedCombatState != null && !ReferenceEquals(monsterCreature.CombatState, expectedCombatState))
                {
                    reason = "monster belongs to a different combat state";
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static bool TryGetInvalidCardOwnedHookListenerReason(
        CardModel? card,
        CombatState? expectedCombatState,
        string missingCardReason,
        string cardLabel,
        out string? reason)
    {
        if (card == null)
        {
            reason = missingCardReason;
            return true;
        }

        return TryGetInvalidCardHookListenerReason(card, expectedCombatState, out reason, cardLabel);
    }

    private static bool TryGetInvalidCardHookListenerReason(
        CardModel card,
        CombatState? expectedCombatState,
        out string? reason,
        string cardLabel = "card")
    {
        if (card.HasBeenRemovedFromState)
        {
            reason = $"{cardLabel} has been removed from state";
            return true;
        }

        Player? owner = card.Owner;
        if (owner == null)
        {
            reason = $"{cardLabel} has no owner";
            return true;
        }

        if (!owner.IsActiveForHooks)
        {
            reason = $"{cardLabel} owner is inactive for hooks";
            return true;
        }

        if (expectedCombatState != null && !ReferenceEquals(card.CombatState, expectedCombatState))
        {
            reason = $"{cardLabel} belongs to a different combat state or no combat pile";
            return true;
        }

        reason = null;
        return false;
    }

    private static bool TryGetInvalidPlayerOwnedHookListenerReason(
        Player? owner,
        bool hasBeenRemovedFromState,
        string modelKind,
        out string? reason)
    {
        if (hasBeenRemovedFromState)
        {
            reason = $"{modelKind} has been removed from state";
            return true;
        }

        if (owner == null)
        {
            reason = $"{modelKind} has no owner";
            return true;
        }

        if (!owner.IsActiveForHooks)
        {
            reason = $"{modelKind} owner is inactive for hooks";
            return true;
        }

        reason = null;
        return false;
    }

    private static void LogSkippedInvalidHookListener(
        AbstractModel? model,
        string listenerSource,
        string reason)
    {
        string description = DescribeHookListener(model);
        string key = $"{listenerSource}|{RuntimeHelpers.GetHashCode(model)}|{reason}|{description}";
        lock (InvalidHookListenerLogKeys)
        {
            if (!InvalidHookListenerLogKeys.Add(key))
            {
                return;
            }
        }

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment][HookListenerGuard] Skipping invalid {listenerSource} hook listener. " +
            $"Reason={reason}; {description}");
    }

    private static string DescribeHookListener(AbstractModel? model)
    {
        if (model == null)
        {
            return "Listener=<null>";
        }

        string typeName = model.GetType().FullName ?? model.GetType().Name;
        string id = SafeModelId(model);
        return model switch
        {
            CardModel card => $"ListenerType={typeName} Id={id} Card={DescribeCard(card)}",
            EnchantmentModel enchantment => $"ListenerType={typeName} Id={id} Enchantment={SafeModelId(enchantment)} Card={DescribeCard(enchantment.Card)}",
            AfflictionModel affliction => $"ListenerType={typeName} Id={id} Affliction={SafeModelId(affliction)} Card={DescribeCard(affliction.Card)}",
            PowerModel power => $"ListenerType={typeName} Id={id} Power={SafeModelId(power)} Owner={DescribeCreature(power.Owner)}",
            RelicModel relic => $"ListenerType={typeName} Id={id} Relic={SafeModelId(relic)} Owner={SafePlayerId(relic.Owner)} Removed={SafeRemoved(relic)}",
            PotionModel potion => $"ListenerType={typeName} Id={id} Potion={SafeModelId(potion)} Owner={SafePlayerId(potion.Owner)} Removed={SafeRemoved(potion)}",
            OrbModel orb => $"ListenerType={typeName} Id={id} Orb={SafeModelId(orb)} Owner={SafePlayerId(orb.Owner)} Removed={SafeRemoved(orb)}",
            MonsterModel monster => $"ListenerType={typeName} Id={id} Monster={SafeModelId(monster)} Creature={DescribeCreature(monster.Creature)}",
            _ => $"ListenerType={typeName} Id={id}",
        };
    }

    private static string DescribeCard(CardModel? card)
    {
        if (card == null)
        {
            return "<null>";
        }

        try
        {
            return $"{SafeModelId(card)} Owner={SafePlayerId(card.Owner)} Pile={SafePileType(card)} Removed={card.HasBeenRemovedFromState}";
        }
        catch (Exception ex)
        {
            return $"<card describe threw {ex.GetType().Name}>";
        }
    }

    private static string DescribeCreature(Creature? creature)
    {
        if (creature == null)
        {
            return "<null>";
        }

        try
        {
            return $"{creature.LogName} CombatState={(creature.CombatState == null ? "<null>" : "present")} Player={SafePlayerId(creature.Player)}";
        }
        catch (Exception ex)
        {
            return $"<creature describe threw {ex.GetType().Name}>";
        }
    }

    private static string SafePlayerId(Player? player)
    {
        if (player == null)
        {
            return "<null>";
        }

        try
        {
            return player.NetId.ToString();
        }
        catch (Exception ex)
        {
            return $"<player id threw {ex.GetType().Name}>";
        }
    }

    private static string SafeModelId(AbstractModel model)
    {
        try
        {
            return model.Id.ToString();
        }
        catch (Exception ex)
        {
            return $"<id threw {ex.GetType().Name}>";
        }
    }

    private static string SafePileType(CardModel? card)
    {
        try
        {
            return card?.Pile?.Type.ToString() ?? "<none>";
        }
        catch (Exception ex)
        {
            return $"<pile threw {ex.GetType().Name}>";
        }
    }

    private static bool SafeRemoved(AbstractModel model)
    {
        try
        {
            return model switch
            {
                CardModel card => card.HasBeenRemovedFromState,
                RelicModel relic => relic.HasBeenRemovedFromState,
                PotionModel potion => potion.HasBeenRemovedFromState,
                OrbModel orb => orb.HasBeenRemovedFromState,
                _ => false,
            };
        }
        catch
        {
            return false;
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
        // Card movement / queue / selection flows can reuse the same NCard after vanilla has
        // touched the primary enchantment tab again. Always rebuild from the captured template
        // state before applying our presentation knobs, otherwise IconScale/IconOffset/backing
        // visibility can be lost or compounded across refreshes.
        RestoreEnchantmentBadgePresentation(tab);

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
        ApplyEnchantmentPresentationStyle(tab, icon, label, visualState.Status, visualState.PresentationStyle);
    }

    private static Control DuplicateEnchantmentTab(Control source)
    {
        Control tab = (Control)source.Duplicate();
        RestoreDuplicatedEnchantmentBadgePresentation(source, tab);
        tab.UniqueNameInOwner = false;
        if (tab.Material != null)
        {
            tab.Material = (Material)tab.Material.Duplicate();
        }

        return tab;
    }

    private static void RestoreDuplicatedEnchantmentBadgePresentation(Node source, Node clone)
    {
        if (source is Control sourceControl && clone is Control cloneControl)
        {
            if (EnchantmentIconRestoreStates.TryGetValue(sourceControl, out EnchantmentIconRestoreState? iconState))
            {
                iconState.Restore(cloneControl);
            }

            if (EnchantmentBadgeRestoreStates.TryGetValue(sourceControl, out EnchantmentBadgeRestoreState? badgeState))
            {
                badgeState.Restore(cloneControl);
            }
        }

        if (clone is CanvasItem canvasItem && canvasItem.HasMeta(EnchantmentBadgeHiddenMeta))
        {
            bool wasVisible = canvasItem.HasMeta(EnchantmentBadgeHiddenVisibleMeta)
                ? canvasItem.GetMeta(EnchantmentBadgeHiddenVisibleMeta).AsBool()
                : true;
            canvasItem.Visible = wasVisible;
            canvasItem.RemoveMeta(EnchantmentBadgeHiddenVisibleMeta);
            canvasItem.RemoveMeta(EnchantmentBadgeHiddenMeta);
        }

        Godot.Collections.Array<Node> sourceChildren = source.GetChildren();
        Godot.Collections.Array<Node> cloneChildren = clone.GetChildren();
        int childCount = Math.Min(sourceChildren.Count, cloneChildren.Count);
        for (int i = 0; i < childCount; i++)
        {
            RestoreDuplicatedEnchantmentBadgePresentation(sourceChildren[i], cloneChildren[i]);
        }
    }

    private static void ApplyEnchantmentPresentationStyle(
        Control tab,
        TextureRect? icon,
        Control? label,
        EnchantmentStatus status,
        EnchantmentPresentationStyle style)
    {
        if (!style.ShowBadgeBacking)
        {
            HideEnchantmentBadgeBacking(tab);
            CaptureEnchantmentIconRestoreState(icon);
            if (icon != null)
            {
                // Backing hidden → the vanilla desaturating parent shader is detached, so this path
                // must dim disabled icons itself (gray fallback).
                icon.UseParentMaterial = false;
                icon.SelfModulate = ResolveIconTint(status, style, shaderHandlesDimming: false);
            }
            ApplyEnchantmentIconPresentation(icon, label, style);
            return;
        }

        RestoreEnchantmentBadgePresentation(tab);
        ApplyBadgeBackingTexture(tab, style.BadgeBackingTexture);
        if (icon != null)
        {
            // Backing shown → ApplyStatusToTab leaves the parent desaturation shader active for
            // disabled entries, so only apply an explicit author tint here; a gray fallback would
            // dim the icon twice.
            CaptureEnchantmentIconRestoreState(icon);
            icon.SelfModulate = ResolveIconTint(status, style, shaderHandlesDimming: true);
        }

        ApplyEnchantmentIconPresentation(icon, label, style);

        // Right-aligned badges mirror only the backing layer (icon + label stay upright), so the
        // asymmetric badge art points toward the card edge it now hugs. RestoreEnchantmentBadgePresentation
        // above always un-flips first, so this stays correct when a tab is reused left ↔ right.
        if (style.RightAligned)
        {
            FlipBadgeBacking(tab);
        }
    }

    /// <summary>
    /// Horizontally mirrors the badge backing of <paramref name="tab"/> in place, leaving the
    /// <c>Icon</c>/<c>Label</c> foreground upright so only the backing art is mirrored. The original
    /// state is captured into <see cref="EnchantmentBadgeRestoreStates"/> so
    /// <see cref="RestoreEnchantmentBadgePresentation"/> reverts it.
    /// </summary>
    private static void FlipBadgeBacking(Control tab)
    {
        // The tab itself may BE the backing pixel carrier rather than a container: the enchant
        // appear-VFX template is a TextureRect whose direct child is the Icon. FlipH mirrors only
        // the rendered texture without touching child transforms, so the Icon stays upright and in
        // place. Container tabs (the on-card %Enchantment Control) carry no texture and fall through
        // to flipping their backing children.
        if (tab is TextureRect rootBacking && rootBacking.Texture != null)
        {
            FlipBackingNode(rootBacking);
        }

        FlipBadgeBackingChildren(tab);
    }

    private static void FlipBadgeBackingChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (IsBadgeForegroundNode(child))
            {
                continue;
            }

            // A wrapper that holds the Icon/Label foreground must not itself be scale-flipped (scale
            // propagates to children, which would mirror the icon too). Descend to find the
            // pure-backing leaves instead. Mirrors the SetBadgeBackingVisible safety logic.
            if (ContainsBadgeForegroundNode(child))
            {
                FlipBadgeBackingChildren(child);
                continue;
            }

            if (child is Control control)
            {
                FlipBackingNode(control);
                // The whole pure-backing subtree mirrors with this node; don't recurse and
                // double-flip nested backing art.
                continue;
            }

            FlipBadgeBackingChildren(child);
        }
    }

    private static void FlipBackingNode(Control node)
    {
        EnchantmentBadgeRestoreStates.GetValue(node, static key => new EnchantmentBadgeRestoreState(key));

        if (node is TextureRect textureRect)
        {
            // FlipH mirrors the drawn pixels without altering the node's transform, so any child
            // (e.g. an Icon parented under a TextureRect badge) stays upright and in place. It also
            // doesn't depend on the node's laid-out Size, so it is correct on the first frame.
            textureRect.FlipH = !textureRect.FlipH;
            return;
        }

        // NinePatchRect / other Controls have no FlipH: mirror via a centered negative-X scale.
        // Only reached for pure-backing siblings (no foreground descendants), so nothing else flips.
        node.PivotOffset = node.Size * 0.5f;
        node.Scale = new Vector2(-Mathf.Abs(node.Scale.X), node.Scale.Y);
    }

    private static Color ResolveIconTint(EnchantmentStatus status, EnchantmentPresentationStyle style, bool shaderHandlesDimming)
    {
        if (status == EnchantmentStatus.Disabled)
        {
            Color? explicitTint = style.DisabledIconTint ?? style.IconTint;
            return explicitTint ?? (shaderHandlesDimming ? Colors.White : StsColors.gray);
        }

        return style.IconTint ?? Colors.White;
    }

    private static void ApplyEnchantmentIconPresentation(TextureRect? icon, Control? label, EnchantmentPresentationStyle style)
    {
        float scale = NormalizeIconScale(style.IconScale);
        Vector2 offset = style.IconOffset;
        if (Mathf.IsEqualApprox(scale, 1f) && offset == Vector2.Zero)
        {
            return;
        }

        if (icon != null)
        {
            CaptureEnchantmentIconRestoreState(icon);
            icon.PivotOffset = icon.Size * 0.5f;
            icon.Scale *= scale;
            icon.Position += offset;
        }

        // With NO backing, IconOffset is the only thing positioning the badge content, so the amount
        // Label must move with the Icon or the number is left behind (the reported bug). WITH a
        // backing, the backing frame is the anchor and IconOffset historically nudges only the Icon
        // within it — keep that so existing backed badges that tuned IconOffset aren't disturbed.
        // IconScale stays icon-only either way: the number's size comes from the label's own
        // auto-size, and scaling it would desync from the surrounding card text.
        if (label != null && offset != Vector2.Zero && !style.ShowBadgeBacking)
        {
            CaptureEnchantmentIconRestoreState(label);
            label.Position += offset;
        }
    }

    private static void CaptureEnchantmentIconRestoreState(Control? node)
    {
        if (node != null)
        {
            EnchantmentIconRestoreStates.GetValue(node, static key => new EnchantmentIconRestoreState(key));
        }
    }

    private static float NormalizeIconScale(float scale)
    {
        return scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale)
            ? 1f
            : scale;
    }

    private static void RestoreEnchantmentIconStyle(Control node)
    {
        if (EnchantmentIconRestoreStates.TryGetValue(node, out EnchantmentIconRestoreState? state))
        {
            state.Restore(node);
            EnchantmentIconRestoreStates.Remove(node);
        }
    }

    private static void RestoreEnchantmentBadgePresentation(Control tab)
    {
        if (tab.GetNodeOrNull<TextureRect>("Icon") is { } icon)
        {
            RestoreEnchantmentIconStyle(icon);
        }

        if (tab.GetNodeOrNull<MegaLabel>("Label") is { } label)
        {
            RestoreEnchantmentIconStyle(label);
        }

        RestoreBadgeBackingNode(tab);
        SetBadgeBackingVisible(tab, visible: true);
    }

    private static void HideEnchantmentBadgeBacking(Control tab)
    {
        ClearBadgeBackingTexture(tab);
        SetBadgeBackingVisible(tab, visible: false);
    }

    private static void ApplyBadgeBackingTexture(Control tab, Texture2D? texture)
    {
        if (texture == null)
        {
            return;
        }

        if (TryApplyBadgeBackingTexture(tab, texture))
        {
            return;
        }

        foreach (Node child in tab.GetChildren())
        {
            if (TryApplyBadgeBackingTexture(child, texture))
            {
                return;
            }
        }
    }

    private static bool TryApplyBadgeBackingTexture(Node node, Texture2D texture)
    {
        if (IsBadgeForegroundNode(node))
        {
            return false;
        }

        if (node is TextureRect textureRect)
        {
            EnchantmentBadgeRestoreStates.GetValue(textureRect, static key => new EnchantmentBadgeRestoreState(key));
            textureRect.Texture = texture;
            textureRect.SelfModulate = Colors.White;
            textureRect.Visible = true;
            return true;
        }

        if (node is NinePatchRect ninePatchRect)
        {
            EnchantmentBadgeRestoreStates.GetValue(ninePatchRect, static key => new EnchantmentBadgeRestoreState(key));
            ninePatchRect.Texture = texture;
            ninePatchRect.SelfModulate = Colors.White;
            ninePatchRect.Visible = true;
            return true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (TryApplyBadgeBackingTexture(child, texture))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetBadgeBackingVisible(Node node, bool visible)
    {
        foreach (Node child in node.GetChildren())
        {
            if (IsBadgeForegroundNode(child))
            {
                continue;
            }

            bool containsForeground = ContainsBadgeForegroundNode(child);
            if (visible)
            {
                RestoreBadgeBackingNode(child);
            }
            else if (child is CanvasItem canvasItem)
            {
                if (child is Control control)
                {
                    ClearBadgeBackingTexture(control);
                }

                if (!containsForeground)
                {
                    HideBadgeBackingNode(canvasItem);
                }
            }

            SetBadgeBackingVisible(child, visible);
        }
    }

    private static bool IsBadgeForegroundNode(Node node)
    {
        string name = node.Name.ToString();
        return string.Equals(name, "Icon", StringComparison.Ordinal) ||
               string.Equals(name, "Label", StringComparison.Ordinal);
    }

    private static bool ContainsBadgeForegroundNode(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (IsBadgeForegroundNode(child) || ContainsBadgeForegroundNode(child))
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearBadgeBackingTexture(Control node)
    {
        EnchantmentBadgeRestoreStates.GetValue(node, static key => new EnchantmentBadgeRestoreState(key));

        if (node is TextureRect textureRect)
        {
            textureRect.Texture = null;
        }

        if (node is NinePatchRect ninePatchRect)
        {
            ninePatchRect.Texture = null;
        }

        node.SelfModulate = Colors.Transparent;
    }

    private static void HideBadgeBackingNode(CanvasItem node)
    {
        if (!node.HasMeta(EnchantmentBadgeHiddenMeta))
        {
            node.SetMeta(EnchantmentBadgeHiddenMeta, true);
            node.SetMeta(EnchantmentBadgeHiddenVisibleMeta, node.Visible);
        }

        node.Visible = false;
    }

    private static void RestoreBadgeBackingNode(Node node)
    {
        if (node is Control control &&
            EnchantmentBadgeRestoreStates.TryGetValue(control, out EnchantmentBadgeRestoreState? state))
        {
            state.Restore(control);
            EnchantmentBadgeRestoreStates.Remove(control);
        }

        if (node is CanvasItem canvasItem && canvasItem.HasMeta(EnchantmentBadgeHiddenMeta))
        {
            bool wasVisible = canvasItem.HasMeta(EnchantmentBadgeHiddenVisibleMeta)
                ? canvasItem.GetMeta(EnchantmentBadgeHiddenVisibleMeta).AsBool()
                : true;
            canvasItem.Visible = wasVisible;
            canvasItem.RemoveMeta(EnchantmentBadgeHiddenVisibleMeta);
            canvasItem.RemoveMeta(EnchantmentBadgeHiddenMeta);
        }
    }

    private static List<EnchantmentSlotLayout> BuildEnchantmentSlotLayouts(
        NCard cardNode,
        Control primaryTab,
        IReadOnlyList<EnchantmentVisualState> visualStates,
        Vector2 defaultPosition)
    {
        int slotCount = visualStates.Count;
        List<EnchantmentSlotLayout> layouts = new(slotCount);
        if (slotCount <= 0)
        {
            return layouts;
        }

        CardModel? model = cardNode.Model;
        // Left column anchor: the vanilla enchantment position (with the no-star-cost 45px lift),
        // taken from the STABLE default — NOT primaryTab.Position. Reading the tab's live position
        // would feed our own previous placement back in: when the primary badge is itself
        // right-aligned, its Position is last frame's right-column X, and mirroring that flips it
        // back left on the next refresh — the hover "left/right jitter".
        Vector2 leftAnchor = model != null && (model.HasStarCostX || model.CurrentStarCost >= 0)
            ? defaultPosition
            : defaultPosition + Vector2.Up * 45f;
        float rowOffset = GetExtraEnchantmentRowOffset(primaryTab);

        // Right column anchor: the energy icon reflected across the card's vertical midline, so the
        // first right-aligned badge is symmetric to the energy icon (mirrored X, same top Y) and the
        // column ignores the star-cost lift. Computed from live transforms (not primaryTab.Position),
        // so it's stable across refreshes. Falls back to left X / default Y if unavailable.
        float badgeWidth = primaryTab.Size.X * primaryTab.Scale.X;
        Vector2 rightAnchor = TryResolveRightColumnAnchor(cardNode, primaryTab, badgeWidth, out Vector2 resolvedRight)
            ? resolvedRight
            : new Vector2(leftAnchor.X, defaultPosition.Y);

        int leftRow = 0;
        int rightRow = 0;
        for (int i = 0; i < slotCount; i++)
        {
            Vector2 position = visualStates[i].PresentationStyle.RightAligned
                ? rightAnchor + Vector2.Down * (rightRow++ * rowOffset)
                : leftAnchor + Vector2.Down * (leftRow++ * rowOffset);

            layouts.Add(new EnchantmentSlotLayout(
                position,
                primaryTab.Scale,
                primaryTab.Rotation,
                primaryTab.PivotOffset,
                primaryTab.ZIndex,
                primaryTab.TopLevel));
        }

        return layouts;
    }

    /// <summary>
    /// Right-column anchor = the card's cost icon reflected across the card's vertical midline,
    /// expressed in the badge parent's local space (where the enchantment tabs'
    /// <see cref="Control.Position"/> lives). The badge's right edge aligns to the mirrored cost-icon
    /// left edge, and its top to the cost-icon top, so the first right-aligned badge is symmetric to
    /// the cost icon the card actually shows (StarIcon for star-cost cards, else EnergyIcon).
    /// </summary>
    /// <remarks>
    /// Computed from live transforms (not <c>primaryTab.Position</c>) so it stays correct and stable
    /// across refreshes under the card's hand rotation/scale. The card lays out with centered anchors
    /// over a zero-size <c>CardContainer</c>, so the midline is at the container origin
    /// (<c>body.Size*0.5 == origin</c>) — a perfectly valid axis even at local X ≈ 0. Returns
    /// <c>false</c> only when the body, badge parent, or cost icon are unavailable.
    /// </remarks>
    private static bool TryResolveRightColumnAnchor(NCard cardNode, Control primaryTab, float badgeWidth, out Vector2 anchor)
    {
        anchor = Vector2.Zero;
        Control? body = cardNode.Body;
        if (body == null || !GodotObject.IsInstanceValid(body))
        {
            return false;
        }

        if (primaryTab.GetParent() is not CanvasItem badgeRoot || !GodotObject.IsInstanceValid(badgeRoot))
        {
            return false;
        }

        Control? costIcon = ResolveCostIcon(cardNode);
        if (costIcon == null || !GodotObject.IsInstanceValid(costIcon))
        {
            return false;
        }

        Transform2D toBadgeLocal = badgeRoot.GetGlobalTransform().AffineInverse();
        // Card vertical midline: body.Size*0.5 is the container origin under centered-anchor layout.
        float axisX = (toBadgeLocal * (body.GetGlobalTransform() * (body.Size * 0.5f))).X;
        // Cost icon top-left corner in badge-parent local space.
        Vector2 costLocal = toBadgeLocal * costIcon.GetGlobalTransform().Origin;
        // Mirror across the midline: badge right edge ↔ mirrored cost-icon left edge; top aligned.
        anchor = new Vector2(2f * axisX - costLocal.X - badgeWidth, costLocal.Y);
        return true;
    }

    /// <summary>
    /// The cost icon the card actually displays: StarIcon for star-cost cards, otherwise EnergyIcon.
    /// Chosen by the same model check vanilla uses to position the enchantment tab
    /// (<c>HasStarCostX || CurrentStarCost &gt;= 0</c>), so it doesn't depend on the icons' Visible
    /// flags having been updated yet this frame. Falls back to whichever icon is available.
    /// </summary>
    private static Control? ResolveCostIcon(NCard cardNode)
    {
        Control? energyIcon = NCardEnergyIconField?.GetValue(cardNode) as Control;
        Control? starIcon = NCardStarIconField?.GetValue(cardNode) as Control;
        CardModel? model = cardNode.Model;
        bool usesStar = model != null && (model.HasStarCostX || model.CurrentStarCost >= 0);
        Control? preferred = usesStar ? starIcon : energyIcon;
        return preferred ?? energyIcon ?? starIcon;
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
            visualStates,
            defaultPosition);

        ApplyEnchantmentSlotLayout(primaryTab, slotLayouts[0], visible: true);
        ApplyEnchantmentVisualState(primaryTab, visualStates[0]);
        for (int i = 0; i < state.ExtraTabs.Count; i++)
        {
            Control tab = state.ExtraTabs[i];
            bool shouldShow = i < expectedExtraTabCount;
            if (!shouldShow)
            {
                RestoreEnchantmentBadgePresentation(tab);
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

        if (HasDisplayOnlyExtraIconVisuals(model))
        {
            return true;
        }

        EnchantmentModel? primary = model.Enchantment;
        return primary != null && MultiEnchantmentStackSupport.GetVisualStackCount(primary) > 1;
    }

    private static bool NeedsPresentationRefresh(CardModel? model)
    {
        if (model == null)
        {
            return false;
        }

        foreach (EnchantmentVisualState visualState in GetOrderedVisualStates(model))
        {
            Type? styleType = visualState.MarkerType ?? visualState.IconSource?.GetType();
            if (styleType == null)
            {
                continue;
            }

            EnchantmentPresentationStyle defaultStyle = EnchantmentRegistry.GetDefaultPresentationStyle(styleType);
            if (visualState.PresentationStyle != defaultStyle)
            {
                return true;
            }
        }

        return false;
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
        // primaryTab.Position / Size below already cover the right-aligned column's inputs: the
        // mirror reflects leftAnchor.X (= primaryTab.Position.X) about the card midline using
        // primaryTab.Size.X as the badge width, and the midline is a fixed scene constant.
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
        hash.Add(visualState.PresentationStyle.ShowBadgeBacking);
        hash.Add(visualState.PresentationStyle.PreserveExtraTextBbCode);
        hash.Add(NormalizeIconScale(visualState.PresentationStyle.IconScale));
        hash.Add(visualState.PresentationStyle.IconOffset);
        hash.Add(visualState.PresentationStyle.IconTint);
        hash.Add(visualState.PresentationStyle.DisabledIconTint);
        hash.Add(visualState.PresentationStyle.BadgeBackingTexture);
        hash.Add(visualState.PresentationStyle.BadgeBackingTexture?.ResourcePath);
        hash.Add(visualState.PresentationStyle.HideWhenDisabled);
        hash.Add(visualState.PresentationStyle.DisplayPriority);
        hash.Add(visualState.PresentationStyle.RightAligned);
        hash.Add(visualState.IsDisplayOnly);
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

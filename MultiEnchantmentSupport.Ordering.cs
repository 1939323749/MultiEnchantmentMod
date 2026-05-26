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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type AnchorType, Type IconType), byte> VisualSliceIconResolutionWarnings = new();

    public static EnchantmentModel? GetMostRecentlyAppliedEnchantment(CardModel? card)
    {
        if (card == null)
        {
            return null;
        }

        if (CardStates.TryGetValue(card, out CardEnchantmentState? state) &&
            state.LastAppliedEnchantment?.Card == card)
        {
            return state.LastAppliedEnchantment;
        }

        IReadOnlyList<EnchantmentModel> extras = GetAdditionalEnchantments(card);
        if (extras.Count > 0)
        {
            return extras[^1];
        }

        return card.Enchantment;
    }

    private static IReadOnlyList<ModelId> GetApplicationOrder(CardModel? card)
    {
        if (card == null || !CardStates.TryGetValue(card, out CardEnchantmentState? state))
        {
            return Array.Empty<ModelId>();
        }

        return state.ApplicationOrder;
    }

    internal static ScopeRuntimeState EnsureScopeState(CardModel card, EnchantmentModel enchantment)
    {
        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        if (!state.ScopeStates.TryGetValue(enchantment, out ScopeRuntimeState? scopeState))
        {
            scopeState = new ScopeRuntimeState();
            // Lazy restore from serialized form. Covers save-restore and multiplayer packet-receive
            // paths: SerializableEnchantment.Props was copied into enchantment.Props by
            // EnchantmentFromSerializablePostfix → RestoreSerializedProps; the first EnsureScopeState
            // for this (card, enchantment) pair on the receiving side rehydrates the counters.
            MultiEnchantmentScopeSupport.TryRestoreScopeStateFromProps(enchantment, scopeState);
            state.ScopeStates[enchantment] = scopeState;
        }

        return scopeState;
    }

    /// <summary>
    /// Non-mutating lookup used by the ToSerializable boundary to capture only the
    /// <see cref="ScopeRuntimeState"/>s that have actually been instantiated. Returns false +
    /// null when no state exists for the pair, so we don't materialize a default state inside
    /// serialization (which would inflate every multiplayer packet with empty payloads).
    /// </summary>
    internal static bool TryGetExistingScopeState(CardModel card, EnchantmentModel enchantment, out ScopeRuntimeState? state)
    {
        state = null;
        if (!CardStates.TryGetValue(card, out CardEnchantmentState? cardState))
        {
            return false;
        }

        if (!cardState.ScopeStates.TryGetValue(enchantment, out ScopeRuntimeState? scopeState))
        {
            return false;
        }

        state = scopeState;
        return true;
    }

    internal static IEnumerable<EnchantmentModel> GetOrderedEnchantmentsForRemoval(CardModel card)
    {
        // Build a ModelId → live-instance queue, not a flat dict: DuplicateInstance and
        // ExistenceStack enchantments can have multiple live instances sharing the same Id on
        // the same card. A naive ToDictionary call throws ArgumentException on the duplicate
        // key, which crashes the end-of-turn scope sweep (HandleTurnEnd) and stalls the enemy
        // turn. Each ApplicationOrder entry pops one instance from its bucket so the LIFO
        // sweep walks every instance exactly once.
        List<EnchantmentModel> ordered = new();
        HashSet<EnchantmentModel> seen = new(ReferenceEqualityComparer.Instance);
        Dictionary<ModelId, Queue<EnchantmentModel>> byId = new();
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            if (!byId.TryGetValue(enchantment.Id, out Queue<EnchantmentModel>? queue))
            {
                queue = new Queue<EnchantmentModel>();
                byId[enchantment.Id] = queue;
            }

            queue.Enqueue(enchantment);
        }

        IReadOnlyList<ModelId> applicationOrder = GetApplicationOrder(card);
        for (int i = applicationOrder.Count - 1; i >= 0; i--)
        {
            if (byId.TryGetValue(applicationOrder[i], out Queue<EnchantmentModel>? queue) &&
                queue.Count > 0)
            {
                EnchantmentModel enchantment = queue.Dequeue();
                if (seen.Add(enchantment))
                {
                    ordered.Add(enchantment);
                }
            }
        }

        foreach (EnchantmentModel enchantment in GetEnchantments(card).Reverse())
        {
            if (seen.Add(enchantment))
            {
                ordered.Add(enchantment);
            }
        }

        return ordered;
    }

    internal static void QueuePendingRemoval(CardModel card, EnchantmentModel enchantment, RemovalReason reason)
    {
        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        if (state.PendingRemovals.Any(entry => ReferenceEquals(entry.Enchantment, enchantment)))
        {
            return;
        }

        state.PendingRemovals.Add(new PendingRemovalEntry(enchantment, reason));
    }

    internal static void FlushPendingRemovals(CardModel card)
    {
        if (!CardStates.TryGetValue(card, out CardEnchantmentState? state) || state.PendingRemovals.Count == 0)
        {
            return;
        }

        List<PendingRemovalEntry> pending = state.PendingRemovals.ToList();
        state.PendingRemovals.Clear();
        foreach (PendingRemovalEntry entry in pending)
        {
            RemoveEnchantmentInternal(
                card,
                entry.Enchantment,
                entry.Reason,
                bypassVeto: entry.Reason == RemovalReason.CardCleared,
                refreshCard: false,
                triggerChanged: false);
        }

        card.DynamicVars.RecalculateForUpgradeOrEnchant();
        card.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(card);
        TriggerEnchantmentChanged(card);
    }


    private static void AppendApplicationOrder(CardModel card, ModelId enchantmentId)
    {
        CardStates.GetOrCreateValue(card).ApplicationOrder.Add(enchantmentId);
    }

    private static void RemoveOneApplicationOrder(CardEnchantmentState state, ModelId enchantmentId)
    {
        int index = state.ApplicationOrder.FindIndex(id => id.Equals(enchantmentId));
        if (index >= 0)
        {
            state.ApplicationOrder.RemoveAt(index);
        }
    }

    private static void RebuildApplicationOrder(CardModel card)
    {
        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        state.ApplicationOrder.Clear();
        state.ApplicationOrder.AddRange(
            GetEnchantments(card)
                .SelectMany(static enchantment =>
                    Enumerable.Repeat(enchantment.Id, MultiEnchantmentStackSupport.GetVisualStackCount(enchantment))));
    }

    private static void CopyApplicationOrder(CardModel source, CardModel target)
    {
        if (!CardStates.TryGetValue(source, out CardEnchantmentState? sourceState) ||
            sourceState.ApplicationOrder.Count == 0)
        {
            return;
        }

        CardEnchantmentState targetState = CardStates.GetOrCreateValue(target);
        targetState.ApplicationOrder.Clear();
        targetState.ApplicationOrder.AddRange(sourceState.ApplicationOrder);
    }

    private static void CopyScopeState(
        CardModel sourceCard,
        EnchantmentModel sourceEnchantment,
        CardModel targetCard,
        EnchantmentModel targetEnchantment)
    {
        if (!CardStates.TryGetValue(sourceCard, out CardEnchantmentState? sourceState) ||
            !sourceState.ScopeStates.TryGetValue(sourceEnchantment, out ScopeRuntimeState? sourceScopeState))
        {
            return;
        }

        ScopeRuntimeState targetScopeState = EnsureScopeState(targetCard, targetEnchantment);
        targetScopeState.Scope = sourceScopeState.Scope;
        targetScopeState.OverrideScope = sourceScopeState.OverrideScope;
        targetScopeState.ActivationCount = sourceScopeState.ActivationCount;
        targetScopeState.TurnsRemaining = sourceScopeState.TurnsRemaining;
    }

    private static void PruneEmptyCardState(CardModel card, CardEnchantmentState state)
    {
        if (state.ExtraEnchantments.Count == 0 &&
            state.ApplicationOrder.Count == 0 &&
            state.ScopeStates.Count == 0 &&
            state.PendingRemovals.Count == 0 &&
            state.LastAppliedEnchantment == null)
        {
            CardStates.Remove(card);
        }
    }

    private static void SeedMissingApplicationOrder(CardModel card)
    {
        if (!HasAnyEnchantments(card))
        {
            return;
        }

        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        if (state.ApplicationOrder.Count > 0)
        {
            return;
        }

        state.ApplicationOrder.AddRange(
            GetDefaultOrderedEnchantmentEntries(card).Select(static entry => entry.Enchantment.Id));
    }

    private static List<OrderedEnchantmentEntry> GetOrderedEnchantmentEntries(CardModel? card)
    {
        return OrderEntries(
            card,
            GetDefaultOrderedEnchantmentEntries(card),
            static entry => entry.Enchantment.Id);
    }

    private static List<OrderedEnchantmentEntry> GetDefaultOrderedEnchantmentEntries(CardModel? card)
    {
        List<OrderedEnchantmentEntry> entries = new();
        HashSet<Type> handledMergedTypes = new();
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            if (MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType()) == EnchantmentStackBehavior.MergeAmount)
            {
                if (!handledMergedTypes.Add(enchantment.GetType()) ||
                    !MultiEnchantmentStackSupport.TryGetMergedStackAmounts(enchantment, out int[] stackAmounts))
                {
                    continue;
                }

                // Merge all stack slices into one entry with the total amount, so each
                // enchantment contributes exactly once to replay count.
                entries.Add(new OrderedEnchantmentEntry(enchantment, stackAmounts.Sum()));
                continue;
            }

            entries.Add(new OrderedEnchantmentEntry(enchantment, enchantment.Amount));
        }

        return entries;
    }

    private static List<OrderedDynamicVarEnchantmentEntry> GetOrderedDynamicVarEnchantmentEntries(CardModel? card)
    {
        return OrderEntries(
            card,
            GetDefaultOrderedDynamicVarEnchantmentEntries(card),
            static entry => entry.Enchantment.Id);
    }

    private static List<OrderedDynamicVarEnchantmentEntry> GetDefaultOrderedDynamicVarEnchantmentEntries(CardModel? card)
    {
        List<OrderedDynamicVarEnchantmentEntry> entries = new();
        HashSet<Type> handledTypes = new();
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            Type enchantmentType = enchantment.GetType();
            if (!handledTypes.Add(enchantmentType))
            {
                continue;
            }

            EnchantmentStackSnapshot snapshot = MultiEnchantmentStackSupport.GetSnapshot(enchantment);
            if (snapshot.Definition.Behavior == EnchantmentStackBehavior.MergeAmount)
            {
                foreach (EnchantmentStackSlice slice in snapshot.GameplaySlices)
                {
                    if (!slice.IsActive)
                    {
                        continue;
                    }

                    entries.Add(new OrderedDynamicVarEnchantmentEntry(
                        enchantment,
                        MultiEnchantmentStackSupport.CreateSingleSliceSnapshot(snapshot, slice)));
                }

                continue;
            }

            entries.Add(new OrderedDynamicVarEnchantmentEntry(enchantment, snapshot));
        }

        return entries;
    }

    private static List<OrderedVisualEntry> GetDefaultOrderedVisualEntries(CardModel? card)
    {
        List<OrderedVisualEntry> entries = new();
        HashSet<Type> handledTypes = new();
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            if (!handledTypes.Add(enchantment.GetType()))
            {
                continue;
            }

            EnchantmentStackSnapshot snapshot = MultiEnchantmentStackSupport.GetSnapshot(enchantment);
            IReadOnlyList<EnchantmentVisualSlice>? customVisualSlices = MultiEnchantmentStackSupport.GetValidCustomVisualSlices(snapshot, enchantment);
            int sliceIndex = 0;
            foreach (EnchantmentStackSlice slice in snapshot.VisualSlices)
            {
                EnchantmentVisualSlice? customVisualSlice = sliceIndex < customVisualSlices?.Count
                    ? customVisualSlices[sliceIndex]
                    : null;

                entries.Add(new OrderedVisualEntry(
                    enchantment.Id,
                    new EnchantmentVisualState(
                        ResolveVisualSliceIcon(enchantment, snapshot, customVisualSlice),
                        GetDisplayAmount(enchantment, slice.Amount),
                        enchantment.ShowAmount,
                        slice.Status)));
                sliceIndex++;
            }
        }

        return entries;
    }

    private static Texture2D ResolveVisualSliceIcon(
        EnchantmentModel anchor,
        EnchantmentStackSnapshot snapshot,
        EnchantmentVisualSlice? visualSlice)
    {
        if (visualSlice?.IconTexture is { } iconTexture)
        {
            return iconTexture;
        }

        Type? iconType = visualSlice?.IconEnchantmentType;
        if (iconType == null)
        {
            return anchor.Icon;
        }

        if (!typeof(EnchantmentModel).IsAssignableFrom(iconType))
        {
            LogVisualSliceIconResolutionFailure(
                anchor.GetType(),
                iconType,
                $"{iconType.FullName ?? iconType.Name} is not an {nameof(EnchantmentModel)} subclass.");
            return anchor.Icon;
        }

        EnchantmentModel? liveIconSource = snapshot.Card == null
            ? null
            : GetEnchantments(snapshot.Card).FirstOrDefault(enchantment => enchantment.GetType() == iconType);
        if (liveIconSource != null)
        {
            return liveIconSource.Icon;
        }

        if (TryCreateDefaultVisualSliceIcon(iconType, out Texture2D defaultIcon, out string? failureReason))
        {
            return defaultIcon;
        }

        LogVisualSliceIconResolutionFailure(anchor.GetType(), iconType, failureReason);
        return anchor.Icon;
    }

    private static bool TryCreateDefaultVisualSliceIcon(
        Type iconType,
        out Texture2D defaultIcon,
        out string? failureReason)
    {
        try
        {
            if (Activator.CreateInstance(iconType) is not EnchantmentModel defaultInstance)
            {
                defaultIcon = null!;
                failureReason = "Activator.CreateInstance returned null.";
                return false;
            }

            if (defaultInstance.Icon == null)
            {
                defaultIcon = null!;
                failureReason = "Constructed enchantment did not provide a default icon.";
                return false;
            }

            defaultIcon = defaultInstance.Icon;
            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            defaultIcon = null!;
            failureReason = ex.GetBaseException().Message;
            return false;
        }
    }

    private static void LogVisualSliceIconResolutionFailure(
        Type anchorType,
        Type iconType,
        string? failureReason)
    {
        if (!VisualSliceIconResolutionWarnings.TryAdd((anchorType, iconType), 0))
        {
            return;
        }

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment] Failed to resolve visual-slice icon for {anchorType.FullName ?? anchorType.Name} " +
            $"from {iconType.FullName ?? iconType.Name}; using {anchorType.FullName ?? anchorType.Name}. " +
            $"Error: {failureReason ?? "Unknown error."}");
    }

    private static List<OrderedVisualEntry> GetOrderedVisualEntries(CardModel? card)
    {
        return OrderEntries(
            card,
            GetDefaultOrderedVisualEntries(card),
            static entry => entry.EnchantmentId);
    }

    private static List<TEntry> OrderEntries<TEntry>(
        CardModel? card,
        List<TEntry> defaultEntries,
        Func<TEntry, ModelId> idSelector)
    {
        if (card == null)
        {
            return defaultEntries;
        }

        IReadOnlyList<ModelId> order = GetApplicationOrder(card);
        if (order.Count == 0 || order.Count != defaultEntries.Count)
        {
            return defaultEntries;
        }

        Dictionary<ModelId, Queue<TEntry>> entriesById = new();
        foreach (TEntry entry in defaultEntries)
        {
            ModelId enchantmentId = idSelector(entry);
            if (!entriesById.TryGetValue(enchantmentId, out Queue<TEntry>? queue))
            {
                queue = new Queue<TEntry>();
                entriesById[enchantmentId] = queue;
            }

            queue.Enqueue(entry);
        }

        List<TEntry> orderedEntries = new(order.Count);
        foreach (ModelId enchantmentId in order)
        {
            if (!entriesById.TryGetValue(enchantmentId, out Queue<TEntry>? queue) ||
                queue.Count == 0)
            {
                return defaultEntries;
            }

            orderedEntries.Add(queue.Dequeue());
        }

        return entriesById.Values.Any(static queue => queue.Count > 0)
            ? defaultEntries
            : orderedEntries;
    }

    private static int GetDisplayAmount(OrderedEnchantmentEntry entry)
    {
        return EvaluateWithEffectiveAmount(entry, enchantment => enchantment.DisplayAmount);
    }

    private static int GetDisplayAmount(EnchantmentModel enchantment, int effectiveAmount)
    {
        return EvaluateWithEffectiveAmount(
            new OrderedEnchantmentEntry(enchantment, effectiveAmount),
            static value => value.DisplayAmount);
    }

    private static T EvaluateWithEffectiveAmount<T>(OrderedEnchantmentEntry entry, Func<EnchantmentModel, T> evaluator)
    {
        EnchantmentModel enchantment = entry.Enchantment;
        if (entry.EffectiveAmount == enchantment.Amount)
        {
            return evaluator(enchantment);
        }

        int originalAmount = enchantment.Amount;
        enchantment.Amount = entry.EffectiveAmount;
        SyncAmountDependentDynamicVars(enchantment);
        try
        {
            return evaluator(enchantment);
        }
        finally
        {
            enchantment.Amount = originalAmount;
            SyncAmountDependentDynamicVars(enchantment);
        }
    }

    /// <summary>
    /// Keeps DynamicVars in sync with Amount for enchantments whose vanilla EnchantPlayCount
    /// reads DynamicVars instead of Amount.  Without this, EvaluateWithEffectiveAmount would
    /// temporarily change Amount while DynamicVars["Times"] stayed at the merged total, causing
    /// per-slice evaluations to add the full total each time (e.g. Spiral ×2 → replay 4).
    /// </summary>
    private static void SyncAmountDependentDynamicVars(EnchantmentModel enchantment)
    {
        if (enchantment is Glam or Spiral)
        {
            enchantment.DynamicVars["Times"].BaseValue = enchantment.Amount;
        }
    }

}

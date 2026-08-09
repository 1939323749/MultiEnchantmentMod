using System;
using System.Collections.Concurrent;
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
    // Diagnostic de-dupe bounded by the number of registered visual-slice type pairs.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type AnchorType, Type IconType), byte> VisualSliceIconResolutionWarnings = new();

    public static EnchantmentModel? GetMostRecentlyAppliedEnchantment(CardModel? card)
    {
        if (card == null)
        {
            return null;
        }

        if (CardStates.TryGetValue(card, out CardEnchantmentState? state) &&
            state.LastAppliedEnchantment?.Card == card &&
            IsGameplayEnchantment(state.LastAppliedEnchantment))
        {
            return state.LastAppliedEnchantment;
        }

        List<EnchantmentModel> extras = GetAdditionalEnchantments(card)
            .Where(IsGameplayEnchantment)
            .ToList();
        if (extras.Count > 0)
        {
            return extras[^1];
        }

        return card.Enchantment != null && IsGameplayEnchantment(card.Enchantment)
            ? card.Enchantment
            : null;
    }

    /// <summary>
    /// Returns the enchantment most recently applied to <paramref name="card"/> during the current
    /// player turn, or <c>null</c> when nothing has been applied since the turn started. Unlike
    /// <see cref="GetMostRecentlyAppliedEnchantment"/> this does NOT fall back to existing
    /// enchantments — it is purely "what did I inject this turn".
    /// </summary>
    public static EnchantmentModel? GetMostRecentlyAppliedEnchantmentThisTurn(CardModel? card)
    {
        if (card == null)
        {
            return null;
        }

        if (CardStates.TryGetValue(card, out CardEnchantmentState? state) &&
            state.LastAppliedEnchantmentThisTurn?.Card == card &&
            IsGameplayEnchantment(state.LastAppliedEnchantmentThisTurn))
        {
            return state.LastAppliedEnchantmentThisTurn;
        }

        return null;
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
        foreach (EnchantmentModel enchantment in GetGameplayEnchantments(card))
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

        foreach (EnchantmentModel enchantment in GetGameplayEnchantments(card).Reverse())
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

        const int maxFlushBatches = 16;
        int batchCount = 0;
        do
        {
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

            batchCount++;
            if (batchCount >= maxFlushBatches && state.PendingRemovals.Count > 0)
            {
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment] Pending removal flush for Card={card.Id} exceeded {maxFlushBatches} batches; leaving {state.PendingRemovals.Count} queued removal(s) for the next flush.");
                break;
            }
        }
        while (state.PendingRemovals.Count > 0);

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
            GetGameplayEnchantments(card)
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

        // ApplicationOrder is the gameplay replay order (RebuildApplicationOrder / SeedMissing are
        // gameplay-only), so never carry marker ids across — they have no gameplay order and would be
        // stripped on the next rebuild anyway, leaving clone and rebuild paths inconsistent.
        HashSet<ModelId> gameplayIds = GetGameplayEnchantments(source)
            .Select(static enchantment => enchantment.Id)
            .ToHashSet();

        CardEnchantmentState targetState = CardStates.GetOrCreateValue(target);
        targetState.ApplicationOrder.Clear();
        targetState.ApplicationOrder.AddRange(sourceState.ApplicationOrder.Where(gameplayIds.Contains));
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
            state.LastAppliedEnchantment == null &&
            state.LastAppliedEnchantmentThisTurn == null)
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
        using Perf.Scope _perf = Perf.Measure("GetOrderedEnchantmentEntries");
        return OrderEntries(
            card,
            GetDefaultOrderedEnchantmentEntries(card),
            static entry => entry.Enchantment.Id);
    }

    private static List<OrderedEnchantmentEntry> GetDefaultOrderedEnchantmentEntries(CardModel? card)
    {
        List<OrderedEnchantmentEntry> entries = new();
        HashSet<Type> handledMergedTypes = new();
        foreach (EnchantmentModel enchantment in GetGameplayEnchantments(card))
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
        foreach (EnchantmentModel enchantment in GetGameplayEnchantments(card))
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

            // Invisible enchantments render no badge: skip before building visual slices so
            // they consume no badge slot. The type stays claimed in handledTypes so a same-type
            // display-only marker still follows the normal live-suppression rules.
            if (EnchantmentRegistry.IsInvisible(enchantment.GetType()))
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
                        slice.Status,
                        EnchantmentRegistry.GetPresentationStyle(enchantment.GetType()),
                        MarkerType: enchantment is MarkerEnchantmentModel ? enchantment.GetType() : null,
                        StoredMarker: enchantment as MarkerEnchantmentModel,
                        IconSource: enchantment)));
                sliceIndex++;
            }
        }

        AddDisplayOnlyMarkerEntries(card, entries, handledTypes);
        return entries;
    }

    private static void AddDisplayOnlyMarkerEntries(
        CardModel? card,
        List<OrderedVisualEntry> entries,
        HashSet<Type> handledTypes)
    {
        if (card == null || !MarkerDisplayRegistry.HasProviders)
        {
            return;
        }

        foreach ((MarkerDisplay display, Texture2D icon, EnchantmentStatus status, EnchantmentModel? source) in EnumerateShowingDisplayOnlyMarkers(card, handledTypes))
        {
            entries.Add(new OrderedVisualEntry(
                CreateDisplayOnlyVisualId(display.EnchantmentType),
                new EnchantmentVisualState(
                    icon,
                    display.Amount,
                    display.ShowAmount,
                    status,
                    display.PresentationStyle ?? EnchantmentRegistry.GetPresentationStyle(display.EnchantmentType),
                    IsDisplayOnly: true,
                    MarkerType: display.EnchantmentType,
                    IconSource: source)));
        }
    }

    // Shared resolution for "which display-only markers are currently showing on this card", reused
    // by visual-entry building and by card hover-tip aggregation so both honor the exact same
    // ShouldDisplay predicate, live-enchantment suppression, and icon-source resolution. Mutates
    // <paramref name="handledTypes"/> just like the visual path (claims the type unless the display
    // opts into ShowWithLiveEnchantment).
    private static IEnumerable<(MarkerDisplay Display, Texture2D Icon, EnchantmentStatus Status, EnchantmentModel? Source)> EnumerateShowingDisplayOnlyMarkers(
        CardModel card,
        HashSet<Type> handledTypes)
    {
        IReadOnlyList<MarkerDisplay> displays = MarkerDisplayRegistry.GetDisplays(card);
        if (displays.Count == 0)
        {
            yield break;
        }

        bool isCombatCard = card.CombatState != null;
        bool isPreviewCard = card.IsEnchantmentPreview;
        foreach (MarkerDisplay display in displays)
        {
            Type enchantmentType = display.EnchantmentType;
            if (display.ShouldDisplay != null)
            {
                bool shouldDisplay;
                try
                {
                    bool hasLiveEnchantment = GetEnchantments(card).Any(enchantment => enchantment.GetType() == enchantmentType);
                    MarkerDisplayContext context = new(card, hasLiveEnchantment, isCombatCard, isPreviewCard);
                    shouldDisplay = display.ShouldDisplay(context);
                }
                catch (Exception ex)
                {
                    MultiEnchantmentMod.Logger.Warn(
                        $"[MultiEnchantment] Marker display predicate failed for {enchantmentType.FullName}: {ex}");
                    continue;
                }

                if (!shouldDisplay)
                {
                    continue;
                }
            }

            // By default suppress the marker when a live instance (or earlier marker) of this type
            // already claimed a slot; ShowWithLiveEnchantment opts out so the marker coexists with
            // the live badge instead of being dropped.
            if (!display.ShowWithLiveEnchantment && !handledTypes.Add(enchantmentType))
            {
                continue;
            }

            // Icon priority: an explicit MarkerDisplay.Icon wins (the only way to use arbitrary
            // art, since EnchantmentModel.Icon is non-virtual); then a supplied Enchantment's icon;
            // then the type's canonical model icon (resolved from ModelDb — never constructed).
            EnchantmentModel? iconSource = display.Enchantment ?? TryResolveDefaultEnchantment(enchantmentType);
            Texture2D? icon = display.Icon ?? (iconSource == null ? null : SafeGetIcon(iconSource));
            if (icon == null)
            {
                LogDisplayOnlyMarkerFailure(enchantmentType,
                    "no icon resolved — set MarkerDisplay.Icon to a texture, supply MarkerDisplay.Enchantment, " +
                    "or ship a texture at the model's icon path (EnchantmentModel.Icon is non-virtual and cannot be overridden).");
                continue;
            }

            EnchantmentStatus status = iconSource?.Status ?? EnchantmentStatus.Normal;
            yield return (display, icon, status, iconSource);
        }
    }

    // Hover tips for the display-only markers currently shown on a card. Vanilla surfaces enchantment
    // hover info at the card level (CardModel.HoverTips aggregates Enchantment.HoverTips), and the
    // mod already extends that to stored extra enchantments — but a provider marker is not a stored
    // enchantment, so without this its icon has no explanation. Markers suppressed by a live
    // enchantment of the same type contribute nothing (that enchantment already surfaces its tips),
    // avoiding duplicates.
    internal static IEnumerable<IHoverTip> GetDisplayOnlyMarkerHoverTips(CardModel? card)
    {
        if (card == null || !MarkerDisplayRegistry.HasProviders)
        {
            return Array.Empty<IHoverTip>();
        }

        HashSet<Type> handledTypes = new();
        foreach (EnchantmentModel live in GetEnchantments(card))
        {
            handledTypes.Add(live.GetType());
        }

        List<IHoverTip>? tips = null;
        foreach ((MarkerDisplay _, Texture2D _, EnchantmentStatus _, EnchantmentModel? source) in EnumerateShowingDisplayOnlyMarkers(card, handledTypes))
        {
            if (source == null)
            {
                continue;
            }

            foreach (IHoverTip tip in source.HoverTips)
            {
                (tips ??= new List<IHoverTip>()).Add(tip);
            }
        }

        return (IEnumerable<IHoverTip>?)tips ?? Array.Empty<IHoverTip>();
    }

    // The marker types that currently render on a card after provider evaluation,
    // live-enchantment suppression, HideWhenDisabled filtering, and DisplayPriority ordering.
    // Includes both stored MarkerEnchantmentModel instances and display-only provider markers.
    internal static IReadOnlyList<Type> GetShownMarkerTypes(CardModel? card)
    {
        if (card == null)
        {
            return Array.Empty<Type>();
        }

        List<Type>? shown = null;
        foreach (OrderedVisualEntry entry in GetOrderedVisualEntries(card))
        {
            if (entry.VisualState.MarkerType is { } markerType)
            {
                (shown ??= new List<Type>()).Add(markerType);
            }
        }

        return (IReadOnlyList<Type>?)shown ?? Array.Empty<Type>();
    }

    // Full snapshots for markers currently visible on the icon row. This intentionally
    // builds from GetOrderedVisualEntries rather than provider output so callers see the same final
    // visibility/order/style/amount resolution as the UI.
    internal static IReadOnlyList<ShownMarker> GetShownMarkerDetails(CardModel? card)
    {
        if (card == null)
        {
            return Array.Empty<ShownMarker>();
        }

        List<ShownMarker>? shown = null;
        foreach (OrderedVisualEntry entry in GetOrderedVisualEntries(card))
        {
            EnchantmentVisualState visualState = entry.VisualState;
            if (visualState.MarkerType is not { } markerType)
            {
                continue;
            }

            // ShownMarker.Icon is public API with a non-null contract; an entry whose
            // texture failed to load (SafeGetIcon) has no art to expose, so skip it here.
            if (visualState.Icon is not { } shownIcon)
            {
                continue;
            }

            (shown ??= new List<ShownMarker>()).Add(new ShownMarker(
                markerType,
                shownIcon,
                visualState.DisplayAmount,
                visualState.ShowAmount,
                visualState.Status,
                visualState.PresentationStyle,
                visualState.IsDisplayOnly,
                visualState.StoredMarker,
                visualState.IconSource));
        }

        return (IReadOnlyList<ShownMarker>?)shown ?? Array.Empty<ShownMarker>();
    }

    private static Texture2D? ResolveVisualSliceIcon(
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
            return SafeGetIcon(anchor);
        }

        if (!typeof(EnchantmentModel).IsAssignableFrom(iconType))
        {
            LogVisualSliceIconResolutionFailure(
                anchor.GetType(),
                iconType,
                $"{iconType.FullName ?? iconType.Name} is not an {nameof(EnchantmentModel)} subclass.");
            return SafeGetIcon(anchor);
        }

        EnchantmentModel? liveIconSource = snapshot.Card == null
            ? null
            : GetEnchantments(snapshot.Card).FirstOrDefault(enchantment => enchantment.GetType() == iconType);
        if (liveIconSource != null && SafeGetIcon(liveIconSource) is { } liveIcon)
        {
            return liveIcon;
        }

        if (TryCreateDefaultVisualSliceIcon(iconType, out Texture2D defaultIcon, out string? failureReason))
        {
            return defaultIcon;
        }

        LogVisualSliceIconResolutionFailure(anchor.GetType(), iconType, failureReason);
        return SafeGetIcon(anchor);
    }

    // EnchantmentModel.Icon is non-virtual and resolves through AssetCache, which throws
    // AssetLoadException when a third-party mod ships a missing/corrupt texture path. A broken
    // icon must degrade to "badge without art", never crash the visual pipeline (seen in the
    // wild at combat end via CaptureEnchantVfxSnapshot). Logged once per enchantment type.
    private static Texture2D? SafeGetIcon(EnchantmentModel model)
    {
        try
        {
            return model.Icon;
        }
        catch (Exception ex)
        {
            Type enchantmentType = model.GetType();
            if (VisualSliceIconResolutionWarnings.TryAdd((enchantmentType, enchantmentType), 0))
            {
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment] Failed to load icon texture for {enchantmentType.FullName ?? enchantmentType.Name}: " +
                    $"{ex.Message} The enchantment badge will render without art.");
            }

            return null;
        }
    }

    private static bool TryCreateDefaultVisualSliceIcon(
        Type iconType,
        out Texture2D defaultIcon,
        out string? failureReason)
    {
        try
        {
            if (ResolveCanonicalEnchantment(iconType) is not { } defaultInstance)
            {
                defaultIcon = null!;
                failureReason = "ModelDb has no canonical model for this type.";
                return false;
            }

            if (defaultInstance.Icon == null)
            {
                defaultIcon = null!;
                failureReason = "the resolved model's Icon is null.";
                return false;
            }

            defaultIcon = defaultInstance.Icon;
            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            defaultIcon = null!;
            failureReason = ex.ToString();
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
        List<OrderedVisualEntry> defaultEntries = GetDefaultOrderedVisualEntries(card);
        List<OrderedVisualEntry> displayOnlyEntries = defaultEntries
            .Where(static entry => entry.VisualState.IsDisplayOnly)
            .ToList();
        List<OrderedVisualEntry> orderedEntries;
        if (displayOnlyEntries.Count > 0)
        {
            List<OrderedVisualEntry> liveEntries = defaultEntries
                .Where(static entry => !entry.VisualState.IsDisplayOnly)
                .ToList();
            orderedEntries = OrderEntries(card, liveEntries, static entry => entry.EnchantmentId);
            orderedEntries.AddRange(displayOnlyEntries);
        }
        else
        {
            orderedEntries = OrderEntries(
                card,
                defaultEntries,
                static entry => entry.EnchantmentId);
        }

        return orderedEntries
            .Where(static entry => entry.VisualState.Status != EnchantmentStatus.Disabled ||
                                   !entry.VisualState.PresentationStyle.HideWhenDisabled)
            .Select(static (entry, index) => (entry, index))
            .OrderByDescending(static item => item.entry.VisualState.PresentationStyle.DisplayPriority)
            .ThenBy(static item => item.index)
            .Select(static item => item.entry)
            .ToList();
    }

    private static bool HasDisplayOnlyMarkerVisuals(CardModel? card)
    {
        return card != null &&
               MarkerDisplayRegistry.HasProviders &&
               GetOrderedVisualEntries(card).Any(static entry => entry.VisualState.IsDisplayOnly);
    }

    // A display-only marker's icon source is read-only here (we only sample Icon / ShowAmount /
    // Status, all constant per type), so cache the resolved model instead of looking it up on every
    // UI refresh. Caching null on failure also collapses the failure log to a single line.
    private static readonly ConcurrentDictionary<Type, EnchantmentModel?> DefaultEnchantmentCache = new();

    private static EnchantmentModel? TryResolveDefaultEnchantment(Type enchantmentType)
    {
        return DefaultEnchantmentCache.GetOrAdd(enchantmentType, static type =>
        {
            try
            {
                return ResolveCanonicalEnchantment(type);
            }
            catch (Exception ex)
            {
                LogDisplayOnlyMarkerFailure(type, ex.ToString());
                return null;
            }
        });
    }

    // EnchantmentModel instances are canonical singletons owned by ModelDb — constructing one
    // (Activator / `new`) throws DuplicateModelException ("Don't call constructors on models! Use
    // ModelDb instead."). Fetch the registered instance by type and read its icon from that.
    private static EnchantmentModel? ResolveCanonicalEnchantment(Type enchantmentType)
    {
        ModelId modelId = ModelDb.GetId(enchantmentType);
        return ModelDb.GetById<EnchantmentModel>(modelId);
    }

    private static ModelId CreateDisplayOnlyVisualId(Type enchantmentType)
    {
        return new ModelId(
            "multi_enchantment_marker",
            enchantmentType.AssemblyQualifiedName ?? enchantmentType.FullName ?? enchantmentType.Name);
    }

    private static void LogDisplayOnlyMarkerFailure(Type enchantmentType, string reason)
    {
        if (!VisualSliceIconResolutionWarnings.TryAdd((typeof(MarkerDisplay), enchantmentType), 0))
        {
            return;
        }

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment] Failed to create display-only marker for {enchantmentType.FullName ?? enchantmentType.Name}. " +
            $"Error: {reason}");
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
        if (order.Count == 0)
        {
            return defaultEntries;
        }

        // Stable partial reorder. ApplicationOrder is the gameplay replay order, but the visual
        // entry set also contains marker entries that never enter that order. Rather than fall back
        // to the default order whenever the two sets differ (which would silently drop custom
        // ordering for every card carrying a marker), emit entries in ApplicationOrder first, then
        // append any entry whose id is absent from the order in its original relative position.
        // Stale order ids with no matching entry are skipped. Nothing is dropped or duplicated, and
        // when the order matches the entry set exactly the result equals a full reorder.
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

        List<TEntry> orderedEntries = new(defaultEntries.Count);
        foreach (ModelId enchantmentId in order)
        {
            if (entriesById.TryGetValue(enchantmentId, out Queue<TEntry>? queue) && queue.Count > 0)
            {
                orderedEntries.Add(queue.Dequeue());
            }
        }

        // Entries the order did not position (markers, or anything missing from a stale order)
        // follow in their original relative sequence.
        foreach (TEntry entry in defaultEntries)
        {
            Queue<TEntry> queue = entriesById[idSelector(entry)];
            if (queue.Count > 0)
            {
                orderedEntries.Add(queue.Dequeue());
            }
        }

        return orderedEntries;
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
            if (enchantment.DynamicVars.TryGetValue("Times", out var times))
            {
                times.BaseValue = enchantment.Amount;
            }

            return;
        }

        // Third-party enchantments of the same shape, found by measurement rather than by name:
        // the registry probes which input actually drives the author's value modifier, so a var
        // called "PlayCount" or "Bonus" is handled exactly like vanilla's "Times". Without this
        // the merged total would be re-applied in full on every slice.
        if (Api.Internal.EnchantmentRegistry.TryGetAmountDrivenVar(
                enchantment.GetType(), out string varName, out decimal perApplication) &&
            enchantment.DynamicVars.TryGetValue(varName, out var driven))
        {
            driven.BaseValue = perApplication * enchantment.Amount;
        }
    }

}

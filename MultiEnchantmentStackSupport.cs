using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MultiEnchantmentMod;

internal static class MultiEnchantmentStackSupport
{
    private const string MergedStackAmountsPropertyName = "MultiEnchantmentMergedStackAmounts";

    public static EnchantmentStackDefinition GetDefinition(Type enchantmentType)
    {
        if (MultiEnchantmentStackApi.ResolveDefinitionProvider(enchantmentType) is { } provider)
        {
            return provider.GetDefinition();
        }

        // No v2 / v1 provider registered for this type. Run auto-detection — if this is a
        // non-vanilla EnchantmentModel that overrides EnchantDamage*/EnchantBlock*, it gets
        // auto-registered as MergeAmount + SharedAcrossStack so subsequent calls hit the registry
        // path above. Idempotent per type.
        Api.Internal.EnchantmentRegistry.EnsureRegistered(enchantmentType);

        if (MultiEnchantmentStackApi.ResolveDefinitionProvider(enchantmentType) is { } resolvedAfterAutoRegister)
        {
            return resolvedAfterAutoRegister.GetDefinition();
        }

        return GetBuiltInDefinition(enchantmentType);
    }

    public static EnchantmentStackBehavior GetBehavior(Type enchantmentType)
    {
        return GetDefinition(enchantmentType).Behavior;
    }

    public static EnchantmentExecutionPolicy GetExecutionPolicy(Type enchantmentType)
    {
        EnchantmentExecutionPolicy builtIn = GetBuiltInExecutionPolicy(enchantmentType);
        if (MultiEnchantmentStackApi.ResolveExecutionPolicyProvider(enchantmentType) is not { } provider)
        {
            return builtIn;
        }

        EnchantmentExecutionPolicy custom = provider.GetExecutionPolicy();
        return new EnchantmentExecutionPolicy(
            DefaultMode: custom.DefaultMode == HookExecutionMode.Default ? builtIn.DefaultMode : custom.DefaultMode,
            OnEnchant: custom.OnEnchant,
            OnPlay: custom.OnPlay,
            AfterCardPlayed: custom.AfterCardPlayed,
            AfterCardDrawn: custom.AfterCardDrawn,
            AfterPlayerTurnStart: custom.AfterPlayerTurnStart,
            BeforePlayPhaseStart: custom.BeforePlayPhaseStart,
            BeforeFlush: custom.BeforeFlush);
    }

    public static HookExecutionMode GetExecutionMode(Type enchantmentType, EnchantmentHookKind hookKind)
    {
        HookExecutionMode mode = GetExecutionPolicy(enchantmentType).GetExecutionMode(hookKind);
        return mode == HookExecutionMode.Default
            ? GetBuiltInExecutionPolicy(enchantmentType).GetExecutionMode(hookKind)
            : mode;
    }

    public static EnchantmentStackSnapshot GetSnapshot(EnchantmentModel enchantment)
    {
        CardModel? card = enchantment.Card;
        List<EnchantmentModel> liveInstances = card == null
            ? new List<EnchantmentModel> { enchantment }
            : MultiEnchantmentSupport.GetEnchantments(card)
                .Where(instance => instance.GetType() == enchantment.GetType())
                .Cast<EnchantmentModel>()
                .ToList();

        if (liveInstances.Count == 0)
        {
            liveInstances.Add(enchantment);
        }

        EnchantmentModel anchorInstance = liveInstances[0];
        EnchantmentStackDefinition definition = GetDefinition(anchorInstance.GetType());
        int[] defaultSliceAmounts = GetDefaultGameplaySliceAmounts(anchorInstance, liveInstances, definition);
        List<EnchantmentStackSlice> gameplaySlices =
            BuildSlices(anchorInstance, liveInstances, definition, defaultSliceAmounts);
        int totalAmount = Math.Max(1, defaultSliceAmounts.Sum());
        EnchantmentStackSnapshot defaultSnapshot = new(
            card,
            anchorInstance.GetType(),
            anchorInstance,
            definition,
            totalAmount,
            gameplaySlices,
            gameplaySlices,
            liveInstances);

        int[] sliceAmounts = ResolveVisualSliceAmounts(defaultSnapshot, defaultSliceAmounts);
        List<EnchantmentStackSlice> visualSlices =
            ReferenceEquals(sliceAmounts, defaultSliceAmounts)
                ? gameplaySlices
                : BuildSlices(anchorInstance, liveInstances, definition, sliceAmounts);

        // Phase 3-7: build ScopeStates view for live instances that have scope state.
        Dictionary<EnchantmentModel, Api.ScopeRuntimeStateView>? scopeStates = null;
        if (card != null)
        {
            foreach (EnchantmentModel instance in liveInstances)
            {
                if (MultiEnchantmentSupport.TryGetExistingScopeState(card, instance, out ScopeRuntimeState? state) && state != null)
                {
                    scopeStates ??= new Dictionary<EnchantmentModel, Api.ScopeRuntimeStateView>(ReferenceEqualityComparer.Instance);
                    scopeStates[instance] = new Api.ScopeRuntimeStateView(
                        state.Scope,
                        state.ActivationCount,
                        state.TurnsRemaining,
                        state.OverrideScope is not null);
                }
            }
        }

        return new EnchantmentStackSnapshot(
            card,
            anchorInstance.GetType(),
            anchorInstance,
            definition,
            totalAmount,
            gameplaySlices,
            visualSlices,
            liveInstances,
            scopeStates);
    }

    public static EnchantmentStackSnapshot CreateSingleSliceSnapshot(
        EnchantmentStackSnapshot source,
        EnchantmentStackSlice slice)
    {
        EnchantmentStackSlice[] slices = { slice };
        return new EnchantmentStackSnapshot(
            source.Card,
            source.EnchantmentType,
            source.AnchorInstance,
            source.Definition,
            Math.Max(1, slice.Amount),
            slices,
            slices,
            source.LiveInstances,
            source.ScopeStates);
    }

    public static IReadOnlyList<EnchantmentStackSnapshot> GetSnapshots(CardModel? card)
    {
        if (card == null)
        {
            return Array.Empty<EnchantmentStackSnapshot>();
        }

        return MultiEnchantmentSupport.GetEnchantments(card)
            .GroupBy(static enchantment => enchantment.GetType())
            .Select(static group => GetSnapshot(group.First()))
            .ToList();
    }

    public static bool CanApply(CardModel card, Type enchantmentType)
    {
        // Base-game source: EnchantmentModel.CanEnchant
        // sts2.dll @ min_game_version 0.105.1
        // Match vanilla's "no existing same-type enchantment" semantics. External callers
        // (FresnelLens / Kifuda-style relics that re-fire enchant logic from multiple hooks —
        // card reward + card-being-added-to-deck — and the UI's "is this card enchantable"
        // filters) treat "already has a same-type enchantment" as "not enchantable"; otherwise
        // the relic re-enchants on every hook and Amount / merged stack badges double up.
        //
        // The mod's intentional re-apply (merge) path goes through ApplyEnchantment, which
        // bypasses this gate via CanStackOnto when a merge is the intended outcome.
        return GetEnchantmentCount(card, enchantmentType) == 0;
    }

    public static bool CanStackOnto(CardModel card, Type enchantmentType)
    {
        // Internal predicate: "card already has a same-type enchantment AND the type permits
        // merging". Used by ApplyEnchantment to skip the strict CanEnchant gate above when a
        // legitimate merge is happening.
        int existing = GetEnchantmentCount(card, enchantmentType);
        if (existing == 0)
        {
            return false;
        }

        EnchantmentStackBehavior behavior = GetBehavior(enchantmentType);
        if (behavior == EnchantmentStackBehavior.DisallowDuplicate)
        {
            return false;
        }

        // v2-only MaxInstances cap: only applies to instance-multiplying behaviors. MergeAmount
        // keeps a single instance regardless of stacking, so the cap is meaningless there.
        if (behavior is EnchantmentStackBehavior.DuplicateInstance or EnchantmentStackBehavior.ExistenceStack)
        {
            int? cap = Api.Internal.EnchantmentRegistry.GetMaxInstances(enchantmentType);
            if (cap.HasValue && existing >= cap.Value)
            {
                // Phase 4-9: ReplaceOldest / ReplaceNewest let CanStackOnto succeed; the actual
                // eviction happens in ApplyEnchantment via EnforceOverflowPolicy. Reject is the
                // legacy behavior — log and fail.
                Api.StackOverflowPolicy policy = Api.Internal.EnchantmentRegistry.GetOverflowPolicy(enchantmentType);
                if (policy == Api.StackOverflowPolicy.Reject)
                {
                    LogMaxInstancesRejection(enchantmentType, existing, cap.Value);
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns the configured overflow policy for <paramref name="enchantmentType"/>. Used by
    /// the apply pipeline to decide whether to evict an existing instance before attaching a
    /// new one when the cap is hit.
    /// </summary>
    public static Api.StackOverflowPolicy GetOverflowPolicy(Type enchantmentType)
    {
        return Api.Internal.EnchantmentRegistry.GetOverflowPolicy(enchantmentType);
    }

    // Throttle MaxInstances log spam: a runaway loop could call CanStackOnto thousands of times
    // per turn. We log full detail once per (type, cap) per process, and silently reject after.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, byte> MaxInstancesLogged = new();

    private static void LogMaxInstancesRejection(Type enchantmentType, int existing, int cap)
    {
        if (!MaxInstancesLogged.TryAdd(enchantmentType, 0))
        {
            return;
        }

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment] Rejected stacking {enchantmentType.FullName}: {existing} instance(s) already on card; MaxInstances cap is {cap}. Subsequent rejections will be silent until process restart.");
    }

    public static bool PassesAdditionalCanEnchantRules(EnchantmentModel enchantment, CardModel card)
    {
        Type enchantmentType = enchantment.GetType();
        if (enchantmentType == typeof(Goopy))
        {
            return card.Tags.Contains(CardTag.Defend);
        }

        if (enchantmentType == typeof(Nimble))
        {
            return card.GainsBlock;
        }

        if (enchantmentType == typeof(Instinct))
        {
            return !card.Keywords.Contains(CardKeyword.Unplayable) &&
                   !card.EnergyCost.CostsX &&
                   card.EnergyCost.GetWithModifiers(CostModifiers.None) > 0;
        }

        if (enchantmentType == typeof(Slither))
        {
            return !card.Keywords.Contains(CardKeyword.Unplayable) &&
                   !card.EnergyCost.CostsX;
        }

        if (enchantmentType == typeof(SoulsPower))
        {
            return card.Keywords.Contains(CardKeyword.Exhaust);
        }

        if (enchantmentType == typeof(Spiral))
        {
            return card.Rarity == CardRarity.Basic &&
                   (card.Tags.Contains(CardTag.Strike) || card.Tags.Contains(CardTag.Defend));
        }

        return true;
    }

    public static int GetEnchantmentCount(CardModel? card, Type enchantmentType)
    {
        return MultiEnchantmentSupport.GetEnchantments(card).Count(enchantment => enchantment.GetType() == enchantmentType);
    }

    public static int GetTotalAmount(CardModel? card, Type enchantmentType)
    {
        return MultiEnchantmentSupport.GetEnchantments(card)
            .Where(enchantment => enchantment.GetType() == enchantmentType)
            .Sum(enchantment => enchantment.Amount);
    }

    public static int GetVisualStackCount(EnchantmentModel enchantment)
    {
        return GetBehavior(enchantment.GetType()) == EnchantmentStackBehavior.MergeAmount
            ? Math.Max(1, GetSnapshot(enchantment).VisualSlices.Count)
            : 1;
    }

    public static IEnumerable<MultiEnchantmentSupport.EnchantmentVisualState> ExpandVisualStates(CardModel? card)
    {
        return MultiEnchantmentSupport.GetOrderedVisualStates(card);
    }

    public static bool TryGetMergedStackAmounts(EnchantmentModel enchantment, out int[] stackAmounts)
    {
        EnchantmentStackSnapshot snapshot = GetSnapshot(enchantment);
        if (snapshot.Definition.Behavior != EnchantmentStackBehavior.MergeAmount)
        {
            stackAmounts = Array.Empty<int>();
            return false;
        }

        stackAmounts = snapshot.GameplaySlices.Select(static slice => slice.Amount).ToArray();
        return stackAmounts.Length > 0;
    }

    public static int GetResolvedMergedTotalAmount(EnchantmentModel enchantment)
    {
        return GetSnapshot(enchantment).TotalAmount;
    }

    public static void ClearMergedStackMetadata(EnchantmentModel enchantment)
    {
        RemoveSavedIntArray(enchantment, MergedStackAmountsPropertyName);
    }

    public static void InitializeMergedStackMetadata(EnchantmentModel enchantment)
    {
        if (GetBehavior(enchantment.GetType()) != EnchantmentStackBehavior.MergeAmount)
        {
            return;
        }

        NormalizeMergedStackMetadata(enchantment, createFallbackWhenMissing: true);
    }

    public static void AppendMergedStackAmount(EnchantmentModel enchantment, int previousTotalAmount, int addedAmount)
    {
        if (GetBehavior(enchantment.GetType()) != EnchantmentStackBehavior.MergeAmount || addedAmount <= 0)
        {
            return;
        }

        List<int> stackAmounts = new();
        if (TryGetSavedIntArray(enchantment.Props, MergedStackAmountsPropertyName, out int[] existingAmounts) &&
            AreMergedStackAmountsValid(existingAmounts, previousTotalAmount))
        {
            stackAmounts.AddRange(existingAmounts);
        }
        else if (previousTotalAmount > 0)
        {
            // Older cards may only know the merged total. Preserve that total as one legacy stack,
            // then append the newly applied amount so future badge rendering stays accurate.
            stackAmounts.Add(previousTotalAmount);
        }

        stackAmounts.Add(addedAmount);
        SetMergedStackAmounts(enchantment, stackAmounts);
    }

    public static void CloneRuntimeProps(EnchantmentModel source, EnchantmentModel clone)
    {
        clone.Props = CloneSavedProperties(source.Props);
    }

    public static void RestoreSerializedProps(SerializableEnchantment save, EnchantmentModel enchantment)
    {
        enchantment.Props = CloneSavedProperties(save.Props);
        NormalizeMergedStackMetadata(enchantment, createFallbackWhenMissing: false);
    }

    public static void WriteSerializedProps(EnchantmentModel enchantment, ref SerializableEnchantment save)
    {
        int[]? mergedStackAmounts = GetSavedIntArray(enchantment.Props, MergedStackAmountsPropertyName);
        if (mergedStackAmounts == null)
        {
            return;
        }

        if (!AreMergedStackAmountsValid(mergedStackAmounts, enchantment.Amount))
        {
            mergedStackAmounts = enchantment.Amount > 0 ? new[] { enchantment.Amount } : null;
            if (mergedStackAmounts == null)
            {
                return;
            }
        }

        save.Props = CloneSavedProperties(save.Props) ?? new SavedProperties();
        UpsertSavedIntArray(save.Props, MergedStackAmountsPropertyName, mergedStackAmounts);
    }

    public static void ApplyMergedAmountDelta(EnchantmentModel enchantment, int addedAmount)
    {
        if (addedAmount <= 0)
        {
            return;
        }

        if (MultiEnchantmentStackApi.ResolveMergedStateProvider(enchantment.GetType()) is { } provider)
        {
            provider.ApplyMergedAmountDelta(enchantment, addedAmount);
            return;
        }

        // Fallback for enchantment types that haven't registered an OnMergedDelta. The v2
        // BuiltInRegistrations covers every built-in type that needs special behavior (Instinct
        // gets its -1 energy cost via OnMergedDelta); unknown third-party types reach this
        // branch and do nothing, which matches v1 behavior for non-Instinct merge-amount types.
    }

    public static void RefreshMergedEnchantmentState(EnchantmentModel enchantment)
    {
        if (MultiEnchantmentStackApi.ResolveMergedStateProvider(enchantment.GetType()) is { } provider)
        {
            provider.RefreshMergedState(enchantment);
            return;
        }

        // Fallback when no v2 OnMergedRefresh is wired up. Built-in Glam / Spiral install the
        // `DynamicVars["Times"] = Amount` resync explicitly via BuiltInRegistrations; this path
        // is the generic "recalculate values" shape from vanilla EnchantmentModel.ModifyCard.
        enchantment.RecalculateValues();
        enchantment.Card.DynamicVars.RecalculateForUpgradeOrEnchant();
    }

    public static bool TryFormatExtraCardText(EnchantmentModel enchantment, string defaultText, out string formattedText)
    {
        formattedText = defaultText;
        if (MultiEnchantmentStackApi.ResolvePresentationProvider(enchantment.GetType()) is not { } provider)
        {
            return false;
        }

        return provider.TryFormatExtraCardText(GetSnapshot(enchantment), defaultText, out formattedText);
    }

    private static readonly ConditionalWeakTable<CardModel, HashSet<CardKeyword>> RememberedTrackedKeywords = new();

    public static void RefreshDerivedState(CardModel card)
    {
        RefreshDerivedKeywords(card);
    }

    private static void RefreshDerivedKeywords(CardModel card)
    {
        HashSet<CardKeyword> currentTrackedKeywords = GetTrackedKeywords(card).ToHashSet();
        HashSet<CardKeyword> keywordsToRefresh = currentTrackedKeywords.ToHashSet();
        if (RememberedTrackedKeywords.TryGetValue(card, out HashSet<CardKeyword>? rememberedTrackedKeywords))
        {
            keywordsToRefresh.UnionWith(rememberedTrackedKeywords);
        }

        foreach (CardKeyword keyword in keywordsToRefresh)
        {
            int baselineCount = card.CanonicalKeywords.Contains(keyword) ? 1 : 0;
            int netKeywordSources = GetKeywordSourceAmount(card, keyword);
            bool shouldHaveKeyword = baselineCount + netKeywordSources > 0;
            bool hasKeyword = card.Keywords.Contains(keyword);

            if (shouldHaveKeyword && !hasKeyword)
            {
                card.AddKeyword(keyword);
            }
            else if (!shouldHaveKeyword && hasKeyword)
            {
                card.RemoveKeyword(keyword);
            }
        }

        if (currentTrackedKeywords.Count == 0)
        {
            RememberedTrackedKeywords.Remove(card);
            return;
        }

        HashSet<CardKeyword> trackedKeywords = RememberedTrackedKeywords.GetOrCreateValue(card);
        trackedKeywords.Clear();
        trackedKeywords.UnionWith(currentTrackedKeywords);
    }

    private static IEnumerable<CardKeyword> GetTrackedKeywords(CardModel card)
    {
        HashSet<CardKeyword> trackedKeywords = new();

        foreach (EnchantmentStackSnapshot snapshot in GetSnapshots(card))
        {
            trackedKeywords.UnionWith(GetBuiltInTrackedKeywords(snapshot.EnchantmentType));
            foreach (MultiEnchantmentStackApi.IKeywordSourceProviderRegistration provider in
                     MultiEnchantmentStackApi.ResolveKeywordProviders(snapshot.EnchantmentType))
            {
                trackedKeywords.UnionWith(provider.GetTrackedKeywords());
            }
        }

        return trackedKeywords;
    }

    private static int GetKeywordSourceAmount(CardModel card, CardKeyword keyword)
    {
        int result = 0;
        foreach (EnchantmentStackSnapshot snapshot in GetSnapshots(card))
        {
            result += GetBuiltInKeywordSourceAmount(snapshot, keyword);
            foreach (MultiEnchantmentStackApi.IKeywordSourceProviderRegistration provider in
                     MultiEnchantmentStackApi.ResolveKeywordProviders(snapshot.EnchantmentType))
            {
                result += provider.GetKeywordSourceAmount(snapshot, keyword);
            }
        }

        return result;
    }

    private static int GetBuiltInKeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword)
    {
        // All built-in keyword sources (Goopy/SoulsPower → Exhaust, Steady/RoyallyApproved →
        // Retain, RoyallyApproved → Innate, TezcatarasEmber → Eternal) now flow through v2
        // BuiltInRegistrations.TrackKeyword(...) instead. Fallback returns 0 so unknown
        // third-party types not yet ported don't contribute spurious keyword amounts.
        _ = snapshot;
        _ = keyword;
        return 0;
    }

    private static IEnumerable<CardKeyword> GetBuiltInTrackedKeywords(Type enchantmentType)
    {
        // Same shape as GetBuiltInKeywordSourceAmount: every built-in tracked keyword now goes
        // through v2 BuiltInRegistrations. Returning an empty set lets the v2 provider be the
        // single source of truth (HashSet.UnionWith in GetTrackedKeywords still dedupes
        // gracefully, but eliminating the duplicate prevents double-counting from any v2 source
        // that ports a built-in type with custom semantics).
        _ = enchantmentType;
        return Array.Empty<CardKeyword>();
    }

    private static EnchantmentStackDefinition GetBuiltInDefinition(Type enchantmentType)
    {
        // Every built-in MegaCrit enchantment is now registered via the v2 path in
        // Api.Internal.BuiltInRegistrations.RegisterAll(), so this fallback only runs for
        // third-party enchantment types whose author hasn't supplied a v2 registration
        // (attribute, EnchantmentDefinition<T>, or fluent builder). Refuse duplicates by
        // default; mods that want stacking opt in explicitly.
        _ = enchantmentType;
        return new EnchantmentStackDefinition(
            EnchantmentStackBehavior.DisallowDuplicate,
            EnchantmentStatusAggregation.PresenceOnly);
    }

    private static EnchantmentExecutionPolicy GetBuiltInExecutionPolicy(Type enchantmentType)
    {
        return GetBehavior(enchantmentType) switch
        {
            EnchantmentStackBehavior.MergeAmount => new EnchantmentExecutionPolicy(DefaultMode: HookExecutionMode.MergedTotal),
            EnchantmentStackBehavior.DuplicateInstance => new EnchantmentExecutionPolicy(DefaultMode: HookExecutionMode.PerLiveInstance),
            EnchantmentStackBehavior.ExistenceStack => new EnchantmentExecutionPolicy(DefaultMode: HookExecutionMode.FirstActiveInstanceOnly),
            _ => new EnchantmentExecutionPolicy(DefaultMode: HookExecutionMode.FirstActiveInstanceOnly),
        };
    }

    private static int[] GetDefaultGameplaySliceAmounts(
        EnchantmentModel anchor,
        IReadOnlyList<EnchantmentModel> liveInstances,
        EnchantmentStackDefinition definition)
    {
        if (definition.Behavior == EnchantmentStackBehavior.MergeAmount)
        {
            return liveInstances
                .SelectMany(static instance => GetRawMergedStackAmounts(instance))
                .DefaultIfEmpty(Math.Max(1, anchor.Amount))
                .ToArray();
        }

        return liveInstances
            .Select(static enchantment => Math.Max(1, enchantment.Amount))
            .DefaultIfEmpty(1)
            .ToArray();
    }

    private static int[] ResolveVisualSliceAmounts(
        EnchantmentStackSnapshot defaultSnapshot,
        int[] defaultSliceAmounts)
    {
        if (MultiEnchantmentStackApi.ResolvePresentationProvider(defaultSnapshot.EnchantmentType) is not { } provider)
        {
            return defaultSliceAmounts;
        }

        IReadOnlyList<int>? customSliceAmounts = provider.GetVisualSliceAmounts(defaultSnapshot);
        if (customSliceAmounts == null ||
            customSliceAmounts.Count == 0 ||
            customSliceAmounts.Any(static amount => amount <= 0) ||
            customSliceAmounts.Sum() != defaultSnapshot.TotalAmount)
        {
            return defaultSliceAmounts;
        }

        return customSliceAmounts.ToArray();
    }

    private static List<EnchantmentStackSlice> BuildSlices(
        EnchantmentModel anchor,
        IReadOnlyList<EnchantmentModel> liveInstances,
        EnchantmentStackDefinition definition,
        IReadOnlyList<int> sliceAmounts)
    {
        List<EnchantmentStackSlice> slices = new(sliceAmounts.Count);
        if (definition.StatusAggregation == EnchantmentStatusAggregation.PerInstance &&
            definition.Behavior != EnchantmentStackBehavior.MergeAmount &&
            liveInstances.Count == sliceAmounts.Count)
        {
            for (int i = 0; i < sliceAmounts.Count; i++)
            {
                slices.Add(new EnchantmentStackSlice(
                    sliceAmounts[i],
                    liveInstances[i].Status,
                    i));
            }

            return slices;
        }

        EnchantmentStatus sharedStatus = ResolveSharedStatus(anchor, liveInstances, definition.StatusAggregation);
        for (int i = 0; i < sliceAmounts.Count; i++)
        {
            slices.Add(new EnchantmentStackSlice(
                sliceAmounts[i],
                sharedStatus,
                i));
        }

        return slices;
    }

    private static EnchantmentStatus ResolveSharedStatus(
        EnchantmentModel anchor,
        IReadOnlyList<EnchantmentModel> liveInstances,
        EnchantmentStatusAggregation aggregation)
    {
        return aggregation switch
        {
            EnchantmentStatusAggregation.PresenceOnly => liveInstances.Any(static instance => instance.Status != EnchantmentStatus.Disabled)
                ? EnchantmentStatus.Normal
                : EnchantmentStatus.Disabled,
            EnchantmentStatusAggregation.None => anchor.Status,
            _ => liveInstances.FirstOrDefault()?.Status ?? anchor.Status,
        };
    }

    private static void NormalizeMergedStackMetadata(EnchantmentModel enchantment, bool createFallbackWhenMissing)
    {
        if (GetBehavior(enchantment.GetType()) != EnchantmentStackBehavior.MergeAmount)
        {
            return;
        }

        int[]? stackAmounts = GetSavedIntArray(enchantment.Props, MergedStackAmountsPropertyName);
        if (stackAmounts == null)
        {
            if (createFallbackWhenMissing && enchantment.Amount > 0)
            {
                SetMergedStackAmounts(enchantment, new[] { enchantment.Amount });
            }

            return;
        }

        if (!AreMergedStackAmountsValid(stackAmounts, enchantment.Amount))
        {
            if (enchantment.Amount > 0)
            {
                SetMergedStackAmounts(enchantment, new[] { enchantment.Amount });
            }
            else
            {
                RemoveSavedIntArray(enchantment, MergedStackAmountsPropertyName);
            }
        }
    }

    private static bool TryGetValidMergedStackAmounts(EnchantmentModel enchantment, out int[] stackAmounts)
    {
        stackAmounts = Array.Empty<int>();
        return TryGetSavedIntArray(enchantment.Props, MergedStackAmountsPropertyName, out stackAmounts) &&
               AreMergedStackAmountsValid(stackAmounts, enchantment.Amount);
    }

    private static IEnumerable<int> GetRawMergedStackAmounts(EnchantmentModel enchantment)
    {
        return TryGetValidMergedStackAmounts(enchantment, out int[] stackAmounts)
            ? stackAmounts
            : new[] { Math.Max(1, enchantment.Amount) };
    }

    private static bool AreMergedStackAmountsValid(IReadOnlyCollection<int> stackAmounts, int expectedTotalAmount)
    {
        return stackAmounts.Count > 0 &&
               stackAmounts.All(static amount => amount > 0) &&
               stackAmounts.Sum() == expectedTotalAmount;
    }

    private static SavedProperties? CloneSavedProperties(SavedProperties? source)
    {
        if (source == null)
        {
            return null;
        }

        SavedProperties clone = new()
        {
            ints = CloneSavedPropertyList(source.ints),
            bools = CloneSavedPropertyList(source.bools),
            strings = CloneSavedPropertyList(source.strings),
            intArrays = CloneSavedIntArrayList(source.intArrays),
            modelIds = CloneSavedPropertyList(source.modelIds),
            cards = CloneSavedPropertyList(source.cards),
            cardArrays = CloneSavedCardArrayList(source.cardArrays),
        };

        return HasAnySavedProperties(clone) ? clone : null;
    }

    private static List<SavedProperties.SavedProperty<T>>? CloneSavedPropertyList<T>(
        List<SavedProperties.SavedProperty<T>>? source)
    {
        return source?.ToList();
    }

    private static List<SavedProperties.SavedProperty<int[]>>? CloneSavedIntArrayList(
        List<SavedProperties.SavedProperty<int[]>>? source)
    {
        return source?.Select(static property =>
            new SavedProperties.SavedProperty<int[]>(property.name, (int[])property.value.Clone())).ToList();
    }

    private static List<SavedProperties.SavedProperty<SerializableCard[]>>? CloneSavedCardArrayList(
        List<SavedProperties.SavedProperty<SerializableCard[]>>? source)
    {
        return source?.Select(static property =>
            new SavedProperties.SavedProperty<SerializableCard[]>(property.name, property.value.ToArray())).ToList();
    }

    private static bool HasAnySavedProperties(SavedProperties properties)
    {
        return HasValues(properties.ints) ||
               HasValues(properties.bools) ||
               HasValues(properties.strings) ||
               HasValues(properties.intArrays) ||
               HasValues(properties.modelIds) ||
               HasValues(properties.cards) ||
               HasValues(properties.cardArrays);
    }

    private static bool HasValues<T>(IReadOnlyCollection<T>? values)
    {
        return values != null && values.Count > 0;
    }

    private static int[]? GetSavedIntArray(SavedProperties? properties, string propertyName)
    {
        return TryGetSavedIntArray(properties, propertyName, out int[] values) ? values : null;
    }

    private static bool TryGetSavedIntArray(SavedProperties? properties, string propertyName, out int[] values)
    {
        values = Array.Empty<int>();
        if (properties?.intArrays == null)
        {
            return false;
        }

        foreach (SavedProperties.SavedProperty<int[]> property in properties.intArrays)
        {
            if (property.name != propertyName)
            {
                continue;
            }

            values = (int[])property.value.Clone();
            return true;
        }

        return false;
    }

    private static void SetMergedStackAmounts(EnchantmentModel enchantment, IReadOnlyCollection<int> stackAmounts)
    {
        if (stackAmounts.Count == 0)
        {
            RemoveSavedIntArray(enchantment, MergedStackAmountsPropertyName);
            return;
        }

        SavedProperties props = enchantment.Props ??= new SavedProperties();
        UpsertSavedIntArray(props, MergedStackAmountsPropertyName, stackAmounts.ToArray());
    }

    private static void UpsertSavedIntArray(SavedProperties properties, string propertyName, int[] values)
    {
        properties.intArrays ??= new List<SavedProperties.SavedProperty<int[]>>();

        SavedProperties.SavedProperty<int[]> property = new(propertyName, (int[])values.Clone());
        int existingIndex = properties.intArrays.FindIndex(existing => existing.name == propertyName);
        if (existingIndex >= 0)
        {
            properties.intArrays[existingIndex] = property;
        }
        else
        {
            properties.intArrays.Add(property);
        }
    }

    private static void RemoveSavedIntArray(EnchantmentModel enchantment, string propertyName)
    {
        SavedProperties? props = enchantment.Props;
        if (props?.intArrays == null)
        {
            return;
        }

        props.intArrays.RemoveAll(property => property.name == propertyName);
        if (!HasAnySavedProperties(props))
        {
            enchantment.Props = null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Saves.Runs;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

internal static class MultiEnchantmentStackSupport
{
    private const string MergedStackAmountsPropertyName = "MultiEnchantmentMergedStackAmounts";

    public static EnchantmentStackDefinition GetDefinition(Type enchantmentType)
    {
        if (EnchantmentRegistry.GetDefinitionEntry(enchantmentType, static entry => entry.Definition != null) is { } entry)
        {
            return entry.GetDefinition();
        }

        // No v2 registry entry registered for this type. Run auto-detection — if this is a
        // non-vanilla EnchantmentModel that overrides EnchantDamage*/EnchantBlock*, it gets
        // auto-registered as MergeAmount + SharedAcrossStack so subsequent calls hit the registry
        // path above. Idempotent per type.
        Api.Internal.EnchantmentRegistry.EnsureRegistered(enchantmentType);

        if (EnchantmentRegistry.GetDefinitionEntry(enchantmentType, static entry => entry.Definition != null) is { } resolvedAfterAutoRegister)
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
        if (EnchantmentRegistry.GetDefinitionEntry(enchantmentType, static entry => entry.ExecutionPolicy != null) is not { } entry)
        {
            return builtIn;
        }

        EnchantmentExecutionPolicy custom = entry.GetExecutionPolicy();
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
            : MultiEnchantmentSupport.GetGameplayEnchantments(card)
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

        List<EnchantmentStackSlice> visualSlices =
            ResolveVisualSlices(defaultSnapshot, anchorInstance, liveInstances, definition, defaultSliceAmounts, gameplaySlices);

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

        return MultiEnchantmentSupport.GetGameplayEnchantments(card)
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

    internal static void LogMaxInstancesRejection(Type enchantmentType, int existing, int cap)
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

        return PassesCardEnchantmentLimit(enchantment, card);
    }

    /// <summary>
    /// Enforces the per-card enchantment cap registered through
    /// <see cref="Api.MultiEnchantmentApi.SetCardEnchantmentLimit(ModelId, int?)"/> and friends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives inside <see cref="PassesAdditionalCanEnchantRules"/> on purpose: that runs from the
    /// vanilla <c>CanEnchant</c> postfix, so a capped card is filtered out of the enchant UI up front
    /// instead of blowing up with <c>InvalidOperationException</c> halfway through an application.
    /// It is therefore also bypassed by <see cref="Api.MultiEnchantmentApi.IgnoreCanEnchant"/> —
    /// forcing ignores every veto, this one included.
    /// </para>
    /// <para>
    /// The cap counts <b>slots</b> (distinct gameplay enchantments), not stacked amount. An
    /// application that merges into an existing instance of the same type consumes no new slot and is
    /// always allowed, so "one enchantment only" still permits Sharp 1 → Sharp 5. Non-gameplay
    /// markers never count.
    /// </para>
    /// </remarks>
    internal static bool PassesCardEnchantmentLimit(EnchantmentModel enchantment, CardModel card)
    {
        if (CardEnchantmentLimits.IsEmpty)
        {
            return true;
        }

        int? cap = CardEnchantmentLimits.Resolve(card);
        if (cap is null)
        {
            return true;
        }

        Type enchantmentType = enchantment.GetType();
        bool mergesIntoExisting =
            GetBehavior(enchantmentType) == EnchantmentStackBehavior.MergeAmount &&
            MultiEnchantmentSupport.GetEnchantment(card, enchantmentType) != null;
        if (mergesIntoExisting)
        {
            return true;
        }

        return MultiEnchantmentSupport.GetGameplayEnchantments(card).Count() < cap.Value;
    }

    public static bool PassesCanEnchantRulesIgnoringDuplicate(EnchantmentModel enchantment, CardModel card)
    {
        CardType type = card.Type;
        if (type is CardType.Status or CardType.Curse or CardType.Quest) return false;
        if (!enchantment.CanEnchantCardType(type)) return false;
        CardPile? pile = card.Pile;
        if (pile != null && pile.Type == PileType.Deck && card.Keywords.Contains(CardKeyword.Unplayable)) return false;
        return PassesAdditionalCanEnchantRules(enchantment, card);
    }

    /// <summary>
    /// Merge-path CanEnchant gate. Every merge passes the vanilla base clauses (with the
    /// duplicate check excluded — the merge IS the deliberate duplicate); types the IsStackable
    /// heuristic swept in whose author also overrides <c>CanEnchant</c> additionally re-run that
    /// override, because vanilla <c>CardCmd.Enchant</c> re-evaluates it on every same-type stack
    /// and authors encode stacking caps there (all five vanilla stackables do). The probe runs
    /// with the mod's CanEnchant postfix suspended, so it returns pure vanilla semantics — which
    /// also means a type living only in the extra storage under a different primary refuses the
    /// merge (vanilla's primary-slot clause), the conservative reading of a contract the author
    /// wrote for single-slot vanilla.
    /// </summary>
    public static bool PassesMergeCanEnchantRules(EnchantmentModel enchantment, CardModel card)
    {
        if (!PassesCanEnchantRulesIgnoringDuplicate(enchantment, card))
        {
            return false;
        }

        if (!EnchantmentRegistry.RequiresAuthorCanEnchantGateOnMerge(enchantment.GetType()))
        {
            return true;
        }

        return MultiEnchantmentPatches.ProbeVanillaCanEnchant(enchantment, card);
    }

    public static int GetEnchantmentCount(CardModel? card, Type enchantmentType)
    {
        return MultiEnchantmentSupport.GetEnchantmentsForType(card, enchantmentType)
            .Count(enchantment => enchantment.GetType() == enchantmentType);
    }

    public static int GetTotalAmount(CardModel? card, Type enchantmentType)
    {
        return MultiEnchantmentSupport.GetEnchantmentsForType(card, enchantmentType)
            .Where(enchantment => enchantment.GetType() == enchantmentType)
            .Sum(enchantment => enchantment.Amount);
    }

    public static int GetVisualStackCount(EnchantmentModel enchantment)
    {
        return Math.Max(1, GetSnapshot(enchantment).VisualSlices.Count);
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

    /// <summary>
    /// True when this instance still carries MultiEnchantment's own merged-stack metadata
    /// (<see cref="MergedStackAmountsPropertyName"/>). That property is only ever written while a
    /// type is classified <see cref="EnchantmentStackBehavior.MergeAmount"/>, so its presence is a
    /// reliable signal that MultiEnchantment merged this instance itself. A genuine third-party
    /// <see cref="EnchantmentStackBehavior.DisallowDuplicate"/> enchantment that uses <c>Amount</c>
    /// as its own value never has it — letting <c>NormalizeCardEnchantmentStacks</c> tell "our stale
    /// merge artifact" apart from "the author's intended Amount".
    /// </summary>
    public static bool HasMergedStackMetadata(EnchantmentModel enchantment)
    {
        return TryGetSavedIntArray(enchantment.Props, MergedStackAmountsPropertyName, out _);
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

    public static void RemoveMergedStackAmount(EnchantmentModel enchantment, int amountToRemove)
    {
        if (GetBehavior(enchantment.GetType()) != EnchantmentStackBehavior.MergeAmount || amountToRemove <= 0)
        {
            return;
        }

        List<int> stackAmounts = GetRawMergedStackAmounts(enchantment).ToList();
        for (int i = stackAmounts.Count - 1; i >= 0 && amountToRemove > 0; i--)
        {
            int removedFromSlice = Math.Min(stackAmounts[i], amountToRemove);
            stackAmounts[i] -= removedFromSlice;
            amountToRemove -= removedFromSlice;
            if (stackAmounts[i] <= 0)
            {
                stackAmounts.RemoveAt(i);
            }
        }

        if (stackAmounts.Count == 0)
        {
            RemoveSavedIntArray(enchantment, MergedStackAmountsPropertyName);
        }
        else
        {
            SetMergedStackAmounts(enchantment, stackAmounts);
        }
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

        if (EnchantmentRegistry.GetDefinitionEntry(enchantment.GetType(), static entry => entry.OnMergedDelta != null) is { } entry)
        {
            entry.ApplyMergedAmountDelta(enchantment, addedAmount);
            return;
        }

        // Fallback for enchantment types that haven't registered an OnMergedDelta. The v2
        // BuiltInRegistrations covers every built-in type that needs special behavior; unknown
        // third-party types reach this branch and do nothing, which matches v1 behavior for
        // non-special merge-amount types.
    }

    public static void RefreshMergedEnchantmentState(EnchantmentModel enchantment)
    {
        if (EnchantmentRegistry.GetDefinitionEntry(
                enchantment.GetType(),
                static entry => entry.OnMergedRefresh != null || entry.OnMergedDelta != null) is { } entry)
        {
            entry.RefreshMergedState(enchantment);
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
        if (EnchantmentRegistry.GetLastContributionEntry(
                enchantment.GetType(),
                static entry => entry.FormatExtraText != null) is not { } entry)
        {
            return false;
        }

        return entry.TryFormatExtraCardText(GetSnapshot(enchantment), defaultText, out formattedText);
    }

    private static readonly ConditionalWeakTable<CardModel, HashSet<CardKeyword>> RememberedTrackedKeywords = new();
    private static readonly ConditionalWeakTable<CardModel, HashSet<CardKeyword>> ModAddedKeywords = new();
    private static readonly ConditionalWeakTable<CardModel, HashSet<CardKeyword>> ModRemovedKeywords = new();

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

        HashSet<CardKeyword>? modAddedKeywords = ModAddedKeywords.TryGetValue(card, out HashSet<CardKeyword>? existingModAddedKeywords)
            ? existingModAddedKeywords
            : null;
        HashSet<CardKeyword>? modRemovedKeywords = ModRemovedKeywords.TryGetValue(card, out HashSet<CardKeyword>? existingModRemovedKeywords)
            ? existingModRemovedKeywords
            : null;

        foreach (CardKeyword keyword in keywordsToRefresh)
        {
            int baselineCount = card.CanonicalKeywords.Contains(keyword) ? 1 : 0;
            int netKeywordSources = GetKeywordSourceAmount(card, keyword);
            bool shouldHaveKeyword = baselineCount + netKeywordSources > 0;
            bool hasKeyword = card.Keywords.Contains(keyword);
            bool weAddedKeyword = modAddedKeywords?.Contains(keyword) ?? false;
            bool weRemovedKeyword = modRemovedKeywords?.Contains(keyword) ?? false;

            if (shouldHaveKeyword)
            {
                if (!hasKeyword)
                {
                    card.AddKeyword(keyword);
                    if (baselineCount == 0)
                    {
                        (modAddedKeywords ??= ModAddedKeywords.GetOrCreateValue(card)).Add(keyword);
                    }
                }

                if (weRemovedKeyword)
                {
                    modRemovedKeywords!.Remove(keyword);
                }
            }
            else
            {
                if (hasKeyword)
                {
                    card.RemoveKeyword(keyword);
                    if (baselineCount > 0)
                    {
                        (modRemovedKeywords ??= ModRemovedKeywords.GetOrCreateValue(card)).Add(keyword);
                    }
                }

                if (weAddedKeyword)
                {
                    modAddedKeywords!.Remove(keyword);
                }

                if (weRemovedKeyword && baselineCount == 0)
                {
                    modRemovedKeywords!.Remove(keyword);
                }
            }
        }

        if (currentTrackedKeywords.Count == 0)
        {
            RememberedTrackedKeywords.Remove(card);
            if (modAddedKeywords is { Count: 0 })
            {
                ModAddedKeywords.Remove(card);
            }

            if (modRemovedKeywords is { Count: 0 })
            {
                ModRemovedKeywords.Remove(card);
            }

            return;
        }

        HashSet<CardKeyword> trackedKeywords = RememberedTrackedKeywords.GetOrCreateValue(card);
        trackedKeywords.Clear();
        trackedKeywords.UnionWith(currentTrackedKeywords);

        if (modAddedKeywords is { Count: 0 })
        {
            ModAddedKeywords.Remove(card);
        }

        if (modRemovedKeywords is { Count: 0 })
        {
            ModRemovedKeywords.Remove(card);
        }
    }

    private static IEnumerable<CardKeyword> GetTrackedKeywords(CardModel card)
    {
        HashSet<CardKeyword> trackedKeywords = new();

        foreach (EnchantmentStackSnapshot snapshot in GetSnapshots(card))
        {
            trackedKeywords.UnionWith(GetBuiltInTrackedKeywords(snapshot.EnchantmentType));
            foreach (EnchantmentEntry entry in EnchantmentRegistry.GetEntries(snapshot.EnchantmentType))
            {
                trackedKeywords.UnionWith(entry.GetTrackedKeywords());
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
            foreach (EnchantmentEntry entry in EnchantmentRegistry.GetEntries(snapshot.EnchantmentType))
            {
                result += entry.GetKeywordSourceAmount(snapshot, keyword);
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
        // through v2 BuiltInRegistrations. Returning an empty set lets the v2 registry be the
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
        // Resolve behavior first: GetBehavior runs EnsureRegistered, which is what records the
        // type in the auto-registration set we check next. Doing it in this order keeps the
        // execution mode stable across calls — and independent of whether the registry was already
        // sealed when the type was first seen.
        EnchantmentStackBehavior behavior = GetBehavior(enchantmentType);

        // Unregistered third-party enchantments (auto-detected or fully defaulted) fire every hook
        // PER LIVE INSTANCE — exactly as often as vanilla would for that instance — instead of
        // MergedTotal times. We never saw the author's intent, so we cannot assume a hook is safe
        // to replay ActiveTotalAmount times: an OnPlay already scaled by Amount would otherwise
        // apply Amount² (the Momentum/Adroit quadratic trap, which the explicit built-in
        // registrations special-case but auto-registration cannot). Explicitly registered types
        // keep their behavior-derived default below.
        if (EnchantmentRegistry.WasAutoRegistered(enchantmentType))
        {
            return new EnchantmentExecutionPolicy(DefaultMode: HookExecutionMode.PerLiveInstance);
        }

        return behavior switch
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

    public static IReadOnlyList<EnchantmentVisualSlice>? GetValidCustomVisualSlices(
        EnchantmentStackSnapshot snapshot,
        EnchantmentModel anchor)
    {
        if (EnchantmentRegistry.GetLastContributionEntry(
                snapshot.EnchantmentType,
                static entry => entry.GetVisualSlices != null) is not { } entry)
        {
            return null;
        }

        IReadOnlyList<EnchantmentVisualSlice>? customSlices = entry.GetSafeVisualSlices(snapshot);
        return customSlices != null && AreVisualSlicesValid(customSlices, snapshot, anchor.ShowAmount)
            ? customSlices
            : null;
    }

    private static List<EnchantmentStackSlice> ResolveVisualSlices(
        EnchantmentStackSnapshot defaultSnapshot,
        EnchantmentModel anchor,
        IReadOnlyList<EnchantmentModel> liveInstances,
        EnchantmentStackDefinition definition,
        int[] defaultSliceAmounts,
        List<EnchantmentStackSlice> defaultSlices)
    {
        if (EnchantmentRegistry.GetLastContributionEntry(
                defaultSnapshot.EnchantmentType,
                static entry => entry.GetVisualSlices != null || entry.GetVisualSliceAmounts != null) is not { } entry)
        {
            return defaultSlices;
        }

        IReadOnlyList<EnchantmentVisualSlice>? customSlices = GetValidCustomVisualSlices(defaultSnapshot, anchor);
        if (customSlices != null)
        {
            return customSlices
                .Select(static (slice, index) => new EnchantmentStackSlice(slice.Amount, slice.Status, index))
                .ToList();
        }

        IReadOnlyList<int>? customSliceAmounts = entry.GetSafeVisualSliceAmounts(defaultSnapshot);
        if (customSliceAmounts == null ||
            !AreVisualSliceAmountsValid(customSliceAmounts, defaultSnapshot, anchor.ShowAmount))
        {
            return defaultSlices;
        }

        int[] sliceAmounts = customSliceAmounts.ToArray();
        return BuildSlices(anchor, liveInstances, definition, sliceAmounts);
    }

    private static bool AreVisualSlicesValid(
        IReadOnlyList<EnchantmentVisualSlice> slices,
        EnchantmentStackSnapshot snapshot,
        bool showAmount)
    {
        return slices.Count > 0 &&
               slices.All(static slice => slice.Amount > 0) &&
               (!showAmount || slices.Sum(static slice => slice.Amount) == snapshot.TotalAmount);
    }

    private static bool AreVisualSliceAmountsValid(
        IReadOnlyList<int> amounts,
        EnchantmentStackSnapshot snapshot,
        bool showAmount)
    {
        return amounts.Count > 0 &&
               amounts.All(static amount => amount > 0) &&
               (!showAmount || amounts.Sum() == snapshot.TotalAmount);
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
            cards = CloneSavedCardList(source.cards),
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

    private static List<SavedProperties.SavedProperty<SerializableCard>>? CloneSavedCardList(
        List<SavedProperties.SavedProperty<SerializableCard>>? source)
    {
        return source?.Select(static property =>
            new SavedProperties.SavedProperty<SerializableCard>(property.name, CloneSerializableCard(property.value))).ToList();
    }

    private static List<SavedProperties.SavedProperty<SerializableCard[]>>? CloneSavedCardArrayList(
        List<SavedProperties.SavedProperty<SerializableCard[]>>? source)
    {
        return source?.Select(static property =>
            new SavedProperties.SavedProperty<SerializableCard[]>(
                property.name,
                property.value.Select(CloneSerializableCard).ToArray())).ToList();
    }

    private static SerializableCard CloneSerializableCard(SerializableCard source)
    {
        return new SerializableCard
        {
            Id = source.Id,
            CurrentUpgradeLevel = source.CurrentUpgradeLevel,
            Enchantment = CloneSerializableEnchantment(source.Enchantment),
            Props = CloneSavedProperties(source.Props),
            FloorAddedToDeck = source.FloorAddedToDeck,
        };
    }

    private static SerializableEnchantment? CloneSerializableEnchantment(SerializableEnchantment? source)
    {
        if (source == null)
        {
            return null;
        }

        return new SerializableEnchantment
        {
            Id = source.Id,
            Amount = source.Amount,
            Props = CloneSavedProperties(source.Props),
        };
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
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

        return new EnchantmentStackSnapshot(
            card,
            anchorInstance.GetType(),
            anchorInstance,
            definition,
            totalAmount,
            gameplaySlices,
            visualSlices,
            liveInstances);
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
        return GetEnchantmentCount(card, enchantmentType) == 0 ||
               GetBehavior(enchantmentType) != EnchantmentStackBehavior.DisallowDuplicate;
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

        // Base-game source: EnchantmentModel.ModifyCard triggers both OnEnchant() and
        // RecalculateValues(). For merged stacks we must not blindly replay OnEnchant() for every
        // enchantment type, because several stackable enchantments are represented as one merged
        // instance whose ongoing state is derived from Amount or from custom mod-side refresh
        // logic. Apply only the stack-specific delta side effects that are actually required.
        if (enchantment is Instinct instinct)
        {
            for (int i = 0; i < addedAmount; i++)
            {
                instinct.Card.EnergyCost.UpgradeBy(-1);
            }
        }
    }

    public static void RefreshMergedEnchantmentState(EnchantmentModel enchantment)
    {
        if (MultiEnchantmentStackApi.ResolveMergedStateProvider(enchantment.GetType()) is { } provider)
        {
            provider.RefreshMergedState(enchantment);
            return;
        }

        // Base-game source: EnchantmentModel.ModifyCard.
        // Keep the recalculation half of ModifyCard without re-running generic OnEnchant().
        enchantment.RecalculateValues();
        if (enchantment is Glam or Spiral)
        {
            enchantment.DynamicVars["Times"].BaseValue = enchantment.Amount;
        }

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
        return (snapshot.EnchantmentType, keyword) switch
        {
            ({ } type, CardKeyword.Exhaust) when type == typeof(Goopy) => snapshot.ActiveInstanceCount,
            ({ } type, CardKeyword.Exhaust) when type == typeof(SoulsPower) => -snapshot.ActiveTotalAmount,
            ({ } type, CardKeyword.Retain) when type == typeof(Steady) => snapshot.ActiveInstanceCount > 0 ? 1 : 0,
            ({ } type, CardKeyword.Retain) when type == typeof(RoyallyApproved) => snapshot.ActiveInstanceCount > 0 ? 1 : 0,
            ({ } type, CardKeyword.Innate) when type == typeof(RoyallyApproved) => snapshot.ActiveInstanceCount > 0 ? 1 : 0,
            ({ } type, CardKeyword.Eternal) when type == typeof(TezcatarasEmber) => snapshot.ActiveInstanceCount > 0 ? 1 : 0,
            _ => 0,
        };
    }

    private static IEnumerable<CardKeyword> GetBuiltInTrackedKeywords(Type enchantmentType)
    {
        if (enchantmentType == typeof(Goopy) || enchantmentType == typeof(SoulsPower))
        {
            return new[] { CardKeyword.Exhaust };
        }

        if (enchantmentType == typeof(Steady))
        {
            return new[] { CardKeyword.Retain };
        }

        if (enchantmentType == typeof(RoyallyApproved))
        {
            return new[] { CardKeyword.Innate, CardKeyword.Retain };
        }

        if (enchantmentType == typeof(TezcatarasEmber))
        {
            return new[] { CardKeyword.Eternal };
        }

        return Array.Empty<CardKeyword>();
    }

    private static EnchantmentStackDefinition GetBuiltInDefinition(Type enchantmentType)
    {
        // Mod source: explicit duplicate-enchantment policy for multi-enchantment support.
        // The rule is "merge gameplay state only when the merged representation is semantically
        // correct, then expand UI badges from the merged amount".
        if (enchantmentType == typeof(Adroit) ||
            enchantmentType == typeof(Clone) ||
            enchantmentType == typeof(Favored) ||
            enchantmentType == typeof(Glam) ||
            enchantmentType == typeof(Imbued) ||
            enchantmentType == typeof(Instinct) ||
            enchantmentType == typeof(Momentum) ||
            enchantmentType == typeof(Nimble) ||
            enchantmentType == typeof(Sharp) ||
            enchantmentType == typeof(Slither) ||
            enchantmentType == typeof(SlumberingEssence) ||
            enchantmentType == typeof(SoulsPower) ||
            enchantmentType == typeof(Sown) ||
            enchantmentType == typeof(Spiral) ||
            enchantmentType == typeof(Swift) ||
            enchantmentType == typeof(Vigorous))
        {
            return new EnchantmentStackDefinition(
                EnchantmentStackBehavior.MergeAmount,
                EnchantmentStatusAggregation.Shared);
        }

        if (enchantmentType == typeof(Goopy))
        {
            // Goopy's Amount is live gameplay state that grows after each play. Keeping one Goopy
            // instance per stack preserves correct Exhaust netting and permanent block growth.
            return new EnchantmentStackDefinition(
                EnchantmentStackBehavior.DuplicateInstance,
                EnchantmentStatusAggregation.PerInstance);
        }

        if (enchantmentType == typeof(PerfectFit) ||
            enchantmentType == typeof(RoyallyApproved) ||
            enchantmentType == typeof(Steady) ||
            enchantmentType == typeof(TezcatarasEmber))
        {
            return new EnchantmentStackDefinition(
                EnchantmentStackBehavior.ExistenceStack,
                EnchantmentStatusAggregation.PresenceOnly);
        }

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
            EnchantmentStatusAggregation.None => EnchantmentStatus.Normal,
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

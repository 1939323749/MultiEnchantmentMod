using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MultiEnchantmentMod;

internal static class MultiEnchantmentStackSupport
{
    private const string MergedStackAmountsPropertyName = "MultiEnchantmentMergedStackAmounts";

    public static EnchantmentStackBehavior GetBehavior(Type enchantmentType)
    {
        if (MultiEnchantmentStackApi.ResolveProvider(enchantmentType) is IEnchantmentStackBehaviorProvider provider)
        {
            return provider.GetBehavior(enchantmentType);
        }

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
            return EnchantmentStackBehavior.MergeAmount;
        }

        if (enchantmentType == typeof(Goopy))
        {
            // Goopy's Amount is live gameplay state that grows after each play. Keeping one Goopy
            // instance per stack preserves correct Exhaust netting and permanent block growth.
            return EnchantmentStackBehavior.DuplicateInstance;
        }

        if (enchantmentType == typeof(PerfectFit) ||
            enchantmentType == typeof(RoyallyApproved) ||
            enchantmentType == typeof(Steady) ||
            enchantmentType == typeof(TezcatarasEmber))
        {
            return EnchantmentStackBehavior.ExistenceStack;
        }
        
        // Corrupted enchantment: HP loss is hardcoded to 2, ONLY Casey Yano KNOWS
        return EnchantmentStackBehavior.DisallowDuplicate;
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
        if (MultiEnchantmentStackApi.ResolveProvider(enchantment.GetType()) is IEnchantmentStackBehaviorProvider provider)
        {
            return Math.Max(1, provider.GetVisualStackCount(enchantment));
        }

        if (GetBehavior(enchantment.GetType()) != EnchantmentStackBehavior.MergeAmount)
        {
            return 1;
        }

        return TryGetValidMergedStackAmounts(enchantment, out int[] stackAmounts)
            ? Math.Max(1, stackAmounts.Length)
            : 1;
    }

    public static IEnumerable<MultiEnchantmentSupport.EnchantmentVisualState> ExpandVisualStates(CardModel? card)
    {
        return MultiEnchantmentSupport.GetOrderedVisualStates(card);
    }

    public static bool TryGetMergedStackAmounts(EnchantmentModel enchantment, out int[] stackAmounts)
    {
        return TryGetValidMergedStackAmounts(enchantment, out stackAmounts);
    }

    public static int GetResolvedMergedTotalAmount(EnchantmentModel enchantment)
    {
        if (GetBehavior(enchantment.GetType()) != EnchantmentStackBehavior.MergeAmount)
        {
            return Math.Max(1, enchantment.Amount);
        }

        int[]? rawStackAmounts = GetSavedIntArray(enchantment.Props, MergedStackAmountsPropertyName);
        if (rawStackAmounts is { Length: > 0 } && rawStackAmounts.All(static amount => amount > 0))
        {
            return Math.Max(1, rawStackAmounts.Sum());
        }

        return Math.Max(1, enchantment.Amount);
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

        if (MultiEnchantmentStackApi.ResolveProvider(enchantment.GetType()) is IEnchantmentStackBehaviorProvider provider)
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
        if (MultiEnchantmentStackApi.ResolveProvider(enchantment.GetType()) is IEnchantmentStackBehaviorProvider provider)
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

    public static void RefreshDerivedState(CardModel card)
    {
        RefreshDerivedKeywords(card);
    }

    private static void RefreshDerivedKeywords(CardModel card)
    {
        foreach (CardKeyword keyword in GetTrackedKeywords(card))
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
    }

    private static IEnumerable<CardKeyword> GetTrackedKeywords(CardModel card)
    {
        HashSet<CardKeyword> trackedKeywords = new();

        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card))
        {
            trackedKeywords.UnionWith(GetBuiltInTrackedKeywords(enchantment));
            foreach (IEnchantmentKeywordSourceProvider provider in MultiEnchantmentStackApi.ResolveKeywordProviders(enchantment.GetType()))
            {
                trackedKeywords.UnionWith(provider.GetTrackedKeywords(enchantment.GetType()));
            }
        }

        return trackedKeywords;
    }

    private static int GetKeywordSourceAmount(CardModel card, CardKeyword keyword)
    {
        int result = 0;
        foreach (EnchantmentModel enchantment in MultiEnchantmentSupport.GetEnchantments(card))
        {
            result += GetBuiltInKeywordSourceAmount(enchantment, keyword);
            foreach (IEnchantmentKeywordSourceProvider provider in MultiEnchantmentStackApi.ResolveKeywordProviders(enchantment.GetType()))
            {
                result += provider.GetKeywordSourceAmount(enchantment, keyword);
            }
        }

        return result;
    }

    private static int GetBuiltInKeywordSourceAmount(EnchantmentModel enchantment, CardKeyword keyword)
    {
        return (enchantment, keyword) switch
        {
            (Goopy, CardKeyword.Exhaust) => 1,
            (SoulsPower soulsPower, CardKeyword.Exhaust) => -soulsPower.Amount,
            (Steady, CardKeyword.Retain) => 1,
            (RoyallyApproved, CardKeyword.Retain) => 1,
            (RoyallyApproved, CardKeyword.Innate) => 1,
            (TezcatarasEmber, CardKeyword.Eternal) => 1,
            _ => 0,
        };
    }

    private static IEnumerable<CardKeyword> GetBuiltInTrackedKeywords(EnchantmentModel enchantment)
    {
        return enchantment switch
        {
            Goopy => new[] { CardKeyword.Exhaust },
            SoulsPower => new[] { CardKeyword.Exhaust },
            Steady => new[] { CardKeyword.Retain },
            RoyallyApproved => new[] { CardKeyword.Innate, CardKeyword.Retain },
            TezcatarasEmber => new[] { CardKeyword.Eternal },
            _ => Array.Empty<CardKeyword>(),
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

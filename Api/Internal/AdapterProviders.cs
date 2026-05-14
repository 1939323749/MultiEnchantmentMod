using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using LegacyDefinitionProvider = MultiEnchantmentMod.IEnchantmentStackDefinitionProvider<MegaCrit.Sts2.Core.Models.EnchantmentModel>;
using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;
using LegacyStackDefinition = MultiEnchantmentMod.EnchantmentStackDefinition;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api.Internal;

// Each adapter implements one of the legacy v1 provider interfaces (now internal on
// MultiEnchantmentMod.IEnchantment*Provider<T> after Step 9) and forwards every call to the
// v2 EnchantmentEntry it was constructed with. Both the shims and the interfaces live in the
// main mod assembly, so the internal accessibility is enough for the adapter pipeline to work.
//
// The interfaces are generic on T : EnchantmentModel, but EnchantmentEntry erases T to
// EnchantmentModel for storage. The strong type only matters when registering against the
// legacy table, so each shim is generic too and is instantiated via the
// MultiEnchantmentApi.Register&lt;T&gt;() entry point (or reflection for Register(Type)).

internal sealed class AdapterDefinitionProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentStackDefinitionProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }
    public int Priority => Entry.Priority;
    public LegacyStackDefinition GetDefinition() =>
        (Entry.Definition ?? StackDefinition.Default).ToLegacy();
}

internal sealed class AdapterMergedStateProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentMergedStateProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }
    public int Priority => Entry.Priority;

    public void ApplyMergedAmountDelta(TEnchantment enchantment, int addedAmount)
    {
        Entry.OnMergedDelta?.Invoke(enchantment, addedAmount);
    }

    public void RefreshMergedState(TEnchantment enchantment)
    {
        if (Entry.OnMergedRefresh != null)
        {
            Entry.OnMergedRefresh(enchantment);
        }
        else
        {
            // Replicate the documented default in EnchantmentDefinition<T>.OnMergedRefresh so
            // callers who only set OnMergedDelta still get a working refresh path.
            enchantment.RecalculateValues();
            enchantment.Card?.DynamicVars.RecalculateForUpgradeOrEnchant();
        }
    }
}

internal sealed class AdapterExecutionPolicyProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentExecutionPolicyProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }
    public int Priority => Entry.Priority;
    public LegacyExecutionPolicy GetExecutionPolicy() =>
        Entry.ExecutionPolicy ?? new LegacyExecutionPolicy();
}

internal sealed class AdapterKeywordSourceProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentKeywordSourceProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }
    public int Priority => Entry.Priority;

    public IEnumerable<CardKeyword> GetTrackedKeywords()
    {
        return Entry.Keywords.Select(contribution => contribution.Keyword).Distinct();
    }

    public int GetKeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword)
    {
        int total = 0;
        foreach (KeywordContribution contribution in Entry.Keywords)
        {
            if (contribution.Keyword == keyword)
            {
                total += contribution.AmountFn(snapshot);
            }
        }

        return total;
    }
}

internal sealed class AdapterPresentationProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentPresentationProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }
    public int Priority => Entry.Priority;

    public IReadOnlyList<int>? GetVisualSliceAmounts(EnchantmentStackSnapshot snapshot)
    {
        return Entry.GetVisualSliceAmounts?.Invoke(snapshot);
    }

    public bool TryFormatExtraCardText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText)
    {
        if (Entry.FormatExtraText != null)
        {
            return Entry.FormatExtraText(snapshot, defaultText, out formattedText);
        }

        formattedText = defaultText;
        return false;
    }
}

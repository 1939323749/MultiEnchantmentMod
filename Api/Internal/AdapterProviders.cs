using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using LegacyDefinitionProvider = MultiEnchantmentMod.IEnchantmentStackDefinitionProvider<MegaCrit.Sts2.Core.Models.EnchantmentModel>;
using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;
using LegacyStackDefinition = MultiEnchantmentMod.EnchantmentStackDefinition;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api.Internal;

// Each adapter implements one of the internal provider interfaces on
// MultiEnchantmentMod.IEnchantment*Provider<T> and forwards every call to the v2 EnchantmentEntry
// it was constructed with. Both the shims and the interfaces live in the main mod assembly, so the
// internal accessibility is enough for the adapter pipeline to work.
//
// The interfaces are generic on T : EnchantmentModel, but EnchantmentEntry erases T to
// EnchantmentModel for storage. The strong type only matters when registering against the
// legacy table, so each shim is generic too and is instantiated via the
// MultiEnchantmentApi.Register&lt;T&gt;() entry point (or reflection for Register(Type)).
//
// All author-supplied delegate invocations flow through SafeInvoker.Run so a buggy enchantment
// callback only logs and is skipped, instead of bubbling up through a vanilla Harmony patch.

internal sealed class AdapterDefinitionProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentStackDefinitionProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }
    public LegacyStackDefinition GetDefinition() =>
        (Entry.Definition ?? StackDefinition.Default).ToLegacy();
}

internal sealed class AdapterMergedStateProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentMergedStateProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }

    public void ApplyMergedAmountDelta(TEnchantment enchantment, int addedAmount)
    {
        if (Entry.OnMergedDelta == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnMergedDelta),
            () => Entry.OnMergedDelta!(enchantment, addedAmount));
    }

    public void RefreshMergedState(TEnchantment enchantment)
    {
        if (Entry.OnMergedRefresh != null)
        {
            SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnMergedRefresh),
                () => Entry.OnMergedRefresh!(enchantment));
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
    public LegacyExecutionPolicy GetExecutionPolicy() =>
        Entry.ExecutionPolicy ?? new LegacyExecutionPolicy();
}

internal sealed class AdapterKeywordSourceProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentKeywordSourceProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }

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
                total += SafeInvoker.Run(
                    Entry.EnchantmentType,
                    $"TrackKeyword({keyword})",
                    () => contribution.AmountFn(snapshot),
                    fallback: 0);
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

    public IReadOnlyList<int>? GetVisualSliceAmounts(EnchantmentStackSnapshot snapshot)
    {
        if (Entry.GetVisualSliceAmounts == null) return null;
        return SafeInvoker.Run(
            Entry.EnchantmentType,
            nameof(Entry.GetVisualSliceAmounts),
            () => Entry.GetVisualSliceAmounts!(snapshot),
            fallback: null);
    }

    public bool TryFormatExtraCardText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText)
    {
        if (Entry.FormatExtraText == null)
        {
            formattedText = defaultText;
            return false;
        }

        // Out params can't be forwarded through SafeInvoker.Run<T>; wrap the call directly and
        // fall back to the default text on failure so card rendering stays stable.
        string captured = defaultText;
        bool handled = SafeInvoker.Run(
            Entry.EnchantmentType,
            nameof(Entry.FormatExtraText),
            () =>
            {
                bool ok = Entry.FormatExtraText!(snapshot, defaultText, out string result);
                captured = result;
                return ok;
            },
            fallback: false);

        formattedText = handled ? captured : defaultText;
        return handled;
    }
}

internal sealed class AdapterLifecycleProvider<TEnchantment>
    : global::MultiEnchantmentMod.IEnchantmentLifecycleProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    public required EnchantmentEntry Entry { get; init; }

    public Api.EnchantmentScope GetScope() =>
        Entry.GetScope == null
            ? Api.EnchantmentScope.Permanent
            : SafeInvoker.Run(
                Entry.EnchantmentType,
                nameof(Entry.GetScope),
                () => Entry.GetScope!(),
                fallback: Api.EnchantmentScope.Permanent);

    public void OnApplied(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnApplied == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnApplied),
            () => Entry.OnApplied!(card, enchantment));
    }

    public bool OnRemoved(CardModel card, TEnchantment enchantment, Api.RemovalReason reason)
    {
        if (Entry.OnRemoved == null) return true;
        // Fallback true means "removal not vetoed" — consistent with the bare-?.Invoke default.
        return SafeInvoker.Run(
            Entry.EnchantmentType,
            nameof(Entry.OnRemoved),
            () => Entry.OnRemoved!(card, enchantment, reason),
            fallback: true);
    }

    public void OnCombatStart(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCombatStart == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCombatStart),
            () => Entry.OnCombatStart!(card, enchantment));
    }

    public void OnCombatEnd(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCombatEnd == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCombatEnd),
            () => Entry.OnCombatEnd!(card, enchantment));
    }

    public void OnTurnStart(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnTurnStart == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnTurnStart),
            () => Entry.OnTurnStart!(card, enchantment));
    }

    public void OnTurnEnd(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnTurnEnd == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnTurnEnd),
            () => Entry.OnTurnEnd!(card, enchantment));
    }

    public void OnRestored(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnRestored == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnRestored),
            () => Entry.OnRestored!(card, enchantment));
    }

    public void OnCardPlayed(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCardPlayed == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCardPlayed),
            () => Entry.OnCardPlayed!(card, enchantment));
    }

    public void OnCardDrawn(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCardDrawn == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCardDrawn),
            () => Entry.OnCardDrawn!(card, enchantment));
    }

    public void OnCardExhausted(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCardExhausted == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCardExhausted),
            () => Entry.OnCardExhausted!(card, enchantment));
    }

    public void OnCardDiscarded(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCardDiscarded == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCardDiscarded),
            () => Entry.OnCardDiscarded!(card, enchantment));
    }

    public void OnCardEnteredCombat(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCardEnteredCombat == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCardEnteredCombat),
            () => Entry.OnCardEnteredCombat!(card, enchantment));
    }

    public void OnAfterDamageReceived(CardModel card, TEnchantment enchantment, DamageReceivedContext context)
    {
        if (Entry.OnAfterDamageReceived == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnAfterDamageReceived),
            () => Entry.OnAfterDamageReceived!(card, enchantment, context));
    }

    public void OnSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side)
    {
        if (Entry.OnSideTurnStart == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnSideTurnStart),
            () => Entry.OnSideTurnStart!(card, enchantment, side));
    }

    public void OnBeforeSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side)
    {
        if (Entry.OnBeforeSideTurnStart == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnBeforeSideTurnStart),
            () => Entry.OnBeforeSideTurnStart!(card, enchantment, side));
    }

    public void OnBeforeAttack(CardModel card, TEnchantment enchantment, AttackCommand command)
    {
        if (Entry.OnBeforeAttack == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnBeforeAttack),
            () => Entry.OnBeforeAttack!(card, enchantment, command));
    }

    public void OnAfterAttack(CardModel card, TEnchantment enchantment, AttackCommand command)
    {
        if (Entry.OnAfterAttack == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnAfterAttack),
            () => Entry.OnAfterAttack!(card, enchantment, command));
    }

    public void OnCardChangedPiles(CardModel card, TEnchantment enchantment, PileType oldPile, AbstractModel? source)
    {
        if (Entry.OnCardChangedPiles == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCardChangedPiles),
            () => Entry.OnCardChangedPiles!(card, enchantment, oldPile, source));
    }

    public void OnCardRetained(CardModel card, TEnchantment enchantment)
    {
        if (Entry.OnCardRetained == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnCardRetained),
            () => Entry.OnCardRetained!(card, enchantment));
    }

    public void OnBeforeBlockGained(CardModel card, TEnchantment enchantment, BlockGainContext context)
    {
        if (Entry.OnBeforeBlockGained == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnBeforeBlockGained),
            () => Entry.OnBeforeBlockGained!(card, enchantment, context));
    }

    public void OnBlockGained(CardModel card, TEnchantment enchantment, BlockGainContext context)
    {
        if (Entry.OnBlockGained == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnBlockGained),
            () => Entry.OnBlockGained!(card, enchantment, context));
    }

    public bool OnShouldDie(CardModel card, TEnchantment enchantment, Creature creature)
    {
        // Default true means "this enchantment does not prevent death" — semantics match vanilla
        // (Hook.ShouldDie returns true unless a listener says otherwise).
        if (Entry.OnShouldDie == null) return true;
        return SafeInvoker.Run(
            Entry.EnchantmentType,
            nameof(Entry.OnShouldDie),
            () => Entry.OnShouldDie!(card, enchantment, creature),
            fallback: true);
    }

    // === Phase 4 — broadcast card-event hooks (opt-in) =======================================
    // Null-check is the opt-in gate: when the entry field is unset, these return immediately
    // so enchantments that don't register the broadcast hook pay zero cost beyond the method call.

    public void OnAnyCardPlayed(CardModel playedCard, CardModel selfCard, TEnchantment enchantment)
    {
        if (Entry.OnAnyCardPlayed == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnAnyCardPlayed),
            () => Entry.OnAnyCardPlayed!(playedCard, selfCard, enchantment));
    }

    public void OnAnyCardDrawn(CardModel drawnCard, CardModel selfCard, TEnchantment enchantment)
    {
        if (Entry.OnAnyCardDrawn == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnAnyCardDrawn),
            () => Entry.OnAnyCardDrawn!(drawnCard, selfCard, enchantment));
    }

    public void OnAnyCardExhausted(CardModel exhaustedCard, CardModel selfCard, TEnchantment enchantment)
    {
        if (Entry.OnAnyCardExhausted == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnAnyCardExhausted),
            () => Entry.OnAnyCardExhausted!(exhaustedCard, selfCard, enchantment));
    }

    public void OnAnyCardDiscarded(CardModel discardedCard, CardModel selfCard, TEnchantment enchantment)
    {
        if (Entry.OnAnyCardDiscarded == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnAnyCardDiscarded),
            () => Entry.OnAnyCardDiscarded!(discardedCard, selfCard, enchantment));
    }

    // === Phase 5 — sibling lifecycle hooks ====================================================

    public void OnSiblingApplied(CardModel card, TEnchantment self, EnchantmentModel newSibling)
    {
        if (Entry.OnSiblingApplied == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnSiblingApplied),
            () => Entry.OnSiblingApplied!(card, self, newSibling));
    }

    public void OnSiblingRemoved(CardModel card, TEnchantment self, EnchantmentModel removedSibling, Api.RemovalReason reason)
    {
        if (Entry.OnSiblingRemoved == null) return;
        SafeInvoker.Run(Entry.EnchantmentType, nameof(Entry.OnSiblingRemoved),
            () => Entry.OnSiblingRemoved!(card, self, removedSibling, reason));
    }
}

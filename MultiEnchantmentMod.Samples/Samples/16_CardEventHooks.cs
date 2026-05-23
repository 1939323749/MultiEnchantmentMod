using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — companion EnchantmentDefinition<T> subclasses for card-event lifecycle hooks.
//
// Goal: demonstrate the three card-event callbacks that the mod exposes through its
// Hook.AfterCardDiscarded / Hook.AfterCardExhausted / Hook.AfterCardChangedPiles patches.
// Each callback is card-scoped (only the enchantment on the affected card is notified) and
// gated by IsActive — an inactive ConditionalActive enchantment will not receive the event.
//
// These callbacks are void, meaning they cannot directly await async game commands. For
// actions that need async context (e.g. CreatureCmd.Damage), integrate via the combat
// state's command queue or store intent for the next OnPlay. The samples below mark the
// game-action integration point with a comment.

// ─────────────────────────────────────────────────────────────────────────────
// 1a. SampleEmber — OnCardDiscarded + MergeAmount
//     "When this card is discarded, deal damage equal to stack amount to a random enemy."
// ─────────────────────────────────────────────────────────────────────────────

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleEmber : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

public sealed class SampleEmberDefinition : EnchantmentDefinition<SampleEmber>
{
    protected override void OnCardDiscarded(CardModel card, SampleEmber enchantment)
    {
        _ = card;
        int damage = enchantment.Amount;

        // Game action: deal `damage` to a random enemy creature.
        // Integration point — pick from combat state's enemy pool and apply damage.
        // Example (async): await CreatureCmd.Damage(choiceContext, randomEnemy, damage, card);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 1b. SampleSamsara — OnCardExhausted + DisallowDuplicate
//     "When this card is exhausted, return it to the draw pile instead."
// ─────────────────────────────────────────────────────────────────────────────

[Enchantment(Stack = StackBehavior.DisallowDuplicate)]
public sealed class SampleSamsara : EnchantmentModel { }

public sealed class SampleSamsaraDefinition : EnchantmentDefinition<SampleSamsara>
{
    protected override void OnCardExhausted(CardModel card, SampleSamsara enchantment)
    {
        _ = enchantment;

        // Game action: move the card from the Exhaust pile to the Draw pile.
        // Integration point — exact pile-move API depends on CardModel.Pile.
        // Example: card.Pile.MoveTo(PileType.Draw, position: 0); // top of draw pile
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 1c. SampleWebTrap — OnCardChangedPiles(oldPile) + MergeAmount
//     "When this card leaves the hand and enters the discard pile, apply Weakness
//      equal to stack amount to a random enemy."
// ─────────────────────────────────────────────────────────────────────────────

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleWebTrap : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

public sealed class SampleWebTrapDefinition : EnchantmentDefinition<SampleWebTrap>
{
    protected override void OnCardChangedPiles(
        CardModel card,
        SampleWebTrap enchantment,
        PileType oldPile,
        AbstractModel? source)
    {
        _ = source;

        // oldPile is the pile the card JUST LEFT; card.Pile.Type is where it IS now.
        // Detect "hand → discard" transitions (discarded by player or game effect).
        if (oldPile == PileType.Hand && card.Pile?.Type == PileType.Discard)
        {
            int stacks = enchantment.Amount;

            // Game action: apply `stacks` stacks of Weakness to a random enemy.
            // Integration point — exact debuff API (e.g. ApplyStatus).
        }
    }
}

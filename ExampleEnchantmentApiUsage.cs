// using System;
// using System.Collections.Generic;
// using System.Threading.Tasks;
// using MegaCrit.Sts2.Core.Commands;
// using MegaCrit.Sts2.Core.Entities.Cards;
// using MegaCrit.Sts2.Core.GameActions.Multiplayer;
// using MegaCrit.Sts2.Core.HoverTips;
// using MegaCrit.Sts2.Core.Localization.DynamicVars;
// using MegaCrit.Sts2.Core.Models;
// using MegaCrit.Sts2.Core.ValueProps;
//
// namespace MultiEnchantmentMod;
//
// // Example source: reference implementation for third-party mods integrating with
// // MultiEnchantmentMod's public stacking API.
// // This file intentionally does not register gameplay content into the game's model database.
// // The sample providers may be auto-discovered, but they only target the sample enchantment types
// // defined in this file and therefore do not affect normal gameplay content.
//
// internal sealed class ExampleBrittleEchoEnchantment : EnchantmentModel
// {
//     public override bool HasExtraCardText => true;
//     public override bool ShowAmount => true;
//
//     protected override IEnumerable<DynamicVar> CanonicalVars =>
//         new[] { new BlockVar(2m, ValueProp.Move) };
//
//     protected override IEnumerable<IHoverTip> ExtraHoverTips =>
//         new[] { HoverTipFactory.FromKeyword(CardKeyword.Exhaust) };
//
//     public override bool CanEnchantCardType(CardType cardType)
//     {
//         return cardType == CardType.Skill;
//     }
//
//     public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay? cardPlay)
//     {
//         await CreatureCmd.GainBlock(Card.Owner.Creature, DynamicVars.Block, cardPlay);
//     }
//
//     public override void RecalculateValues()
//     {
//         // Each merged stack increases the granted block by 2.
//         DynamicVars.Block.BaseValue = Amount * 2;
//     }
// }
//
// internal sealed class ExampleBrittleEchoStackProvider : IEnchantmentStackBehaviorProvider
// {
//     public int Priority => 10;
//
//     public bool AppliesTo(Type enchantmentType)
//     {
//         return enchantmentType == typeof(ExampleBrittleEchoEnchantment);
//     }
//
//     public EnchantmentStackBehavior GetBehavior(Type enchantmentType)
//     {
//         return EnchantmentStackBehavior.MergeAmount;
//     }
//
//     public int GetVisualStackCount(EnchantmentModel enchantment)
//     {
//         // This sample assumes every application adds exactly 1 stack. If your enchantment can be
//         // applied with variable per-stack amounts, you must persist explicit per-stack metadata
//         // instead of inferring badge count from the merged Amount.
//         return Math.Max(1, enchantment.Amount);
//     }
//
//     public void ApplyMergedAmountDelta(EnchantmentModel enchantment, int addedAmount)
//     {
//         // This sample enchantment has no one-shot OnEnchant side effects, so merging only needs a
//         // refresh. Real enchantments like Instinct would apply incremental card mutations here.
//     }
//
//     public void RefreshMergedState(EnchantmentModel enchantment)
//     {
//         enchantment.RecalculateValues();
//         enchantment.Card.DynamicVars.RecalculateForUpgradeOrEnchant();
//     }
// }
//
// internal sealed class ExampleRetainAuraEnchantment : EnchantmentModel
// {
//     protected override IEnumerable<IHoverTip> ExtraHoverTips =>
//         new[] { HoverTipFactory.FromKeyword(CardKeyword.Retain) };
//
//     public override bool CanEnchant(CardModel card)
//     {
//         return base.CanEnchant(card) && card.Type == CardType.Skill;
//     }
//
//     protected override void OnEnchant()
//     {
//         // This is intentionally a one-shot mutation. ExistenceStack ensures only the first copy
//         // gets to run this path.
//         Card.AddKeyword(CardKeyword.Retain);
//     }
// }
//
// internal sealed class ExampleRetainAuraStackProvider : IEnchantmentStackBehaviorProvider
// {
//     public int Priority => 10;
//
//     public bool AppliesTo(Type enchantmentType)
//     {
//         return enchantmentType == typeof(ExampleRetainAuraEnchantment);
//     }
//
//     public EnchantmentStackBehavior GetBehavior(Type enchantmentType)
//     {
//         return EnchantmentStackBehavior.ExistenceStack;
//     }
//
//     public int GetVisualStackCount(EnchantmentModel enchantment)
//     {
//         // Existence-style stacks usually want one badge per instance.
//         return 1;
//     }
//
//     public void ApplyMergedAmountDelta(EnchantmentModel enchantment, int addedAmount)
//     {
//     }
//
//     public void RefreshMergedState(EnchantmentModel enchantment)
//     {
//     }
// }
//
// internal sealed class ExampleAddExhaustEnchantment : EnchantmentModel
// {
//     protected override IEnumerable<IHoverTip> ExtraHoverTips =>
//         new[] { HoverTipFactory.FromKeyword(CardKeyword.Exhaust) };
//
//     protected override void OnEnchant()
//     {
//         // This direct keyword mutation is left here to mirror how many third-party mods are
//         // originally written. The keyword provider below makes the final result deterministic even
//         // when another enchantment removes Exhaust.
//         Card.AddKeyword(CardKeyword.Exhaust);
//     }
// }
//
// internal sealed class ExampleRemoveExhaustEnchantment : EnchantmentModel
// {
//     protected override IEnumerable<IHoverTip> ExtraHoverTips =>
//         new[] { HoverTipFactory.FromKeyword(CardKeyword.Exhaust) };
//
//     public override bool CanEnchant(CardModel card)
//     {
//         return base.CanEnchant(card) && card.Keywords.Contains(CardKeyword.Exhaust);
//     }
//
//     protected override void OnEnchant()
//     {
//         Card.RemoveKeyword(CardKeyword.Exhaust);
//     }
// }
//
// internal sealed class ExampleExhaustKeywordProvider : IEnchantmentKeywordSourceProvider
// {
//     public int Priority => 10;
//
//     public bool AppliesTo(Type enchantmentType)
//     {
//         return enchantmentType == typeof(ExampleAddExhaustEnchantment) ||
//                enchantmentType == typeof(ExampleRemoveExhaustEnchantment);
//     }
//
//     public IEnumerable<CardKeyword> GetTrackedKeywords(Type enchantmentType)
//     {
//         yield return CardKeyword.Exhaust;
//     }
//
//     public int GetKeywordSourceAmount(EnchantmentModel enchantment, CardKeyword keyword)
//     {
//         if (keyword != CardKeyword.Exhaust)
//         {
//             return 0;
//         }
//
//         return enchantment switch
//         {
//             ExampleAddExhaustEnchantment => 1,
//             ExampleRemoveExhaustEnchantment => -1,
//             _ => 0,
//         };
//     }
// }
//
// internal sealed class ExampleReturnToHandEnchantment : EnchantmentModel
// {
//     protected override IEnumerable<IHoverTip> ExtraHoverTips =>
//         new[] { HoverTipFactory.FromKeyword(CardKeyword.Retain) };
//
//     public override bool CanEnchant(CardModel card)
//     {
//         // Keep the sample conservative: only allow playable non-power cards that normally discard.
//         return base.CanEnchant(card) &&
//                card.Type != CardType.Power &&
//                !card.Keywords.Contains(CardKeyword.Unplayable);
//     }
//
//     public override (PileType, CardPilePosition) ModifyCardPlayResultPileTypeAndPosition(
//         CardModel card,
//         bool isAutoPlay,
//         ResourceInfo resources,
//         PileType pileType,
//         CardPilePosition position)
//     {
//         if (card != Card)
//         {
//             return (pileType, position);
//         }
//
//         // Match cards like ParticleWall: only reroute normal discard results back to hand, and
//         // leave Exhaust / None / other custom destinations untouched.
//         if (pileType != PileType.Discard)
//         {
//             return (pileType, position);
//         }
//
//         return (PileType.Hand, CardPilePosition.Bottom);
//     }
// }

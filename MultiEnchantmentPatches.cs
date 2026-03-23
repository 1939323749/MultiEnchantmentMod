using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace MultiEnchantmentMod;

[HarmonyPatch]
internal static class MultiEnchantmentPatches
{
    private static readonly MethodInfo? CalculatedVarGetBaseVarMethod =
        AccessTools.Method(typeof(CalculatedVar), "GetBaseVar");
    private static readonly PropertyInfo? ArchaicToothTranscendenceUpgradesProperty =
        AccessTools.Property(typeof(ArchaicTooth), "TranscendenceUpgrades");
    private static readonly PropertyInfo? RestSiteOptionOwnerProperty =
        AccessTools.Property(typeof(RestSiteOption), "Owner");
    private static readonly FieldInfo? NCardEnchantVfxCardModelField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_cardModel");
    private static readonly FieldInfo? NCardEnchantVfxCardNodeField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_cardNode");
    private static readonly FieldInfo? NCardEnchantVfxIconField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_enchantmentIcon");
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.CanEnchant))]
    [HarmonyPrefix]
    private static bool CanEnchantPrefix(EnchantmentModel __instance, CardModel card, ref bool __result)
    {
        // Base-game source: EnchantmentModel.CanEnchant.
        // Keep this logic aligned with vanilla except for allowing stackable duplicates across
        // primary + extra enchantment slots.
        CardType type = card.Type;
        if (type is CardType.Status or CardType.Curse or CardType.Quest)
        {
            __result = false;
            return false;
        }

        if (!__instance.CanEnchantCardType(card.Type))
        {
            __result = false;
            return false;
        }

        CardPile? pile = card.Pile;
        if (pile != null && pile.Type == PileType.Deck && card.Keywords.Contains(CardKeyword.Unplayable))
        {
            __result = false;
            return false;
        }

        __result = MultiEnchantmentSupport.GetEnchantment(card, __instance.GetType()) == null || __instance.IsStackable;
        return false;
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), new[] { typeof(EnchantmentModel), typeof(CardModel), typeof(decimal) })]
    [HarmonyPrefix]
    private static bool EnchantPrefix(EnchantmentModel enchantment, CardModel card, decimal amount, ref EnchantmentModel? __result)
    {
        __result = MultiEnchantmentSupport.ApplyEnchantment(enchantment, card, amount);
        return false;
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.ClearEnchantment))]
    [HarmonyPrefix]
    private static void ClearEnchantmentPrefix(CardModel card)
    {
        MultiEnchantmentSupport.ClearAdditionalEnchantments(card, triggerChanged: card.Enchantment == null);
    }

    [HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
    [HarmonyPostfix]
    private static void MutableClonePostfix(AbstractModel __instance, AbstractModel __result)
    {
        if (__instance is CardModel source && __result is CardModel clone)
        {
            MultiEnchantmentSupport.CloneAdditionalEnchantments(source, clone);
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.GetEnchantedReplayCount))]
    [HarmonyPrefix]
    private static bool ReplayCountPrefix(CardModel __instance, ref int __result)
    {
        __result = MultiEnchantmentSupport.GetReplayCount(__instance);
        return false;
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    [HarmonyPostfix]
    private static void ToSerializablePostfix(CardModel __instance, ref SerializableCard __result)
    {
        MultiEnchantmentSupport.SerializeAdditionalEnchantments(__instance, __result);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
    [HarmonyPostfix]
    private static void FromSerializablePostfix(SerializableCard save, ref CardModel __result)
    {
        MultiEnchantmentSupport.DeserializeAdditionalEnchantments(save, __result);
    }

    [HarmonyPatch(typeof(CardModel), "get_HoverTips")]
    [HarmonyPostfix]
    private static void HoverTipsPostfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        __result = MultiEnchantmentSupport.AppendAdditionalHoverTips(__instance, __result);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForPile), new[] { typeof(PileType), typeof(Creature) })]
    [HarmonyPostfix]
    private static void DescriptionForPilePostfix(CardModel __instance, ref string __result)
    {
        MultiEnchantmentSupport.AppendAdditionalExtraCardText(__instance, ref __result);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForUpgradePreview))]
    [HarmonyPostfix]
    private static void DescriptionForUpgradePreviewPostfix(CardModel __instance, ref string __result)
    {
        MultiEnchantmentSupport.AppendAdditionalExtraCardText(__instance, ref __result);
    }

    [HarmonyPatch(typeof(CardModel), "get_ShouldGlowGold")]
    [HarmonyPostfix]
    private static void ShouldGlowGoldPostfix(CardModel __instance, ref bool __result)
    {
        __result = __result || MultiEnchantmentSupport.ShouldGlowGold(__instance);
    }

    [HarmonyPatch(typeof(CardModel), "get_ShouldGlowRed")]
    [HarmonyPostfix]
    private static void ShouldGlowRedPostfix(CardModel __instance, ref bool __result)
    {
        __result = __result || MultiEnchantmentSupport.ShouldGlowRed(__instance);
    }

    [HarmonyPatch(typeof(CombatManager), "SetupPlayerTurn")]
    [HarmonyPrefix]
    private static bool SetupPlayerTurnPrefix(
        CombatManager __instance,
        Player player,
        HookPlayerChoiceContext playerChoiceContext,
        ref Task __result)
    {
        __result = SetupPlayerTurnWithMultiEnchantments(__instance, player, playerChoiceContext);
        return false;
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyBlock))]
    [HarmonyPrefix]
    private static bool HookModifyBlockPrefix(
        CombatState combatState,
        Creature target,
        decimal block,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ref IEnumerable<AbstractModel> modifiers,
        ref decimal __result)
    {
        // Base-game source: Hook.ModifyBlock.
        // Vanilla applies only cardSource.Enchantment; we fold in every enchantment on the card
        // before preserving the original additive -> multiplicative listener order.
        List<AbstractModel> modifyingModels = new();
        decimal value = MultiEnchantmentSupport.ApplyBlockEnchantments(cardSource, block, props);

        foreach (AbstractModel model in combatState.IterateHookListeners())
        {
            decimal add = model.ModifyBlockAdditive(target, value, props, cardSource, cardPlay);
            value += add;
            if (add != 0m)
            {
                modifyingModels.Add(model);
            }
        }

        foreach (AbstractModel model in combatState.IterateHookListeners())
        {
            decimal multiply = model.ModifyBlockMultiplicative(target, value, props, cardSource, cardPlay);
            value *= multiply;
            if (multiply != 1m)
            {
                modifyingModels.Add(model);
            }
        }

        modifiers = modifyingModels;
        __result = Math.Max(0m, value);
        return false;
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    [HarmonyPrefix]
    private static bool HookModifyDamagePrefix(
        IRunState runState,
        CombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        ref IEnumerable<AbstractModel> modifiers,
        ref decimal __result)
    {
        // Base-game source: Hook.ModifyDamage.
        // Vanilla applies only the primary enchantment; this patch extends that to all enchantments
        // while preserving the vanilla multi-target preview behavior and listener ordering.
        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(cardSource, damage, props, modifyDamageHookType);
        bool multiTargetPreview = target == null && previewMode == CardPreviewMode.MultiCreatureTargeting;

        if (multiTargetPreview && cardSource != null)
        {
            TargetType targetType = cardSource.TargetType;
            if ((uint)(targetType - 3) <= 1u)
            {
                CardPile? pile = cardSource.Pile;
                multiTargetPreview = pile != null && (pile.Type == PileType.Hand || pile.Type == PileType.Play);
            }
            else
            {
                multiTargetPreview = false;
            }
        }

        if (multiTargetPreview)
        {
            bool allEqual = true;
            decimal? sharedValue = null;
            List<AbstractModel> allModifiers = new();

            foreach (Creature enemy in combatState?.HittableEnemies ?? Array.Empty<Creature>())
            {
                List<AbstractModel> perTargetModifiers = new();
                decimal targetValue = ModifyDamageInternal(runState, combatState, enemy, dealer, value, props, cardSource, modifyDamageHookType, perTargetModifiers);
                if (!sharedValue.HasValue)
                {
                    sharedValue = targetValue;
                }
                else if ((int)targetValue != (int)sharedValue.Value)
                {
                    allEqual = false;
                    break;
                }

                allModifiers.AddRange(perTargetModifiers);
            }

            if (sharedValue.HasValue && allEqual)
            {
                modifiers = allModifiers.Distinct().ToList();
                __result = Math.Max(0m, sharedValue.Value);
            }
            else
            {
                modifiers = Array.Empty<AbstractModel>();
                __result = Math.Max(0m, value);
            }

            return false;
        }

        List<AbstractModel> modifiersList = new();
        value = ModifyDamageInternal(runState, combatState, target, dealer, value, props, cardSource, modifyDamageHookType, modifiersList);
        modifiers = modifiersList;
        __result = Math.Max(0m, value);
        return false;
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    [HarmonyPrefix]
    private static bool HookAfterCardPlayedPrefix(CombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task __result)
    {
        // Base-game source: Hook.AfterCardPlayed.
        // This remains a full replacement because the extra enchantments need their own OnPlay pass
        // before the normal AfterCardPlayed / Late listener sweeps.
        __result = MultiEnchantmentSupport.AfterCardPlayedWithAdditionalEnchantments(combatState, choiceContext, cardPlay);
        return false;
    }

    [HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues))]
    [HarmonyPostfix]
    private static void RecalculateCardValuesPostfix(PlayerCombatState __instance)
    {
        foreach (CardModel card in __instance.AllCards)
        {
            MultiEnchantmentSupport.RecalculateAdditionalEnchantments(card);
        }
    }

    [HarmonyPatch(typeof(RunState), nameof(RunState.IterateHookListeners))]
    [HarmonyPostfix]
    private static void RunListenersPostfix(RunState __instance, ref IEnumerable<AbstractModel> __result)
    {
        __result = MultiEnchantmentSupport.AppendRunStateExtraEnchantments(__instance, __result);
    }

    [HarmonyPatch(typeof(CombatState), nameof(CombatState.IterateHookListeners))]
    [HarmonyPostfix]
    private static void CombatListenersPostfix(CombatState __instance, ref IEnumerable<AbstractModel> __result)
    {
        __result = MultiEnchantmentSupport.AppendCombatStateExtraEnchantments(__instance, __result);
    }

    [HarmonyPatch(typeof(DamageVar), nameof(DamageVar.UpdateCardPreview))]
    [HarmonyPrefix]
    private static bool DamageVarUpdateCardPreviewPrefix(DamageVar __instance, CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
    {
        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(card, __instance.BaseValue, __instance.Props, ModifyDamageHookType.All);
        if (!card.IsEnchantmentPreview && MultiEnchantmentSupport.HasAnyEnchantments(card))
        {
            MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
        }

        if (runGlobalHooks)
        {
            value = Hook.ModifyDamage(card.Owner.RunState, card.CombatState, target, card.Owner.Creature, __instance.BaseValue, __instance.Props, card, ModifyDamageHookType.All, previewMode, out IEnumerable<AbstractModel> _);
        }

        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(BlockVar), nameof(BlockVar.UpdateCardPreview))]
    [HarmonyPrefix]
    private static bool BlockVarUpdateCardPreviewPrefix(BlockVar __instance, CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
    {
        decimal value = MultiEnchantmentSupport.ApplyBlockEnchantments(card, __instance.BaseValue, __instance.Props);
        if (!card.IsEnchantmentPreview && MultiEnchantmentSupport.HasAnyEnchantments(card))
        {
            MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
        }

        if (runGlobalHooks)
        {
            value = Hook.ModifyBlock(card.CombatState, card.Owner.Creature, __instance.BaseValue, __instance.Props, card, null, out IEnumerable<AbstractModel> _);
        }

        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(CalculatedDamageVar), nameof(CalculatedDamageVar.UpdateCardPreview))]
    [HarmonyPrefix]
    private static bool CalculatedDamageVarUpdateCardPreviewPrefix(
        CalculatedDamageVar __instance,
        CardModel card,
        CardPreviewMode previewMode,
        Creature? target,
        bool runGlobalHooks)
    {
        // Base-game source: CalculatedDamageVar.UpdateCardPreview.
        // The important invariant here is "apply enchantments exactly once": first to the base var
        // used by Calculate(), then only run non-enchantment global hooks on the calculated result.
        DynamicVar baseVar = GetCalculatedBaseVar(__instance);
        decimal enchantedBase = MultiEnchantmentSupport.ApplyDamageEnchantments(card, baseVar.BaseValue, __instance.Props, ModifyDamageHookType.All);
        enchantedBase = Math.Max(enchantedBase, 0m);
        if (card.IsEnchantmentPreview)
        {
            __instance.PreviewValue = enchantedBase;
        }
        else if (MultiEnchantmentSupport.HasAnyEnchantments(card))
        {
            MultiEnchantmentSupport.SetEnchantedValue(__instance, enchantedBase);
        }

        decimal value = __instance.Calculate(target);
        if (runGlobalHooks)
        {
            CombatState combatState = card.CombatState ?? card.Owner.Creature.CombatState;
            List<AbstractModel> modifiers = new();
            value = ModifyDamageInternal(
                card.Owner.RunState,
                combatState,
                target,
                __instance.IsFromOsty ? card.Owner.Osty : card.Owner.Creature,
                value,
                __instance.Props,
                card,
                ModifyDamageHookType.All,
                modifiers);
        }
        else if (!card.IsEnchantmentPreview)
        {
            value = MultiEnchantmentSupport.ApplyDamageEnchantments(card, value, __instance.Props, ModifyDamageHookType.All);
        }

        __instance.PreviewValue = Math.Max(value, 0m);
        return false;
    }

    [HarmonyPatch(typeof(CalculatedBlockVar), nameof(CalculatedBlockVar.UpdateCardPreview))]
    [HarmonyPrefix]
    private static bool CalculatedBlockVarUpdateCardPreviewPrefix(
        CalculatedBlockVar __instance,
        CardModel card,
        CardPreviewMode previewMode,
        Creature? target,
        bool runGlobalHooks)
    {
        // Base-game source: CalculatedBlockVar.UpdateCardPreview.
        // Keep this in sync with the damage variant above: enchant the calculated base once, then
        // feed the calculated value through the remaining global block modifiers.
        DynamicVar baseVar = GetCalculatedBaseVar(__instance);
        decimal enchantedBase = MultiEnchantmentSupport.ApplyBlockEnchantments(card, baseVar.BaseValue, __instance.Props);
        if (card.IsEnchantmentPreview)
        {
            __instance.PreviewValue = enchantedBase;
        }
        else if (MultiEnchantmentSupport.HasAnyEnchantments(card))
        {
            MultiEnchantmentSupport.SetEnchantedValue(__instance, enchantedBase);
        }

        decimal value = __instance.Calculate(target);
        if (runGlobalHooks)
        {
            CombatState combatState = card.CombatState ?? card.Owner.Creature.CombatState;
            value = ModifyBlockInternal(combatState, card.Owner.Creature, value, __instance.Props, card, null, new List<AbstractModel>());
        }
        else if (!card.IsEnchantmentPreview)
        {
            value = MultiEnchantmentSupport.ApplyBlockEnchantments(card, value, __instance.Props);
        }

        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(ExtraDamageVar), nameof(ExtraDamageVar.UpdateCardPreview))]
    [HarmonyPrefix]
    private static bool ExtraDamageVarUpdateCardPreviewPrefix(
        ExtraDamageVar __instance,
        CardModel card,
        CardPreviewMode previewMode,
        Creature? target,
        bool runGlobalHooks)
    {
        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(card, __instance.BaseValue, ValueProp.Move, ModifyDamageHookType.Multiplicative);
        if (!card.IsEnchantmentPreview && MultiEnchantmentSupport.HasAnyEnchantments(card))
        {
            MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
        }

        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(OstyDamageVar), nameof(OstyDamageVar.UpdateCardPreview))]
    [HarmonyPrefix]
    private static bool OstyDamageVarUpdateCardPreviewPrefix(OstyDamageVar __instance, CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
    {
        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(card, __instance.BaseValue, __instance.Props, ModifyDamageHookType.All);
        if (!card.IsEnchantmentPreview && MultiEnchantmentSupport.HasAnyEnchantments(card))
        {
            MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
        }

        if (runGlobalHooks)
        {
            CombatState combatState = card.CombatState ?? card.Owner.Creature.CombatState;
            value = Hook.ModifyDamage(card.Owner.RunState, combatState, target, card.Owner.Osty, __instance.BaseValue, __instance.Props, card, ModifyDamageHookType.All, previewMode, out IEnumerable<AbstractModel> _);
        }

        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(NEnchantPreview), nameof(NEnchantPreview.Init))]
    [HarmonyPrefix]
    private static bool EnchantPreviewPrefix(NEnchantPreview __instance, CardModel card, EnchantmentModel canonicalEnchantment, int amount)
    {
        // Base-game source: NEnchantPreview.Init.
        // We need the mod-aware enchant path here so previews can show an added extra enchantment.
        canonicalEnchantment.AssertCanonical();
        AccessTools.Method(typeof(NEnchantPreview), "RemoveExistingCards")?.Invoke(__instance, null);

        Control before = __instance.GetNode<Control>("%Before");
        Control after = __instance.GetNode<Control>("%After");

        NPreviewCardHolder beforeHolder = NPreviewCardHolder.Create(NCard.Create(card), showHoverTips: true, scaleOnHover: false);
        before.AddChild(beforeHolder);
        beforeHolder.CardNode.UpdateVisuals(card.Pile?.Type ?? PileType.None, CardPreviewMode.Normal);

        CardModel previewCard = card.CardScope.CloneCard(card);
        MultiEnchantmentSupport.ApplyEnchantment(canonicalEnchantment.ToMutable(), previewCard, amount);
        previewCard.IsEnchantmentPreview = true;

        NPreviewCardHolder afterHolder = NPreviewCardHolder.Create(NCard.Create(previewCard), showHoverTips: true, scaleOnHover: false);
        after.AddChild(afterHolder);
        afterHolder.CardNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        return false;
    }

    [HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
    [HarmonyPrefix]
    private static void CardVisualsPrefix(NCard __instance, PileType pileType, CardPreviewMode previewMode)
    {
        MultiEnchantmentSupport.UpdateAdditionalEnchantmentPreviews(__instance, previewMode);
    }

    [HarmonyPatch(typeof(NCard), "UpdateEnchantmentVisuals")]
    [HarmonyPostfix]
    private static void CardEnchantTabsPostfix(NCard __instance)
    {
        MultiEnchantmentSupport.SyncExtraEnchantmentTabs(__instance);
    }

    [HarmonyPatch(typeof(NCard), nameof(NCard.OnReturnedFromPool))]
    [HarmonyPostfix]
    private static void CardReturnedPostfix(NCard __instance)
    {
        MultiEnchantmentSupport.ClearCardUi(__instance);
    }

    [HarmonyPatch(typeof(NCard), "UnsubscribeFromModel")]
    [HarmonyPostfix]
    private static void CardUnsubscribePostfix(NCard __instance)
    {
        MultiEnchantmentSupport.ClearCardUi(__instance);
    }

    [HarmonyPatch(typeof(CloneRestSiteOption), nameof(CloneRestSiteOption.OnSelect))]
    [HarmonyPrefix]
    private static bool CloneRestSiteOptionPrefix(CloneRestSiteOption __instance, ref Task<bool> __result)
    {
        // Base-game source: CloneRestSiteOption.OnSelect.
        // This override exists so cloned cards keep all compatible enchantments, not just the primary one.
        __result = CloneRestSiteOptionWithMultiEnchantments(__instance);
        return false;
    }

    [HarmonyPatch(typeof(ArchaicTooth), "GetTranscendenceTransformedCard")]
    [HarmonyPrefix]
    private static bool ArchaicToothPrefix(ArchaicTooth __instance, CardModel starterCard, ref CardModel __result)
    {
        // Base-game source: ArchaicTooth.GetTranscendenceTransformedCard.
        // Preserve the vanilla transform result, then copy over every compatible enchantment.
        __result = GetTranscendenceTransformedCardWithMultiEnchantments(__instance, starterCard);
        return false;
    }

    [HarmonyPatch(typeof(Claws), "CreateMaulFromOriginal")]
    [HarmonyPrefix]
    private static bool ClawsPrefix(Claws __instance, CardModel original, bool forPreview, ref CardModel __result)
    {
        // Base-game source: Claws.CreateMaulFromOriginal.
        // Preserve the vanilla Maul creation/upgrade rules, then copy compatible enchantments.
        __result = CreateMaulFromOriginalWithMultiEnchantments(__instance, original, forPreview);
        return false;
    }

    [HarmonyPatch(typeof(NCardEnchantVfx), nameof(NCardEnchantVfx._Ready))]
    [HarmonyPostfix]
    private static void CardEnchantVfxPostfix(NCardEnchantVfx __instance)
    {
        // Base-game source: NCardEnchantVfx._Ready.
        // Vanilla only renders one enchantment icon/label. For multi-enchanted cards we expand the
        // VFX to show the full current enchantment stack, with the existing template node reused for
        // the first entry and duplicated for the rest.
        CardModel? card = NCardEnchantVfxCardModelField?.GetValue(__instance) as CardModel;
        NCard? cardNode = NCardEnchantVfxCardNodeField?.GetValue(__instance) as NCard;
        TextureRect? icon = NCardEnchantVfxIconField?.GetValue(__instance) as TextureRect;
        // Base-game source: NCardEnchantVfx._Ready hides only the primary enchantment tab on the
        // embedded NCard. The mod's extra tabs need to be hidden too so only the VFX badge stack
        // remains visible during the enchant animation.
        MultiEnchantmentSupport.HideExtraEnchantmentTabs(cardNode);
        MultiEnchantmentSupport.SyncEnchantVfxBadges(__instance, card, icon);
    }

    [HarmonyPatch(typeof(NCardEnchantVfx), nameof(NCardEnchantVfx.Create))]
    [HarmonyPostfix]
    private static void CardEnchantVfxCreatePostfix(CardModel card, ref NCardEnchantVfx? __result)
    {
        // Snapshot the visible enchantment stack at VFX creation time so the animation does not
        // depend on later UI refreshes or card-node state during _Ready.
        MultiEnchantmentSupport.CaptureEnchantVfxSnapshot(__result, card);
    }

    private static DynamicVar GetCalculatedBaseVar(CalculatedVar calculatedVar)
    {
        if (CalculatedVarGetBaseVarMethod?.Invoke(calculatedVar, null) is DynamicVar baseVar)
        {
            return baseVar;
        }

        throw new InvalidOperationException("Failed to access CalculatedVar base value.");
    }

    private static async Task<bool> CloneRestSiteOptionWithMultiEnchantments(CloneRestSiteOption option)
    {
        Player owner = GetRestSiteOptionOwner(option);
        IEnumerable<CardModel> cloneCards = owner.Deck.Cards
            .Where(MultiEnchantmentSupport.HasEnchantment<Clone>)
            .ToList();
        List<CardPileAddResult> results = new();

        foreach (CardModel card in cloneCards)
        {
            CardModel clone = owner.RunState.CloneCard(card);
            results.Add(await CardPileCmd.Add(clone, PileType.Deck));
        }

        CardCmd.PreviewCardPileAdd(results, 1.2f, CardPreviewStyle.MessyLayout);
        return true;
    }

    private static Player GetRestSiteOptionOwner(RestSiteOption option)
    {
        return RestSiteOptionOwnerProperty?.GetValue(option) as Player
            ?? throw new InvalidOperationException("Failed to access RestSiteOption owner.");
    }

    private static CardModel GetTranscendenceTransformedCardWithMultiEnchantments(ArchaicTooth relic, CardModel starterCard)
    {
        if (ArchaicToothTranscendenceUpgradesProperty?.GetValue(null) is Dictionary<ModelId, CardModel> upgrades &&
            upgrades.TryGetValue(starterCard.Id, out CardModel? upgradedCard))
        {
            CardModel result = starterCard.Owner.RunState.CreateCard(upgradedCard, starterCard.Owner);
            if (starterCard.IsUpgraded)
            {
                CardCmd.Upgrade(result);
            }

            MultiEnchantmentSupport.CloneCompatibleEnchantments(starterCard, result);
            return result;
        }

        return relic.Owner.RunState.CreateCard<Doubt>(starterCard.Owner);
    }

    private static CardModel CreateMaulFromOriginalWithMultiEnchantments(Claws relic, CardModel original, bool forPreview)
    {
        CardModel result = forPreview ? ModelDb.Card<Maul>().ToMutable() : relic.Owner.RunState.CreateCard<Maul>(relic.Owner);
        if (original.IsUpgraded && result.IsUpgradable)
        {
            if (forPreview)
            {
                result.UpgradeInternal();
            }
            else
            {
                CardCmd.Upgrade(result);
            }
        }

        MultiEnchantmentSupport.CloneCompatibleEnchantments(original, result);
        return result;
    }

    private static decimal ModifyDamageInternal(
        IRunState runState,
        CombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        List<AbstractModel> modifiers)
    {
        decimal value = damage;

        if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Additive))
        {
            foreach (AbstractModel model in runState.IterateHookListeners(combatState))
            {
                decimal add = model.ModifyDamageAdditive(target, value, props, dealer, cardSource);
                value += add;
                if (add != 0m)
                {
                    modifiers.Add(model);
                }
            }
        }

        if (modifyDamageHookType.HasFlag(ModifyDamageHookType.Multiplicative))
        {
            foreach (AbstractModel model in runState.IterateHookListeners(combatState))
            {
                decimal multiply = model.ModifyDamageMultiplicative(target, value, props, dealer, cardSource);
                value *= multiply;
                if (multiply != 1m)
                {
                    modifiers.Add(model);
                }
            }
        }

        decimal damageCap = decimal.MaxValue;
        foreach (AbstractModel model in runState.IterateHookListeners(combatState))
        {
            decimal cap = model.ModifyDamageCap(target, props, dealer, cardSource);
            if (cap < damageCap)
            {
                damageCap = cap;
                if (value > cap)
                {
                    value = cap;
                    modifiers.Add(model);
                }
            }
        }

        return value;
    }

    private static decimal ModifyBlockInternal(
        CombatState combatState,
        Creature target,
        decimal block,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        List<AbstractModel> modifiers)
    {
        decimal value = block;

        foreach (AbstractModel model in combatState.IterateHookListeners())
        {
            decimal add = model.ModifyBlockAdditive(target, value, props, cardSource, cardPlay);
            value += add;
            if (add != 0m)
            {
                modifiers.Add(model);
            }
        }

        foreach (AbstractModel model in combatState.IterateHookListeners())
        {
            decimal multiply = model.ModifyBlockMultiplicative(target, value, props, cardSource, cardPlay);
            value *= multiply;
            if (multiply != 1m)
            {
                modifiers.Add(model);
            }
        }

        return Math.Max(0m, value);
    }

    private static async Task SetupPlayerTurnWithMultiEnchantments(
        CombatManager combatManager,
        Player player,
        HookPlayerChoiceContext playerChoiceContext)
    {
        // Base-game source: CombatManager.SetupPlayerTurn.
        // Keep this method in lockstep with the base game.
        // The only intentional behavior change is checking all enchantments for bottom-of-draw-pile.
        CombatState state = combatManager.DebugOnlyGetState()
            ?? throw new InvalidOperationException("CombatManager state was null during SetupPlayerTurn.");

        if (player.Creature.IsDead)
        {
            return;
        }

        if (Hook.ShouldPlayerResetEnergy(state, player))
        {
            SfxCmd.Play("event:/sfx/ui/gain_energy");
            player.PlayerCombatState.ResetEnergy();
        }
        else
        {
            player.PlayerCombatState.AddMaxEnergyToCurrent();
        }

        await Hook.AfterEnergyReset(state, player);
        await Hook.BeforeHandDraw(state, player, playerChoiceContext);
        decimal handDraw = Hook.ModifyHandDraw(state, player, 5m, out IEnumerable<AbstractModel> modifiers);
        await Hook.AfterModifyingHandDraw(state, modifiers);

        if (state.RoundNumber == 1)
        {
            CardPile pile = PileType.Draw.GetPile(player);
            List<CardModel> bottomCards = pile.Cards
                .Where(MultiEnchantmentSupport.ShouldStartAtBottomOfDrawPile)
                .ToList();

            foreach (CardModel card in bottomCards)
            {
                pile.MoveToBottomInternal(card);
            }

            List<CardModel> innateCards = pile.Cards
                .Where(static card => card.Keywords.Contains(CardKeyword.Innate))
                .Except(bottomCards)
                .ToList();

            foreach (CardModel card in innateCards)
            {
                pile.MoveToTopInternal(card);
            }

            handDraw = Math.Max(handDraw, innateCards.Count);
            handDraw = Math.Min(handDraw, 10m);
        }

        await CardPileCmd.Draw(playerChoiceContext, handDraw, player, fromHandDraw: true);
        await Hook.AfterPlayerTurnStart(state, playerChoiceContext, player);
    }
}

[HarmonyPatch]
internal static class MultiEnchantmentMultiplayerGroupingPatches
{
    private static readonly Type? CardGroupKeyType =
        AccessTools.Inner(typeof(NMultiplayerPlayerExpandedState), "CardGroupKey");
    private static readonly FieldInfo? CardGroupKeyCardField =
        CardGroupKeyType == null ? null : AccessTools.Field(CardGroupKeyType, "_card");

    [HarmonyTargetMethod]
    private static MethodBase? CardGroupKeyEqualsTarget()
    {
        return CardGroupKeyType == null ? null : AccessTools.Method(CardGroupKeyType, nameof(object.Equals));
    }

    [HarmonyPrefix]
    private static bool CardGroupKeyEqualsPrefix(object __instance, object? obj, ref bool __result)
    {
        // Base-game source: NMultiplayerPlayerExpandedState.CardGroupKey.Equals.
        // Card grouping in the multiplayer deck view needs the full enchantment signature so cards
        // with different extra enchantments or enchantment state do not collapse into one row.
        if (obj == null || CardGroupKeyType == null || obj.GetType() != CardGroupKeyType)
        {
            __result = false;
            return false;
        }

        CardModel left = GetCardFromGroupKey(__instance);
        CardModel right = GetCardFromGroupKey(obj);
        __result = left.Id.Equals(right.Id) &&
                   left.CurrentUpgradeLevel == right.CurrentUpgradeLevel &&
                   MultiEnchantmentSupport.HaveSameEnchantments(left, right);
        return false;
    }

    [HarmonyPatch]
    private static class CardGroupKeyHashCodePatch
    {
        [HarmonyTargetMethod]
        private static MethodBase? TargetMethod()
        {
            return CardGroupKeyType == null ? null : AccessTools.Method(CardGroupKeyType, nameof(object.GetHashCode));
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance, ref int __result)
        {
            // Keep hash inputs aligned with CardGroupKeyEqualsPrefix above.
            CardModel card = GetCardFromGroupKey(__instance);
            __result = HashCode.Combine(
                card.Id,
                card.CurrentUpgradeLevel,
                MultiEnchantmentSupport.GetEnchantmentsHashCode(card));
            return false;
        }
    }

    private static CardModel GetCardFromGroupKey(object groupKey)
    {
        return CardGroupKeyCardField?.GetValue(groupKey) as CardModel
            ?? throw new InvalidOperationException("Failed to read multiplayer card group key.");
    }
}

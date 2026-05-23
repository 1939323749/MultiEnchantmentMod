using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
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
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

[HarmonyPatch]
internal static class MultiEnchantmentPatches
{
    private static readonly MethodInfo? CalculatedVarGetBaseVarMethod =
        AccessTools.Method(typeof(CalculatedVar), "GetBaseVar");
    private static readonly PropertyInfo? RestSiteOptionOwnerProperty =
        AccessTools.Property(typeof(RestSiteOption), "Owner");
    private static readonly FieldInfo? NCardEnchantVfxCardModelField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_cardModel");
    private static readonly FieldInfo? NCardEnchantVfxCardNodeField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_cardNode");
    private static readonly FieldInfo? NCardEnchantVfxIconField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_enchantmentIcon");
    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.CanEnchant))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void CanEnchantPostfix(EnchantmentModel __instance, CardModel card, ref bool __result)
    {
        // Base-game source: EnchantmentModel.CanEnchant.
        // Run as a postfix so other mods' prefixes / transpilers on CanEnchant can take effect:
        //   - If vanilla allowed it, tighten with the mod's PassesAdditionalCanEnchantRules.
        //   - If vanilla rejected it ONLY because of the "same enchantment type already present"
        //     clause, re-allow when the stack behavior permits duplicates. All other vanilla
        //     rejection reasons are re-checked to make sure we don't override unrelated rejections
        //     from vanilla or upstream patches.

        if (__result)
        {
            if (!MultiEnchantmentStackSupport.PassesAdditionalCanEnchantRules(__instance, card))
            {
                __result = false;
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] CanEnchant postfix tightening. " +
                    $"Card={card.Id} Enchantment={__instance.Id} Result=False Reason=AdditionalRules");
                return;
            }

            // Vanilla CanEnchant only inspects card.Enchantment (the primary slot) — it cannot
            // see the mod's extra enchantments. So a DisallowDuplicate type that already exists
            // ONLY as an extra (with no primary) slips past vanilla's "same exists" check.
            // Tighten here: if the type already exists anywhere on the card and the stack policy
            // doesn't permit merging, reject. Stack-allowing types (MergeAmount /
            // DuplicateInstance / ExistenceStack) skip this branch because CanStackOnto is true.
            if (!MultiEnchantmentStackSupport.CanApply(card, __instance.GetType()) &&
                !MultiEnchantmentStackSupport.CanStackOnto(card, __instance.GetType()))
            {
                __result = false;
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] CanEnchant postfix tightening. " +
                    $"Card={card.Id} Enchantment={__instance.Id} Result=False Reason=DuplicateExtra");
            }
            return;
        }

        // Re-verify the non-stack vanilla rejection reasons; if any of them still fail, leave
        // __result alone so unrelated rejections (from vanilla or other patches) survive.
        CardType type = card.Type;
        if (type is CardType.Status or CardType.Curse or CardType.Quest) return;
        if (!__instance.CanEnchantCardType(type)) return;
        CardPile? pile = card.Pile;
        if (pile != null && pile.Type == PileType.Deck && card.Keywords.Contains(CardKeyword.Unplayable)) return;
        if (!MultiEnchantmentStackSupport.PassesAdditionalCanEnchantRules(__instance, card)) return;

        // All other vanilla checks pass. The only remaining reason vanilla could have rejected is
        // the "same enchantment already exists" clause — re-enable iff mod's stack policy permits.
        bool relaxed = MultiEnchantmentStackSupport.CanApply(card, __instance.GetType());
        if (relaxed)
        {
            __result = true;
            MultiEnchantmentMod.Logger.Info(
                $"[MultiEnchantment] CanEnchant postfix re-allowed via stack policy. " +
                $"Card={card.Id} Enchantment={__instance.Id}");
        }
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Enchant), new[] { typeof(EnchantmentModel), typeof(CardModel), typeof(decimal) })]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool EnchantPrefix(EnchantmentModel enchantment, CardModel card, decimal amount, ref EnchantmentModel? __result)
    {
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting CardCmd.Enchant. " +
            $"Card={card.Id} Enchantment={enchantment.Id} Amount={amount}");
        try
        {
            __result = MultiEnchantmentSupport.ApplyEnchantment(enchantment, card, amount);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CardCmd.Enchant failed for Card={card.Id} Enchantment={enchantment.Id}. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.ClearEnchantment))]
    [HarmonyPrefix]
    private static void ClearEnchantmentPrefix(CardModel card)
    {
        MultiEnchantmentSupport.ClearAdditionalEnchantments(card, triggerChanged: card.Enchantment == null);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    [HarmonyPostfix]
    private static void BeforeCombatStartPostfix(ICombatState combatState)
    {
        MultiEnchantmentScopeSupport.OnCombatStarted(combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
    [HarmonyPostfix]
    private static void AfterCombatEndPostfix(IRunState runState, ICombatState? combatState)
    {
        MultiEnchantmentScopeSupport.OnCombatEnded(runState, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardEnteredCombat))]
    [HarmonyPostfix]
    private static void AfterCardEnteredCombatPostfix(ICombatState combatState, CardModel card)
    {
        // Fires OnCombatStart for cards that join combat AFTER BeforeCombatStart's initial
        // sweep (relic-copies, Madness-generated cards, etc.). The scope support method gates
        // on whether the sweep has completed, so deck-setup additions stay handled by the
        // sweep itself — see OnCardEnteredCombat for the timing rationale.
        MultiEnchantmentScopeSupport.OnCardEnteredCombat(combatState, card);

        // Phase 3a T3a.5: separate lifecycle that fires on every entry, including deck-setup
        // sweep. OnCardEnteredCombat lifecycle and OnCombatStart lifecycle are distinct — the
        // former is the per-event "card just landed in combat" signal, the latter is the
        // once-per-combat-per-card "initialize" signal. IsActive is enforced inside Dispatch.
        MultiEnchantmentScopeSupport.DispatchOnCardEnteredCombatForCard(card);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterTurnEnd))]
    [HarmonyPostfix]
    private static void AfterTurnEndPostfix(ICombatState combatState, CombatSide side, IEnumerable<Creature> participants)
    {
        // Base-game source: Hook.AfterTurnEnd(ICombatState, CombatSide, IEnumerable<Creature>)
        // The parameter type must be spelled CombatSide (MegaCrit.Sts2.Core.Combat) — never
        // just Side. The file's `using Godot;` makes the unqualified name resolve to
        // Godot.Side (the UI margin enum Left/Top/Right/Bottom), which leaves Harmony with a
        // parameter-type mismatch, so UntilTurnEnds and LingerForTurns(N) silently never fire
        // at turn end.
        if (side == CombatSide.Player)
        {
            MultiEnchantmentScopeSupport.OnPlayerTurnEnded(combatState);

            // Fan the player-scoped AfterPlayerTurnEnd activation trigger out to every card-owned
            // enchantment in PlayerCombatState. Combined with the v2 MaxActivations / RemoveWhen
            // surface this lets authors express "expire after 2 turn endings" cleanly.
            foreach (Player player in (combatState as CombatState)?.Players
                ?? Enumerable.Empty<Player>())
            {
                if (player.IsActiveForHooks && player.PlayerCombatState != null)
                {
                    MultiEnchantmentScopeSupport.DispatchActivationTriggerForPlayer(
                        player, ActivationTrigger.AfterPlayerTurnEnd);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
    [HarmonyPostfix]
    private static void HookAfterCardPlayedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // Goopy already drives its own AfterCardPlayed counter via HandleGoopyAfterCardPlayed.
        // For the general v2 surface, fan the AfterCardPlayed activation trigger out to every
        // enchantment on the played card so MaxActivations(N, AfterCardPlayed) / RemoveWhen
        // checks can count it.
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForCard(
            cardPlay?.Card, ActivationTrigger.AfterCardPlayed);

        // Phase 3a T3a.1: fan the OnCardPlayed lifecycle out to active enchantments on the
        // played card. Distinct from the activation-trigger fan-out above: that one drives
        // scope counters (MaxActivations / RemoveWhen), this one delivers an author-facing
        // event for arbitrary side-effects.
        MultiEnchantmentScopeSupport.DispatchOnCardPlayedForCard(cardPlay?.Card);

        // Phase 4: broadcast OnAnyCardPlayed to every enchantment in combat that opted in.
        MultiEnchantmentScopeSupport.DispatchOnAnyCardPlayedBroadcast(cardPlay?.Card, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
    [HarmonyPostfix]
    private static void HookAfterCardDrawnPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForCard(
            card, ActivationTrigger.AfterCardDrawn);

        // Phase 3a T3a.2: OnCardDrawn lifecycle for active enchantments.
        MultiEnchantmentScopeSupport.DispatchOnCardDrawnForCard(card);

        // Phase 4: broadcast OnAnyCardDrawn to every enchantment in combat that opted in.
        MultiEnchantmentScopeSupport.DispatchOnAnyCardDrawnBroadcast(card, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
    [HarmonyPostfix]
    private static void HookAfterCardExhaustedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool causedByEthereal)
    {
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForCard(
            card, ActivationTrigger.AfterCardExhausted);

        // Phase 3a T3a.3: OnCardExhausted lifecycle for active enchantments.
        MultiEnchantmentScopeSupport.DispatchOnCardExhaustedForCard(card);

        // Phase 4: broadcast OnAnyCardExhausted to every enchantment in combat that opted in.
        MultiEnchantmentScopeSupport.DispatchOnAnyCardExhaustedBroadcast(card, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDiscarded))]
    [HarmonyPostfix]
    private static void HookAfterCardDiscardedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card)
    {
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForCard(
            card, ActivationTrigger.AfterCardDiscarded);

        // Phase 3a T3a.4: OnCardDiscarded lifecycle for active enchantments.
        MultiEnchantmentScopeSupport.DispatchOnCardDiscardedForCard(card);

        // Phase 4: broadcast OnAnyCardDiscarded to every enchantment in combat that opted in.
        MultiEnchantmentScopeSupport.DispatchOnAnyCardDiscardedBroadcast(card, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
    [HarmonyPostfix]
    private static void HookAfterPlayerTurnStartPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForPlayer(
            player, ActivationTrigger.AfterPlayerTurnStart);
    }

    // === Phase 3c — pile / guard / block bridges ============================================

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardChangedPiles))]
    [HarmonyPostfix]
    private static void HookAfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? clonedBy)
    {
        MultiEnchantmentScopeSupport.DispatchOnCardChangedPilesForCard(card, oldPile, clonedBy);
    }

    // vanilla doesn't expose a per-card AfterCardRetained Hook entry point — only AfterFlush
    // which delivers the full retainedCards collection. Fan out from there.
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterFlush))]
    [HarmonyPostfix]
    private static void HookAfterFlushRetainedPostfix(
        ICombatState combatState,
        Player player,
        PlayerChoiceContext playerChoiceContext,
        IReadOnlyCollection<CardModel> flushedCards,
        IReadOnlyCollection<CardModel> retainedCards)
    {
        if (retainedCards == null)
        {
            return;
        }
        foreach (CardModel card in retainedCards)
        {
            MultiEnchantmentScopeSupport.DispatchOnCardRetainedForCard(card);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeBlockGained))]
    [HarmonyPostfix]
    private static void HookBeforeBlockGainedPostfix(ICombatState combatState, Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        BlockGainContext context = new(creature, amount, cardSource);
        MultiEnchantmentScopeSupport.DispatchOnBeforeBlockGainedForPlayer(context);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockGained))]
    [HarmonyPostfix]
    private static void HookAfterBlockGainedPostfix(ICombatState combatState, Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        BlockGainContext context = new(creature, amount, cardSource);
        MultiEnchantmentScopeSupport.DispatchOnBlockGainedForPlayer(context);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldDie))]
    [HarmonyPostfix]
    private static void HookShouldDiePostfix(Creature creature, ref bool __result)
    {
        // Guard semantics: vanilla returns true when nothing prevented death. If it already
        // returned false (some other listener vetoed), don't second-guess. Otherwise, ask the
        // mod's active enchantments — any single false vetoes.
        if (!__result)
        {
            return;
        }
        if (!MultiEnchantmentScopeSupport.DispatchOnShouldDieForCreature(creature))
        {
            __result = false;
        }
    }

    // === Phase 3b — combat-flow bridges =====================================================

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnStart))]
    [HarmonyPostfix]
    private static void HookAfterSideTurnStartPostfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        // Phase 3b T3b.1: bridge to OnSideTurnStart lifecycle. Vanilla fires both for player and
        // enemy turns; handlers can branch on the side parameter. The existing OnTurnStart
        // lifecycle remains player-only for backward compatibility.
        MultiEnchantmentScopeSupport.DispatchOnSideTurnStart(combatState, side);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
    [HarmonyPostfix]
    private static void HookBeforeSideTurnStartPostfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        // Phase 3b T3b.2: bridge to OnBeforeSideTurnStart lifecycle.
        MultiEnchantmentScopeSupport.DispatchOnBeforeSideTurnStart(combatState, side);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeAttack))]
    [HarmonyPostfix]
    private static void HookBeforeAttackPostfix(CombatState combatState, AttackCommand command)
    {
        // Phase 3b T3b.3: bridge to OnBeforeAttack lifecycle. AttackCommand exposes Attacker,
        // CardSource, Results — handlers filter as needed.
        MultiEnchantmentScopeSupport.DispatchOnBeforeAttack(combatState, command);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterAttack))]
    [HarmonyPostfix]
    private static void HookAfterAttackPostfix(CombatState combatState, AttackCommand command)
    {
        // Phase 3b T3b.4: bridge to OnAfterAttack lifecycle.
        MultiEnchantmentScopeSupport.DispatchOnAfterAttack(combatState, command);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
    [HarmonyPostfix]
    private static void HookAfterDamageReceivedPostfix(
        PlayerChoiceContext choiceContext,
        IRunState runState,
        ICombatState? combatState,
        Creature target,
        DamageResult result,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        // Trigger fires when the OWNER of an enchanted card takes damage. Filter to player
        // owners so an enemy taking damage from an attack doesn't burn through MaxActivations
        // counters on player-side enchantments.
        if (target?.Player == null)
        {
            return;
        }

        MultiEnchantmentScopeSupport.DispatchActivationTriggerForPlayer(
            target.Player, ActivationTrigger.AfterDamageReceived);

        // Phase 3a T3a.6: deliver an author-facing OnAfterDamageReceived lifecycle in addition
        // to the scope-counter activation trigger. Build a single context bundle here so every
        // enchantment sees the same payload (target / damage breakdown / dealer / card source).
        DamageReceivedContext context = new(target, result, dealer, cardSource);
        MultiEnchantmentScopeSupport.DispatchOnAfterDamageReceivedForPlayer(target.Player, context);
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.ClearEnchantment))]
    [HarmonyPostfix]
    private static void ClearEnchantmentPostfix(CardModel card)
    {
        MultiEnchantmentStackSupport.RefreshDerivedState(card);
    }

    [HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
    [HarmonyPostfix]
    private static void MutableClonePostfix(AbstractModel __instance, AbstractModel __result)
    {
        if (__instance is EnchantmentModel sourceEnchantment && __result is EnchantmentModel cloneEnchantment)
        {
            MultiEnchantmentStackSupport.CloneRuntimeProps(sourceEnchantment, cloneEnchantment);
        }

        if (__instance is CardModel source && __result is CardModel clone)
        {
            MultiEnchantmentSupport.CloneAdditionalEnchantments(source, clone);
            if (MultiEnchantmentSupport.NormalizeCardEnchantmentStacks(clone))
            {
                clone.FinalizeUpgradeInternal();
                MultiEnchantmentStackSupport.RefreshDerivedState(clone);
            }
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.GetEnchantedReplayCount))]
    [HarmonyPostfix]
    private static void ReplayCountPostfix(CardModel __instance, ref int __result)
    {
        // Stay out of the way when the mod has nothing to add: vanilla already computed
        // primary.EnchantPlayCount(BaseReplayCount), which equals the mod's result whenever there
        // are no extras and no merged-slice metadata. Keeping this as a postfix (instead of a
        // prefix-replace) lets other mods' prefixes / transpilers on GetEnchantedReplayCount run
        // normally.
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(__instance))
        {
            return;
        }

        __result = MultiEnchantmentSupport.GetReplayCount(__instance);
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] CardModel.GetEnchantedReplayCount postfix. " +
            $"Card={__instance.Id} Result={__result}");
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
    [HarmonyPostfix]
    private static void ToSerializablePostfix(CardModel __instance, ref SerializableCard __result)
    {
        MultiEnchantmentSupport.SerializeAdditionalEnchantments(__instance, __result);
        MultiEnchantmentSaveSidecar.CaptureCard(__instance, __result);
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable))]
    [HarmonyPostfix]
    private static void FromSerializablePostfix(SerializableCard save, ref CardModel __result)
    {
        MultiEnchantmentSaveSidecar.RestoreInto(save);
        MultiEnchantmentSaveSidecar.CaptureSerializableCard(save);
        MultiEnchantmentSupport.DeserializeAdditionalEnchantments(save, __result);
        if (MultiEnchantmentSupport.NormalizeCardEnchantmentStacks(__result))
        {
            __result.FinalizeUpgradeInternal();
            MultiEnchantmentStackSupport.RefreshDerivedState(__result);
        }

        // Now that the card has been fully reconstructed (primary enchantment attached via
        // vanilla, extras re-attached by DeserializeAdditionalEnchantments, Props restored by
        // EnchantmentFromSerializablePostfix's RestoreSerializedProps), fire OnRestored on
        // every enchantment so authors can rebuild runtime caches that don't survive the
        // serialization boundary. OnApplied is intentionally NOT fired here — that's reserved
        // for "freshly attached, never before" semantics.
        MultiEnchantmentScopeSupport.DispatchOnRestoredForCard(__result);
    }

    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.ToSerializable))]
    [HarmonyPostfix]
    private static void EnchantmentToSerializablePostfix(EnchantmentModel __instance, ref SerializableEnchantment __result)
    {
        MultiEnchantmentStackSupport.WriteSerializedProps(__instance, ref __result);
        // Capture in-memory ScopeRuntimeState (MaxActivations / LingerForTurns counters) so the
        // receiving side / loaded save can rehydrate them. See WriteScopeStateToSerializableProps
        // for why the Scope kind itself is NOT serialized.
        MultiEnchantmentScopeSupport.WriteScopeStateToSerializableProps(__instance, ref __result);
        MultiEnchantmentSaveSidecar.CaptureEnchantment(__instance, __result);
    }

    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable))]
    [HarmonyPostfix]
    private static void EnchantmentFromSerializablePostfix(SerializableEnchantment save, ref EnchantmentModel __result)
    {
        MultiEnchantmentSaveSidecar.RestoreInto(save);
        MultiEnchantmentSaveSidecar.CaptureSerializableEnchantment(save);
        MultiEnchantmentStackSupport.RestoreSerializedProps(save, __result);
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), new[] { typeof(SerializableRun), typeof(bool) })]
    [HarmonyPrefix]
    private static void SaveRunPrefix(SerializableRun save)
    {
        MultiEnchantmentSaveSidecar.PrepareRunForDisk(save);
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.LoadRunSave))]
    [HarmonyPostfix]
    private static void LoadRunSavePostfix(ReadSaveResult<SerializableRun> __result)
    {
        if (__result is { Success: true, SaveData: { } save })
        {
            MultiEnchantmentSaveSidecar.Reload();
            MultiEnchantmentSaveSidecar.PrepareRunForDisk(save);
        }
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.LoadMultiplayerRunSave))]
    [HarmonyPostfix]
    private static void LoadMultiplayerRunSavePostfix(ReadSaveResult<SerializableRun> __result)
    {
        if (__result is { Success: true, SaveData: { } save })
        {
            MultiEnchantmentSaveSidecar.Reload();
            MultiEnchantmentSaveSidecar.PrepareRunForDisk(save);
        }
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

    [HarmonyPatch(typeof(CombatManager), "SetupPlayerTurn", new[] { typeof(Player), typeof(HookPlayerChoiceContext) })]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool SetupPlayerTurnPrefix(
        CombatManager __instance,
        Player player,
        HookPlayerChoiceContext playerChoiceContext,
        ref Task __result)
    {
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting CombatManager.SetupPlayerTurn. " +
            $"Player={player.NetId}");
        try
        {
            __result = SetupPlayerTurnWithMultiEnchantments(__instance, player, playerChoiceContext);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CombatManager.SetupPlayerTurn failed for Player={player.NetId}. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyBlock))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool HookModifyBlockPrefix(
        ICombatState combatState,
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

        // Fast path: when the card has no extra enchantments and no multi-slice merged primary,
        // vanilla and the mod produce identical results. Defer to vanilla so other mods' patches
        // on Hook.ModifyBlock still take effect.
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(cardSource))
        {
            return true;
        }

        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Hook.ModifyBlock. " +
            $"CardSource={cardSource?.Id} Block={block}");
        try
        {
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

            // Layer ModifyDynamicVar("block", ...) contributions on top of the legacy
            // EnchantBlock*/Hook listener pipeline. UpdateCardPreview's display path also runs this
            // — both paths must agree, otherwise the rendered block ≠ block actually granted.
            value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(cardSource, "block", value);

            modifiers = modifyingModels;
            __result = Math.Max(0m, value);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Hook.ModifyBlock failed. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool HookModifyDamagePrefix(
        IRunState runState,
        ICombatState? combatState,
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

        // Fast path: when the card has no extras and no multi-slice primary, vanilla's single
        // primary-enchantment call equals the mod's per-slice loop. Defer to vanilla so other
        // mods' patches on Hook.ModifyDamage still take effect.
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(cardSource))
        {
            return true;
        }

        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Hook.ModifyDamage. " +
            $"CardSource={cardSource?.Id} Damage={damage} PreviewMode={previewMode}");
        try
        {
            decimal value = ApplyCardDamageEnchantments(cardSource, damage, props, modifyDamageHookType);
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
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Hook.ModifyDamage failed. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    // NOTE: Hook.AfterCardPlayed is intentionally NOT patched. The mod previously had a
    // prefix-replace here so it could inject extra-enchantment OnPlay calls, but that work moved
    // into CardModel.OnPlayWrapper after extra enchantments became visible via the
    // CombatState.IterateHookListeners postfix. Leaving the original prefix in place would just
    // short-circuit any other mod's prefix/transpiler on this hook without doing anything
    // different from vanilla, so it was removed for compatibility.

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.OnPlayWrapper))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool CardModelOnPlayWrapperPrefix(
        CardModel __instance,
        PlayerChoiceContext choiceContext,
        Creature? target,
        bool isAutoPlay,
        ResourceInfo resources,
        bool skipCardPileVisuals,
        ref Task __result)
    {
        // Base-game source: CardModel.OnPlayWrapper.
        // Keep the original control flow, but execute extra enchantments in the same phase as the
        // primary enchantment OnPlay instead of the later AfterCardPlayed hook sweep.
        bool shouldUseMultiLogic = MultiEnchantmentSupport.RequiresOnPlayWrapperMultiEnchantmentLogic(__instance);
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting CardModel.OnPlayWrapper. " +
            $"Card={__instance.Id} AutoPlay={isAutoPlay} UseMultiLogic={shouldUseMultiLogic} " +
            $"SkipVisuals={skipCardPileVisuals}");

        if (!shouldUseMultiLogic)
        {
            return true;
        }

        try
        {
            __result = MultiEnchantmentSupport.OnPlayWrapperWithMultiEnchantments(
                __instance,
                choiceContext,
                target,
                isAutoPlay,
                resources,
                skipCardPileVisuals);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CardModel.OnPlayWrapper failed for Card={__instance.Id}. " +
                $"Falling back to base-game OnPlayWrapper. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(Goopy), nameof(Goopy.AfterCardPlayed))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool GoopyAfterCardPlayedPrefix(Goopy __instance, PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task __result)
    {
        // Base-game source: Goopy.AfterCardPlayed.
        // Vanilla assumes Goopy is always the primary deck enchantment. In multi-enchantment combat
        // that is no longer guaranteed, and a mid-combat-added Goopy may not exist on DeckVersion
        // unless the mod mirrors it. Resolve the matching Goopy instance explicitly.
        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting Goopy.AfterCardPlayed. " +
            $"GoopyCard={__instance.Card?.Id} PlayedCard={cardPlay.Card?.Id}");
        try
        {
            __result = MultiEnchantmentSupport.HandleGoopyAfterCardPlayed(__instance, choiceContext, cardPlay);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Goopy.AfterCardPlayed failed. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.RecalculateCardValues))]
    [HarmonyPostfix]
    private static void RecalculateCardValuesPostfix(PlayerCombatState __instance)
    {
        // Snapshot AllCards: RecalculateAdditionalEnchantments calls EnchantmentModel.RecalculateValues
        // on each enchantment. Vanilla is read-only there, but a user-defined override could call
        // into mod APIs that mutate AllCards. Defensive snapshot keeps this batch loop safe.
        foreach (CardModel card in __instance.AllCards.ToList())
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
    [HarmonyPriority(Priority.Low)]
    private static bool DamageVarUpdateCardPreviewPrefix(DamageVar __instance, CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
    {
        // Fast path: card has no mod-specific enchant state, vanilla preview is equivalent.
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting DamageVar.UpdateCardPreview. " +
            $"Card={card.Id} BaseValue={__instance.BaseValue} PreviewMode={previewMode}");
        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(card, __instance.BaseValue, __instance.Props, ModifyDamageHookType.All);
        if (!card.IsEnchantmentPreview)
        {
            if (MultiEnchantmentSupport.HasAnyEnchantments(card))
            {
                MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
            }
            else
            {
                MultiEnchantmentSupport.ResetEnchantedValue(__instance);
            }
        }

        if (runGlobalHooks)
        {
            // Hook.ModifyDamage's prefix applies legacy damage enchantments and
            // ModifyDynamicVar("damage") before global hooks/caps. Keep passing BaseValue here;
            // passing the already-enchanted local value would apply card damage twice.
            value = Hook.ModifyDamage(card.Owner.RunState, card.CombatState, target, card.Owner.Creature, __instance.BaseValue, __instance.Props, card, ModifyDamageHookType.All, previewMode, out IEnumerable<AbstractModel> _);
        }
        else
        {
            value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
        }

        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(BlockVar), nameof(BlockVar.UpdateCardPreview))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool BlockVarUpdateCardPreviewPrefix(BlockVar __instance, CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
    {
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting BlockVar.UpdateCardPreview. " +
            $"Card={card.Id} BaseValue={__instance.BaseValue} PreviewMode={previewMode}");
        decimal value = MultiEnchantmentSupport.ApplyBlockEnchantments(card, __instance.BaseValue, __instance.Props);
        if (!card.IsEnchantmentPreview)
        {
            if (MultiEnchantmentSupport.HasAnyEnchantments(card))
            {
                MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
            }
            else
            {
                MultiEnchantmentSupport.ResetEnchantedValue(__instance);
            }
        }

        if (runGlobalHooks)
        {
            // Hook.ModifyBlock's prefix already chains ApplyDynamicVarEnchantments — see the
            // matching comment on DamageVar above.
            value = Hook.ModifyBlock(card.CombatState!, card.Owner.Creature, __instance.BaseValue, __instance.Props, card, null, out IEnumerable<AbstractModel> _);
        }
        else
        {
            value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
        }

        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(CalculatedDamageVar), nameof(CalculatedDamageVar.UpdateCardPreview))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
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
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting CalculatedDamageVar.UpdateCardPreview. " +
            $"Card={card.Id} PreviewMode={previewMode}");
        try
        {
            DynamicVar baseVar = GetCalculatedBaseVar(__instance);
            // Base stage: only legacy EnchantDamage* virtuals on the BASE value (matches what
            // those virtuals were designed to modify — e.g. Sharp adjusts base damage). The new
            // ModifyDynamicVar chain runs on the RESULT after Calculate + listeners, mirroring
            // CalculatedBlockVar so authors see consistent application semantics across both.
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
            else
            {
                MultiEnchantmentSupport.ResetEnchantedValue(__instance);
            }

            decimal value = __instance.Calculate(target);
            if (runGlobalHooks)
            {
                ICombatState? combatState = card.CombatState ?? card.Owner.Creature.CombatState;
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

            value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
            __instance.PreviewValue = Math.Max(value, 0m);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CalculatedDamageVar.UpdateCardPreview failed for Card={card.Id}. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(CalculatedBlockVar), nameof(CalculatedBlockVar.UpdateCardPreview))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
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
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        MultiEnchantmentMod.Logger.Info(
            $"[MultiEnchantment] Intercepting CalculatedBlockVar.UpdateCardPreview. " +
            $"Card={card.Id} PreviewMode={previewMode}");
        try
        {
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
            else
            {
                MultiEnchantmentSupport.ResetEnchantedValue(__instance);
            }

            decimal value = __instance.Calculate(target);
            if (runGlobalHooks)
            {
                ICombatState? combatState = card.CombatState ?? card.Owner.Creature.CombatState;
                value = ModifyBlockInternal(combatState, card.Owner.Creature, value, __instance.Props, card, null, new List<AbstractModel>());
            }

            value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
            __instance.PreviewValue = value;
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CalculatedBlockVar.UpdateCardPreview failed for Card={card.Id}. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(ExtraDamageVar), nameof(ExtraDamageVar.UpdateCardPreview))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool ExtraDamageVarUpdateCardPreviewPrefix(
        ExtraDamageVar __instance,
        CardModel card,
        CardPreviewMode previewMode,
        Creature? target,
        bool runGlobalHooks)
    {
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(card, __instance.BaseValue, ValueProp.Move, ModifyDamageHookType.Multiplicative);
        if (!card.IsEnchantmentPreview)
        {
            if (MultiEnchantmentSupport.HasAnyEnchantments(card))
            {
                MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
            }
            else
            {
                MultiEnchantmentSupport.ResetEnchantedValue(__instance);
            }
        }

        value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
        __instance.PreviewValue = value;
        return false;
    }

    [HarmonyPatch(typeof(OstyDamageVar), nameof(OstyDamageVar.UpdateCardPreview))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool OstyDamageVarUpdateCardPreviewPrefix(OstyDamageVar __instance, CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
    {
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(card, __instance.BaseValue, __instance.Props, ModifyDamageHookType.All);
        if (!card.IsEnchantmentPreview)
        {
            if (MultiEnchantmentSupport.HasAnyEnchantments(card))
            {
                MultiEnchantmentSupport.SetEnchantedValue(__instance, value);
            }
            else
            {
                MultiEnchantmentSupport.ResetEnchantedValue(__instance);
            }
        }

        if (runGlobalHooks)
        {
            // Hook.ModifyDamage's prefix applies legacy damage enchantments and
            // ModifyDynamicVar("damage") before global hooks/caps. Keep passing BaseValue here;
            // passing the already-enchanted local value would apply card damage twice.
            ICombatState? combatState = card.CombatState ?? card.Owner.Creature.CombatState;
            value = Hook.ModifyDamage(card.Owner.RunState, combatState, target, card.Owner.Osty, __instance.BaseValue, __instance.Props, card, ModifyDamageHookType.All, previewMode, out IEnumerable<AbstractModel> _);
        }
        else
        {
            value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
        }

        __instance.PreviewValue = value;
        return false;
    }

    // Postfix on the base DynamicVar.UpdateCardPreview — fires for "plain" DynamicVar instances
    // whose runtime type doesn't override UpdateCardPreview (e.g. Glam.DynamicVars["Times"]). The
    // 6 patched subtypes (DamageVar, BlockVar, Calculated{Damage,Block}Var, ExtraDamageVar,
    // OstyDamageVar) have their own override patches above and bypass this postfix. Custom
    // DynamicVar subtypes from third-party mods that override UpdateCardPreview are not picked up
    // here — those authors should patch / extend their own var class.
    [HarmonyPatch(typeof(DynamicVar), nameof(DynamicVar.UpdateCardPreview))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void DynamicVarUpdateCardPreviewPostfix(DynamicVar __instance, CardModel card)
    {
        if (string.IsNullOrEmpty(__instance.Name))
        {
            return;
        }

        if (!MultiEnchantmentSupport.HasDynamicVarContributionsFor(__instance.Name))
        {
            return;
        }

        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return;
        }

        try
        {
            // Start from BaseValue so the postfix is idempotent — re-running it on a previously
            // previewed var (PreviewValue already contains last-round contributions) would
            // otherwise compound contributions. Base no-op UpdateCardPreview hasn't touched
            // PreviewValue yet, but other game systems (RecalculateForUpgradeOrEnchant) reset
            // PreviewValue to BaseValue through ResetToBase. We mirror that contract here.
            decimal value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, __instance.BaseValue);
            __instance.PreviewValue = value;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] DynamicVar.UpdateCardPreview postfix for Var={__instance.Name} " +
                $"Card={card.Id} failed: {ex.GetBaseException().Message}");
        }
    }

    [HarmonyPatch(typeof(NEnchantPreview), nameof(NEnchantPreview.Init))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool EnchantPreviewPrefix(NEnchantPreview __instance, CardModel card, EnchantmentModel canonicalEnchantment, int amount)
    {
        // Base-game source: NEnchantPreview.Init.
        // We need the mod-aware enchant path here so previews can show an added extra enchantment.
        canonicalEnchantment.AssertCanonical();
        AccessTools.Method(typeof(NEnchantPreview), "RemoveExistingCards")?.Invoke(__instance, null);

        Control before = __instance.GetNode<Control>("%Before");
        Control after = __instance.GetNode<Control>("%After");

        NPreviewCardHolder beforeHolder = NPreviewCardHolder.Create(NCard.Create(card)!, showHoverTips: true, scaleOnHover: false)!;
        before.AddChild(beforeHolder);
        beforeHolder.CardNode!.UpdateVisuals(card.Pile?.Type ?? PileType.None, CardPreviewMode.Normal);

        CardModel previewCard = card.CardScope!.CloneCard(card);
        MultiEnchantmentSupport.ApplyEnchantment(canonicalEnchantment.ToMutable(), previewCard, amount);
        previewCard.IsEnchantmentPreview = true;

        NPreviewCardHolder afterHolder = NPreviewCardHolder.Create(NCard.Create(previewCard)!, showHoverTips: true, scaleOnHover: false)!;
        after.AddChild(afterHolder);
        afterHolder.CardNode!.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        return false;
    }

    [HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
    [HarmonyPrefix]
    private static void CardVisualsPrefix(NCard __instance, PileType pileType, CardPreviewMode previewMode)
    {
        MultiEnchantmentSupport.UpdateAdditionalEnchantmentPreviews(__instance, previewMode);
    }

    [HarmonyPatch(typeof(NCard), "OnEnchantmentChanged")]
    [HarmonyPostfix]
    private static void CardEnchantmentChangedPostfix(NCard __instance)
    {
        // Base-game NCard.OnEnchantmentChanged only refreshes enchantment icons. Re-run the full
        // visual pass so formatter-generated extra card text is recomputed when enchantments change.
        if (__instance.Model != null && __instance.IsNodeReady())
        {
            __instance.UpdateVisuals(__instance.Model.Pile?.Type ?? PileType.None, CardPreviewMode.Normal);
        }
    }

    [HarmonyPatch(typeof(NCard), "UpdateEnchantmentVisuals")]
    [HarmonyPostfix]
    private static void CardEnchantTabsPostfix(NCard __instance)
    {
        MultiEnchantmentSupport.SyncExtraEnchantmentTabs(__instance);
    }

    [HarmonyPatch(typeof(NCard), "OnEnchantmentStatusChanged")]
    [HarmonyPostfix]
    private static void CardEnchantmentStatusChangedPostfix(NCard __instance)
    {
        // Base-game source: NCard.OnEnchantmentStatusChanged only updates the primary enchantment
        // tab. Multi-stack visuals that expand one enchantment into several tabs, such as stacked
        // Sown, must resync the extra tabs too so queued/replay cards reflect the consumed state.
        MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(__instance);
    }

    [HarmonyPatch(typeof(NCard), nameof(NCard.OnReturnedFromPool))]
    [HarmonyPostfix]
    private static void CardReturnedPostfix(NCard __instance)
    {
        // Base-game source: NCard.OnReturnedFromPool only resets ready nodes. Match that boundary
        // here so pooled-but-not-ready cards never hit the mod's cleanup path.
        if (__instance.IsNodeReady())
        {
            MultiEnchantmentSupport.ClearCardUi(__instance);
        }
    }

    [HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetPosition))]
    [HarmonyPostfix]
    private static void HandCardHolderTargetPositionPostfix(NHandCardHolder __instance)
    {
        // CenterCard and related targeting flows animate the holder without necessarily refreshing
        // the card's enchantment visuals again. Mirror the primary tab state here so extra tabs
        // keep following the centered card.
        if (__instance.CardNode != null)
        {
            MultiEnchantmentSupport.SyncExtraEnchantmentTabs(__instance.CardNode);
        }
    }

    [HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetScale))]
    [HarmonyPostfix]
    private static void HandCardHolderTargetScalePostfix(NHandCardHolder __instance)
    {
        if (__instance.CardNode != null)
        {
            MultiEnchantmentSupport.SyncExtraEnchantmentTabs(__instance.CardNode);
        }
    }

    [HarmonyPatch(typeof(NCardPlayQueue), "TweenCardToQueuePosition")]
    [HarmonyPostfix]
    private static void CardPlayQueueTweenPostfix(object item)
    {
        // Base-game source: NCardPlayQueue.TweenCardToQueuePosition.
        // Queue cards are re-scaled and moved by tween without a fresh card-visual pass. Mirror
        // the primary enchant tab state here so extra enchant tabs stay visible on queued cards.
        if (AccessTools.Field(item.GetType(), "card")?.GetValue(item) is NCard cardNode)
        {
            MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(cardNode);
        }
    }

    [HarmonyPatch(typeof(NCardPlayQueue), "UpdateCardVisuals")]
    [HarmonyPostfix]
    private static void CardPlayQueueUpdateCardVisualsPostfix(object item)
    {
        // Base-game source: NCardPlayQueue.UpdateCardVisuals.
        // Queue entries can swap to a new combat-card model before execution. Refresh after the
        // model swap so extra enchantment tabs are recreated for the active queued card instance.
        if (AccessTools.Field(item.GetType(), "card")?.GetValue(item) is NCard cardNode)
        {
            MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(cardNode);
        }
    }

    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.AddToPlayContainer))]
    [HarmonyPostfix]
    private static void CombatUiAddToPlayContainerPostfix(NCard card)
    {
        // Base-game source: NCombatUi.AddToPlayContainer.
        // Reparenting into PlayContainer is another path that can reuse an existing NCard without
        // recreating visuals. Refresh here so extra tabs survive hand -> queue -> play moves.
        MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(card);
    }

    [HarmonyPatch(typeof(NCombatUi), "OnPeekButtonToggled")]
    [HarmonyPostfix]
    private static void CombatUiPeekButtonToggledPostfix(NCombatUi __instance)
    {
        // Base-game source: NCombatUi.OnPeekButtonToggled.
        // Peeking recenters cards already in PlayContainer without rerunning NCard visuals.
        // Refresh the extra enchantment tabs after the toggle so the full stack stays visible.
        foreach (NCard cardNode in __instance.PlayContainer.GetChildren().OfType<NCard>())
        {
            MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(cardNode);
        }
    }

    [HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.Add))]
    [HarmonyPostfix]
    private static void PlayerHandAddPostfix(ref NHandCardHolder __result)
    {
        // Base-game source: NPlayerHand.Add.
        // Cards can be reattached to the hand after queue cancellation or other UI flows while
        // keeping the same NCard instance. Refresh the extra tabs after the holder is rebuilt.
        if (__result?.CardNode != null)
        {
            MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(__result.CardNode);
        }
    }

    [HarmonyPatch(typeof(NSelectedHandCardContainer), nameof(NSelectedHandCardContainer.Add))]
    [HarmonyPostfix]
    private static void SelectedHandCardContainerAddPostfix(ref NSelectedHandCardHolder __result)
    {
        // Base-game source: NSelectedHandCardContainer.Add.
        // Multi-select UI reparents live card nodes into a separate container. Mirror the primary
        // enchant tab again so centered/selected cards keep the full enchantment stack visible.
        if (__result?.CardNode != null)
        {
            MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(__result.CardNode);
        }
    }

    [HarmonyPatch(typeof(NCard), nameof(NCard.AnimCardToPlayPile))]
    [HarmonyPostfix]
    private static void CardAnimToPlayPilePostfix(NCard __instance)
    {
        // Base-game source: NCard.AnimCardToPlayPile.
        // The played-card animation shrinks and moves the same node. Refresh immediately before the
        // tween runs so any reused card node keeps its extra enchantment tabs attached.
        MultiEnchantmentSupport.RefreshExtraEnchantmentTabs(__instance);
    }

    [HarmonyPatch(typeof(NCard), "UnsubscribeFromModel")]
    [HarmonyPostfix]
    private static void CardUnsubscribePostfix(NCard __instance)
    {
        MultiEnchantmentSupport.ClearCardUi(__instance);
    }

    [HarmonyPatch(typeof(CloneRestSiteOption), nameof(CloneRestSiteOption.OnSelect))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool CloneRestSiteOptionPrefix(CloneRestSiteOption __instance, ref Task<bool> __result)
    {
        // Base-game source: CloneRestSiteOption.OnSelect.
        // This override exists so cloned cards keep all compatible enchantments, not just the primary one.
        MultiEnchantmentMod.Logger.Info(
            "[MultiEnchantment] Intercepting CloneRestSiteOption.OnSelect.");
        try
        {
            __result = CloneRestSiteOptionWithMultiEnchantments(__instance);
            return false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] CloneRestSiteOption.OnSelect failed. " +
                $"Falling back to base-game implementation. Error: {ex}");
            return true;
        }
    }

    [HarmonyPatch(typeof(NCardEnchantVfx), nameof(NCardEnchantVfx._Ready))]
    [HarmonyPostfix]
    private static void CardEnchantVfxPostfix(NCardEnchantVfx __instance)
    {
        // Base-game source: NCardEnchantVfx._Ready.
        // Vanilla animates exactly one enchantment badge. Preserve that animated path for only the
        // newest enchantment, then render older enchantment badges as static card-local copies so
        // the shader sweep no longer affects the entire stack at once.
        CardModel? card = NCardEnchantVfxCardModelField?.GetValue(__instance) as CardModel;
        NCard? cardNode = NCardEnchantVfxCardNodeField?.GetValue(__instance) as NCard;
        TextureRect? icon = NCardEnchantVfxIconField?.GetValue(__instance) as TextureRect;
        // Base-game source: NCardEnchantVfx._Ready hides only the primary enchantment tab on the
        // embedded NCard. The mod's extra tabs need to be hidden too so only the VFX badge stack
        // remains visible during the enchant animation.
        MultiEnchantmentSupport.HideExtraEnchantmentTabs(cardNode);
        MultiEnchantmentSupport.SyncEnchantVfxPresentation(__instance, card, cardNode, icon);
    }

    [HarmonyPatch(typeof(NCardEnchantVfx), nameof(NCardEnchantVfx.Create))]
    [HarmonyPostfix]
    private static void CardEnchantVfxCreatePostfix(CardModel card, ref NCardEnchantVfx? __result)
    {
        // Snapshot the visible enchantment stack at VFX creation time so the animation does not
        // depend on later UI refreshes or card-node state during _Ready.
        MultiEnchantmentSupport.CaptureEnchantVfxSnapshot(__result, card);
    }

    [HarmonyPatch(typeof(RestSiteOption), nameof(RestSiteOption.Generate))]
    [HarmonyPostfix]
    private static void RestSiteOptionGeneratePostfix(Player player, ref List<RestSiteOption> __result)
    {
        // Base-game source: RestSiteOption.Generate.
        // Vanilla only gets the clone fire option from PaelsGrowth's hook. Console-added Clone
        // enchantments bypass that source, so add the option whenever any deck card currently has
        // Clone, regardless of whether Clone is the primary or an extra enchantment.
        if (!MultiEnchantmentSupport.ShouldOfferCloneRestSiteOption(player))
        {
            return;
        }

        if (__result.Any(static option => option.OptionId == "CLONE"))
        {
            return;
        }

        __result.Add(new CloneRestSiteOption(player));
    }

    private static DynamicVar GetCalculatedBaseVar(CalculatedVar calculatedVar)
    {
        if (CalculatedVarGetBaseVarMethod?.Invoke(calculatedVar, null) is DynamicVar baseVar)
        {
            return baseVar;
        }

        // Fallback when GetBaseVar is unavailable (renamed/removed in newer game versions):
        // CalculatedVar extends DynamicVar, use it directly as the base value source.
        return calculatedVar;
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
            int cloneCount = Math.Max(1, MultiEnchantmentStackSupport.GetTotalAmount(card, typeof(Clone)));
            for (int i = 0; i < cloneCount; i++)
            {
                CardModel clone = owner.RunState.CloneCard(card);
                results.Add(await CardPileCmd.Add(clone, PileType.Deck));
            }
        }

        CardCmd.PreviewCardPileAdd(results, 1.2f, CardPreviewStyle.MessyLayout);
        return true;
    }

    private static Player GetRestSiteOptionOwner(RestSiteOption option)
    {
        return RestSiteOptionOwnerProperty?.GetValue(option) as Player
            ?? throw new InvalidOperationException("Failed to access RestSiteOption owner.");
    }

    private static decimal ApplyCardDamageEnchantments(
        CardModel? cardSource,
        decimal damage,
        ValueProp props,
        ModifyDamageHookType modifyDamageHookType)
    {
        decimal value = MultiEnchantmentSupport.ApplyDamageEnchantments(cardSource, damage, props, modifyDamageHookType);
        return MultiEnchantmentSupport.ApplyDynamicVarEnchantments(cardSource, "damage", value);
    }

    private static decimal ModifyDamageInternal(
        IRunState runState,
        ICombatState? combatState,
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
        ICombatState? combatState,
        Creature target,
        decimal block,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        List<AbstractModel> modifiers)
    {
        decimal value = block;
        if (combatState == null)
        {
            // No combat state available (e.g., out-of-combat preview): skip the listener pass
            // entirely, matching vanilla Hook.ModifyBlock which would otherwise NRE on null.
            return Math.Max(0m, value);
        }

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

        if (Hook.ShouldPlayerResetEnergy((ICombatState)state, player))
        {
            SfxCmd.Play("event:/sfx/ui/gain_energy");
            player.PlayerCombatState!.ResetEnergy();
        }
        else
        {
            player.PlayerCombatState!.AddMaxEnergyToCurrent();
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
        MultiEnchantmentScopeSupport.OnPlayerTurnStarted(state, player);
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
    [HarmonyPriority(Priority.Low)]
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

        if (!TryGetCardFromGroupKey(__instance, out CardModel? left) ||
            !TryGetCardFromGroupKey(obj, out CardModel? right))
        {
            // Fallback when the inner type or field is unavailable (renamed/removed in newer game
            // versions): let the base-game Equals handle card grouping.
            MultiEnchantmentMod.Logger.Info(
                "[MultiEnchantment] CardGroupKey.Equals falling back to base-game implementation (reflection unavailable).");
            return true;
        }

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
        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(object __instance, ref int __result)
        {
            if (!TryGetCardFromGroupKey(__instance, out CardModel? card))
            {
                // Fallback when the inner type or field is unavailable: let the base-game
                // GetHashCode handle card grouping.
                MultiEnchantmentMod.Logger.Info(
                    "[MultiEnchantment] CardGroupKey.GetHashCode falling back to base-game implementation (reflection unavailable).");
                return true;
            }

            // Keep hash inputs aligned with CardGroupKeyEqualsPrefix above.
            __result = HashCode.Combine(
                card.Id,
                card.CurrentUpgradeLevel,
                MultiEnchantmentSupport.GetEnchantmentsHashCode(card));
            return false;
        }
    }

    private static bool TryGetCardFromGroupKey(object groupKey, [NotNullWhen(true)] out CardModel? card)
    {
        card = CardGroupKeyCardField?.GetValue(groupKey) as CardModel;
        return card != null;
    }
}

[HarmonyPatch]
internal static class MultiEnchantmentSerializableCardGroupingPatches
{
    // Base-game source: SerializableCard.Equals / GetHashCode compare only Id, CurrentUpgradeLevel,
    // and the primary Enchantment. The run-history deck view groups cards via
    // NDeckHistory.PopulateCards (group x by x), so two copies that differ only in the mod's extra
    // enchantments collapse into one "Nx" row. Include the mod's two saved-string properties in
    // equality/hash so cards with diverging extras get their own row.

    [HarmonyPatch(typeof(SerializableCard), nameof(SerializableCard.Equals), new[] { typeof(object) })]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool EqualsPrefix(SerializableCard __instance, object? obj, ref bool __result)
    {
        if (obj is null || obj.GetType() != __instance.GetType())
        {
            __result = false;
            return false;
        }

        SerializableCard other = (SerializableCard)obj;
        if (!Equals(__instance.Id, other.Id) ||
            __instance.CurrentUpgradeLevel != other.CurrentUpgradeLevel ||
            !Equals(__instance.Enchantment, other.Enchantment))
        {
            __result = false;
            return false;
        }

        __result =
            SavedPropertiesComparer.HaveSameString(__instance.Props, other.Props,
                MultiEnchantmentSupport.SavePropertyName) &&
            SavedPropertiesComparer.HaveSameString(__instance.Props, other.Props,
                MultiEnchantmentSupport.OrderSavePropertyName);
        return false;
    }

    [HarmonyPatch(typeof(SerializableCard), nameof(SerializableCard.GetHashCode))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static bool GetHashCodePrefix(SerializableCard __instance, ref int __result)
    {
        __result = HashCode.Combine(
            __instance.Id,
            __instance.CurrentUpgradeLevel,
            __instance.Enchantment,
            SavedPropertiesComparer.GetStringHashCode(__instance.Props,
                MultiEnchantmentSupport.SavePropertyName),
            SavedPropertiesComparer.GetStringHashCode(__instance.Props,
                MultiEnchantmentSupport.OrderSavePropertyName));
        return false;
    }
}

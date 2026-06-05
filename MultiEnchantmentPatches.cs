using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
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
    private static readonly PropertyInfo? RestSiteOptionOwnerProperty =
        AccessTools.Property(typeof(RestSiteOption), "Owner");
    private static readonly FieldInfo? NCardEnchantVfxCardModelField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_cardModel");
    private static readonly FieldInfo? NCardEnchantVfxCardNodeField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_cardNode");
    private static readonly FieldInfo? NCardEnchantVfxIconField =
        AccessTools.Field(typeof(NCardEnchantVfx), "_enchantmentIcon");
    private static readonly FieldInfo? NDeckHistoryEntryTitleLabelField =
        AccessTools.Field(typeof(NDeckHistoryEntry), "_titleLabel");
    private static readonly FieldInfo? NDeckHistoryEntryEnchantmentImageField =
        AccessTools.Field(typeof(NDeckHistoryEntry), "_enchantmentImage");

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

        // Vanilla's only remaining rejection reason is the occupied primary slot (clause ④ of
        // EnchantmentModel.CanEnchant). If that clause does NOT hold, vanilla itself would have
        // returned true — so this false came from another mod's patch for a reason we can't see.
        // Leave it alone rather than relaxing it.
        bool vanillaPrimarySlotRejection =
            card.Enchantment != null &&
            (!__instance.IsStackable || card.Enchantment.GetType() != __instance.GetType());
        if (!vanillaPrimarySlotRejection) return;

        // All other vanilla checks pass. The remaining vanilla failure is that the primary
        // enchantment slot is already occupied. Re-enable when this is either a new extra
        // enchantment type or a supported same-type stack.
        bool relaxed = MultiEnchantmentStackSupport.CanApply(card, __instance.GetType()) ||
            MultiEnchantmentStackSupport.CanStackOnto(card, __instance.GetType());
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
    private static bool ClearEnchantmentPrefix(CardModel card)
    {
        EnchantmentModel? primary = card.Enchantment;
        if (primary == null)
        {
            MultiEnchantmentSupport.ClearAdditionalEnchantments(card, triggerChanged: true);
            return false;
        }

        MultiEnchantmentSupport.ClearAdditionalEnchantments(card, triggerChanged: false);
        MultiEnchantmentSupport.RemoveEnchantmentInternal(
            card,
            primary,
            RemovalReason.CardCleared,
            bypassVeto: true,
            refreshCard: true,
            triggerChanged: true);
        return false;
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
    [HarmonyPostfix]
    private static void BeforeCombatStartPostfix(ref Task __result, ICombatState? combatState)
    {
        __result = BeforeCombatStartPostfixAsync(__result, combatState);
    }

    private static async Task BeforeCombatStartPostfixAsync(Task original, ICombatState? combatState)
    {
        // First-time gate: by the time any combat starts, every ModInitializer has run and the
        // enchantment registry must be considered closed. Late registrations after this point
        // would change semantics for cards already cached / normalized inside the running combat.
        // SealRegistryIfNeeded is idempotent (Interlocked-guarded), so subsequent combats no-op.
        Api.Internal.AssemblyScanner.SealRegistryIfNeeded();

        await original;
        if (combatState == null)
        {
            return;
        }

        MultiEnchantmentScopeSupport.OnCombatStarted(combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatEnd))]
    [HarmonyPostfix]
    private static void AfterCombatEndPostfix(ref Task __result, IRunState runState, ICombatState? combatState)
    {
        __result = AfterCombatEndPostfixAsync(__result, runState, combatState);
    }

    private static async Task AfterCombatEndPostfixAsync(Task original, IRunState runState, ICombatState? combatState)
    {
        await original;
        MultiEnchantmentScopeSupport.OnCombatEnded(runState, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardEnteredCombat))]
    [HarmonyPostfix]
    private static void AfterCardEnteredCombatPostfix(ref Task __result, ICombatState combatState, CardModel card)
    {
        __result = AfterCardEnteredCombatPostfixAsync(__result, combatState, card);
    }

    private static async Task AfterCardEnteredCombatPostfixAsync(Task original, ICombatState combatState, CardModel card)
    {
        await original;
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
    private static void AfterTurnEndPostfix(ref Task __result, ICombatState combatState, CombatSide side, IEnumerable<Creature> participants)
    {
        __result = AfterTurnEndPostfixAsync(__result, combatState, side, participants);
    }

    private static async Task AfterTurnEndPostfixAsync(Task original, ICombatState combatState, CombatSide side, IEnumerable<Creature> participants)
    {
        await original;
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
            foreach (Player player in ((combatState as CombatState)?.Players
                         ?? Enumerable.Empty<Player>()).ToList())
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
    private static void HookAfterCardPlayedPostfix(ref Task __result, ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        __result = HookAfterCardPlayedPostfixAsync(__result, combatState, choiceContext, cardPlay);
    }

    private static async Task HookAfterCardPlayedPostfixAsync(Task original, ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await original;
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
    private static void HookAfterCardDrawnPostfix(ref Task __result, ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        __result = HookAfterCardDrawnPostfixAsync(__result, combatState, choiceContext, card, fromHandDraw);
    }

    private static async Task HookAfterCardDrawnPostfixAsync(Task original, ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        await original;
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForCard(
            card, ActivationTrigger.AfterCardDrawn);

        // Phase 3a T3a.2: OnCardDrawn lifecycle for active enchantments.
        MultiEnchantmentScopeSupport.DispatchOnCardDrawnForCard(card);

        // Phase 4: broadcast OnAnyCardDrawn to every enchantment in combat that opted in.
        MultiEnchantmentScopeSupport.DispatchOnAnyCardDrawnBroadcast(card, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void HookAfterCardDrawnStackedPostfix(
        ref Task __result,
        ICombatState combatState,
        PlayerChoiceContext choiceContext,
        CardModel card,
        bool fromHandDraw)
    {
        __result = HookAfterCardDrawnStackedPostfixAsync(__result, combatState, choiceContext, card, fromHandDraw);
    }

    private static async Task HookAfterCardDrawnStackedPostfixAsync(
        Task original,
        ICombatState combatState,
        PlayerChoiceContext choiceContext,
        CardModel card,
        bool fromHandDraw)
    {
        await original;
        await MultiEnchantmentSupport.DispatchAfterCardDrawnStacked(choiceContext, card, fromHandDraw);
        await MultiEnchantmentSupport.DispatchAfterAnyCardDrawnStacked(choiceContext, combatState, card, fromHandDraw);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
    [HarmonyPostfix]
    private static void HookAfterCardExhaustedPostfix(ref Task __result, ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool causedByEthereal)
    {
        __result = HookAfterCardExhaustedPostfixAsync(__result, combatState, choiceContext, card, causedByEthereal);
    }

    private static async Task HookAfterCardExhaustedPostfixAsync(Task original, ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool causedByEthereal)
    {
        await original;
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForCard(
            card, ActivationTrigger.AfterCardExhausted);

        // Phase 3a T3a.3: OnCardExhausted lifecycle for active enchantments.
        MultiEnchantmentScopeSupport.DispatchOnCardExhaustedForCard(card);

        // Phase 4: broadcast OnAnyCardExhausted to every enchantment in combat that opted in.
        MultiEnchantmentScopeSupport.DispatchOnAnyCardExhaustedBroadcast(card, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDiscarded))]
    [HarmonyPostfix]
    private static void HookAfterCardDiscardedPostfix(ref Task __result, ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card)
    {
        __result = HookAfterCardDiscardedPostfixAsync(__result, combatState, choiceContext, card);
    }

    private static async Task HookAfterCardDiscardedPostfixAsync(Task original, ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card)
    {
        await original;
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForCard(
            card, ActivationTrigger.AfterCardDiscarded);

        // Phase 3a T3a.4: OnCardDiscarded lifecycle for active enchantments.
        MultiEnchantmentScopeSupport.DispatchOnCardDiscardedForCard(card);

        // Phase 4: broadcast OnAnyCardDiscarded to every enchantment in combat that opted in.
        MultiEnchantmentScopeSupport.DispatchOnAnyCardDiscardedBroadcast(card, combatState);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
    [HarmonyPostfix]
    private static void HookAfterPlayerTurnStartPostfix(ref Task __result, ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        __result = HookAfterPlayerTurnStartPostfixAsync(__result, combatState, choiceContext, player);
    }

    private static async Task HookAfterPlayerTurnStartPostfixAsync(Task original, ICombatState combatState, PlayerChoiceContext choiceContext, Player player)
    {
        await original;
        MultiEnchantmentScopeSupport.DispatchActivationTriggerForPlayer(
            player, ActivationTrigger.AfterPlayerTurnStart);
    }

    // === Phase 3c — pile / guard / block bridges ============================================

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardChangedPiles))]
    [HarmonyPostfix]
    private static void HookAfterCardChangedPilesPostfix(ref Task __result, IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? clonedBy)
    {
        __result = HookAfterCardChangedPilesPostfixAsync(__result, runState, combatState, card, oldPile, clonedBy);
    }

    private static async Task HookAfterCardChangedPilesPostfixAsync(Task original, IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? clonedBy)
    {
        await original;
        MultiEnchantmentScopeSupport.DispatchOnCardChangedPilesForCard(card, oldPile, clonedBy);
    }

    // vanilla doesn't expose a per-card AfterCardRetained Hook entry point — only AfterFlush
    // which delivers the full retainedCards collection. Fan out from there.
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterFlush))]
    [HarmonyPostfix]
    private static void HookAfterFlushRetainedPostfix(
        ref Task __result,
        ICombatState combatState,
        Player player,
        PlayerChoiceContext playerChoiceContext,
        IReadOnlyCollection<CardModel> flushedCards,
        IReadOnlyCollection<CardModel> retainedCards)
    {
        __result = HookAfterFlushRetainedPostfixAsync(__result, combatState, player, playerChoiceContext, flushedCards, retainedCards);
    }

    private static async Task HookAfterFlushRetainedPostfixAsync(
        Task original,
        ICombatState combatState,
        Player player,
        PlayerChoiceContext playerChoiceContext,
        IReadOnlyCollection<CardModel> flushedCards,
        IReadOnlyCollection<CardModel> retainedCards)
    {
        await original;
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
    private static void HookBeforeBlockGainedPostfix(ref Task __result, ICombatState combatState, Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        __result = HookBeforeBlockGainedPostfixAsync(__result, combatState, creature, amount, props, cardSource);
    }

    private static async Task HookBeforeBlockGainedPostfixAsync(Task original, ICombatState combatState, Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        await original;
        BlockGainContext context = new(creature, amount, cardSource);
        MultiEnchantmentScopeSupport.DispatchOnBeforeBlockGainedForPlayer(context);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockGained))]
    [HarmonyPostfix]
    private static void HookAfterBlockGainedPostfix(ref Task __result, ICombatState combatState, Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        __result = HookAfterBlockGainedPostfixAsync(__result, combatState, creature, amount, props, cardSource);
    }

    private static async Task HookAfterBlockGainedPostfixAsync(Task original, ICombatState combatState, Creature creature, decimal amount, ValueProp props, CardModel? cardSource)
    {
        await original;
        BlockGainContext context = new(creature, amount, cardSource);
        MultiEnchantmentScopeSupport.DispatchOnBlockGainedForPlayer(context);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ShouldDie))]
    [HarmonyPostfix]
    private static void HookShouldDiePostfix(Creature creature, ref bool __result, ref AbstractModel? preventer)
    {
        // Guard semantics: vanilla returns true when nothing prevented death. If it already
        // returned false (some other listener vetoed), don't second-guess. Otherwise, ask the
        // mod's active enchantments — any single false vetoes.
        if (!__result)
        {
            return;
        }
        if (!MultiEnchantmentScopeSupport.DispatchOnShouldDieForCreature(creature, out AbstractModel? modPreventer))
        {
            __result = false;
            preventer = modPreventer;
        }
    }

    // === Phase 3b — combat-flow bridges =====================================================

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnStart))]
    [HarmonyPostfix]
    private static void HookAfterSideTurnStartPostfix(ref Task __result, ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        __result = HookAfterSideTurnStartPostfixAsync(__result, combatState, side, participants);
    }

    private static async Task HookAfterSideTurnStartPostfixAsync(Task original, ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        await original;
        // Phase 3b T3b.1: bridge to OnSideTurnStart lifecycle. Vanilla fires both for player and
        // enemy turns; handlers can branch on the side parameter. The existing OnTurnStart
        // lifecycle remains player-only for backward compatibility.
        MultiEnchantmentScopeSupport.DispatchOnSideTurnStart(combatState, side);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeSideTurnStart))]
    [HarmonyPostfix]
    private static void HookBeforeSideTurnStartPostfix(ref Task __result, ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        __result = HookBeforeSideTurnStartPostfixAsync(__result, combatState, side, participants);
    }

    private static async Task HookBeforeSideTurnStartPostfixAsync(Task original, ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        await original;
        // Phase 3b T3b.2: bridge to OnBeforeSideTurnStart lifecycle.
        MultiEnchantmentScopeSupport.DispatchOnBeforeSideTurnStart(combatState, side);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeAttack))]
    [HarmonyPostfix]
    private static void HookBeforeAttackPostfix(ref Task __result, ICombatState combatState, AttackCommand command)
    {
        __result = HookBeforeAttackPostfixAsync(__result, combatState, command);
    }

    private static async Task HookBeforeAttackPostfixAsync(Task original, ICombatState combatState, AttackCommand command)
    {
        await original;
        // Phase 3b T3b.3: bridge to OnBeforeAttack lifecycle. AttackCommand exposes Attacker,
        // CardSource, Results — handlers filter as needed.
        MultiEnchantmentScopeSupport.DispatchOnBeforeAttack(combatState, command);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterAttack))]
    [HarmonyPostfix]
    private static void HookAfterAttackPostfix(ref Task __result, ICombatState combatState, AttackCommand command)
    {
        __result = HookAfterAttackPostfixAsync(__result, combatState, command);
    }

    private static async Task HookAfterAttackPostfixAsync(Task original, ICombatState combatState, AttackCommand command)
    {
        await original;
        // Phase 3b T3b.4: bridge to OnAfterAttack lifecycle.
        MultiEnchantmentScopeSupport.DispatchOnAfterAttack(combatState, command);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
    [HarmonyPostfix]
    private static void HookAfterDamageReceivedPostfix(
        ref Task __result,
        PlayerChoiceContext choiceContext,
        IRunState runState,
        ICombatState? combatState,
        Creature target,
        DamageResult result,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        __result = HookAfterDamageReceivedPostfixAsync(__result, choiceContext, runState, combatState, target, result, props, dealer, cardSource);
    }

    private static async Task HookAfterDamageReceivedPostfixAsync(
        Task original,
        PlayerChoiceContext choiceContext,
        IRunState runState,
        ICombatState? combatState,
        Creature target,
        DamageResult result,
        ValueProp props,
        Creature? dealer,
        CardModel? cardSource)
    {
        await original;
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

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageGiven))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void HookAfterDamageGivenStackedPostfix(
        ref Task __result,
        PlayerChoiceContext choiceContext,
        ICombatState combatState,
        Creature? dealer,
        DamageResult results,
        ValueProp props,
        Creature target,
        CardModel? cardSource)
    {
        __result = HookAfterDamageGivenStackedPostfixAsync(__result, choiceContext, combatState, dealer, results, props, target, cardSource);
    }

    private static async Task HookAfterDamageGivenStackedPostfixAsync(
        Task original,
        PlayerChoiceContext choiceContext,
        ICombatState combatState,
        Creature? dealer,
        DamageResult results,
        ValueProp props,
        Creature target,
        CardModel? cardSource)
    {
        await original;
        await MultiEnchantmentSupport.DispatchAfterDamageGivenStacked(
            choiceContext,
            cardSource,
            dealer,
            results,
            props,
            target);
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyEnergyCostInCombat))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void HookModifyEnergyCostInCombatPostfix(ICombatState combatState, CardModel card, ref decimal __result)
    {
        __result = MultiEnchantmentSupport.ApplyEnergyCostContributions(card, __result);
    }

    [HarmonyPatch(typeof(MysticLighter), nameof(MysticLighter.ModifyDamageAdditive))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void MysticLighterModifyDamageAdditivePostfix(
        MysticLighter __instance,
        ValueProp props,
        CardModel? cardSource,
        ref decimal __result)
    {
        // Base-game source: MysticLighter.ModifyDamageAdditive.
        // Vanilla checks only cardSource.Enchantment. Re-enable the same bonus when the card's
        // only enchantments live in the mod's extra slots.
        if (__result != 0m ||
            !props.IsPoweredAttack() ||
            cardSource == null ||
            cardSource.Enchantment != null ||
            !MultiEnchantmentSupport.HasAnyEnchantments(cardSource) ||
            cardSource.Owner != __instance.Owner)
        {
            return;
        }

        __result = __instance.DynamicVars.Damage.IntValue;
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeFlush))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void HookBeforeFlushStackedPostfix(ref Task __result, ICombatState combatState, Player player)
    {
        __result = HookBeforeFlushStackedPostfixAsync(__result, combatState, player);
    }

    private static async Task HookBeforeFlushStackedPostfixAsync(Task original, ICombatState combatState, Player player)
    {
        await original;
        if (player.Creature?.CombatState == null)
        {
            return;
        }

        await MultiEnchantmentSupport.DispatchBeforeFlushStacked(null, player);
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
        try
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
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] MutableClone postfix failed for {__instance.GetType().FullName}. " +
                $"Clone will keep vanilla state only. Error: {ex}");
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

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade), new[] { typeof(IEnumerable<CardModel>), typeof(CardPreviewStyle) })]
    [HarmonyPrefix]
    private static void UpgradePrefix(ref IEnumerable<CardModel> cards, out List<(CardModel Card, int UpgradeLevel)> __state)
    {
        List<CardModel> snapshot = cards.ToList();
        cards = snapshot;
        __state = snapshot
            .Where(MultiEnchantmentSupport.RequiresMultiEnchantmentLogic)
            .Select(static card => (card, card.CurrentUpgradeLevel))
            .ToList();
    }

    [HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade), new[] { typeof(IEnumerable<CardModel>), typeof(CardPreviewStyle) })]
    [HarmonyPostfix]
    private static void UpgradePostfix(List<(CardModel Card, int UpgradeLevel)> __state)
    {
        foreach ((CardModel card, int upgradeLevel) in __state)
        {
            if (card.CurrentUpgradeLevel <= upgradeLevel)
            {
                continue;
            }

            try
            {
                MultiEnchantmentScopeSupport.DispatchOnRestoredForCard(card);
                MultiEnchantmentScopeSupport.DispatchOnCardUpgradedForCard(card);
            }
            catch (Exception ex)
            {
                MultiEnchantmentMod.Logger.Error(
                    $"[MultiEnchantment] Failed to refresh extra enchantments after upgrade for Card={card.Id}. " +
                    $"Card may temporarily show vanilla-only upgraded state. Error: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(CardModel), nameof(CardModel.DowngradeInternal))]
    [HarmonyPostfix]
    private static void DowngradeInternalPostfix(CardModel __instance)
    {
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(__instance))
        {
            return;
        }

        try
        {
            MultiEnchantmentSupport.ReapplyMultiEnchantmentsAfterDowngrade(__instance);
            MultiEnchantmentScopeSupport.DispatchOnCardDowngradedForCard(__instance);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Failed to reapply extra enchantments after downgrade for Card={__instance.Id}. " +
                $"Card may temporarily show vanilla-only downgraded state. Error: {ex}");
        }
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
        try
        {
            MultiEnchantmentSupport.DeserializeAdditionalEnchantments(save, __result);
            if (MultiEnchantmentSupport.NormalizeCardEnchantmentStacks(__result))
            {
                __result.FinalizeUpgradeInternal();
                MultiEnchantmentStackSupport.RefreshDerivedState(__result);
            }

            MultiEnchantmentScopeSupport.DispatchOnRestoredForCard(__result);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error(
                $"[MultiEnchantment] Failed to restore multi-enchantment state for card {__result.Id}. " +
                $"The card will load with vanilla enchantment state only. A third-party enchantment mod " +
                $"may have been removed or updated. Error: {ex}");
        }
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
    }

    [HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.FromSerializable))]
    [HarmonyPostfix]
    private static void EnchantmentFromSerializablePostfix(SerializableEnchantment save, ref EnchantmentModel __result)
    {
        MultiEnchantmentStackSupport.RestoreSerializedProps(save, __result);
    }

    [HarmonyPatch(typeof(SavedPropertiesTypeCache), nameof(SavedPropertiesTypeCache.InjectTypeIntoCache))]
    [HarmonyPostfix]
    private static void SavedPropertiesInjectTypeIntoCachePostfix()
    {
        MultiEnchantmentSupport.RefreshSavedPropertiesNetIdBitSize();
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), new[] { typeof(SerializableRun), typeof(bool) })]
    [HarmonyPrefix]
    private static void SaveRunPrefix(SerializableRun save, bool isMultiplayer)
    {
        MultiEnchantmentSaveSidecar.PrepareRunForDisk(save, isMultiplayer);
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.LoadRunSave))]
    [HarmonyPostfix]
    private static void LoadRunSavePostfix(ReadSaveResult<SerializableRun> __result)
    {
        if (__result is { Success: true, SaveData: { } save })
        {
            MultiEnchantmentSaveSidecar.Reload(multiplayer: false);
            MultiEnchantmentSaveSidecar.RestoreRunFromDisk(save);
        }
    }

    [HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.LoadMultiplayerRunSave))]
    [HarmonyPostfix]
    private static void LoadMultiplayerRunSavePostfix(ReadSaveResult<SerializableRun> __result)
    {
        if (__result is { Success: true, SaveData: { } save })
        {
            MultiEnchantmentSaveSidecar.Reload(multiplayer: true);
            MultiEnchantmentSaveSidecar.RestoreRunFromDisk(save);
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

            foreach (AbstractModel model in combatState.IterateHookListeners().ToList())
            {
                decimal add = model.ModifyBlockAdditive(target, value, props, cardSource, cardPlay);
                value += add;
                if (add != 0m)
                {
                    modifyingModels.Add(model);
                }
            }

            foreach (AbstractModel model in combatState.IterateHookListeners().ToList())
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

        value = ApplyDamagePreviewDynamicVarAndGlobalHooks(
            card,
            __instance.Name,
            value,
            __instance.Props,
            target,
            TryGetPreviewOwner(card, out Player? owner) ? owner.Creature : null,
            ModifyDamageHookType.All,
            previewMode,
            runGlobalHooks);

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
            if (TryGetPreviewOwner(card, out Player? owner) &&
                TryGetPreviewCreature(owner, out Creature? ownerCreature) &&
                TryGetPreviewCombatState(card, ownerCreature, out ICombatState? combatState))
            {
                value = ApplyBlockPreviewDynamicVarAndGlobalHooks(
                    card,
                    __instance.Name,
                    value,
                    __instance.Props,
                    runGlobalHooks);
            }
            else
            {
                value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
            }
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
        // Calculate() folds CalculationBase + ExtraDamage * multiplier. Card enchantments modify
        // that full attack amount, matching AttackCommand -> CreatureCmd.Damage -> Hook.ModifyDamage.
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        try
        {
            decimal calculatedBase = __instance.Calculate(target);
            decimal enchantedBase = ApplyCardDamageEnchantments(card, calculatedBase, __instance.Props, ModifyDamageHookType.All);
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

            decimal value = enchantedBase;
            if (runGlobalHooks)
            {
                if (TryGetPreviewOwner(card, out Player? owner) &&
                    TryGetPreviewRunState(owner, out IRunState? runState) &&
                    TryGetPreviewCreature(owner, out Creature? ownerCreature))
                {
                    ICombatState? combatState = card.CombatState ?? ownerCreature.CombatState;
                    List<AbstractModel> modifiers = new();
                    value = ModifyDamageInternal(
                        runState,
                        combatState,
                        target,
                        __instance.IsFromOsty ? owner.Osty : ownerCreature,
                        value,
                        __instance.Props,
                        card,
                        ModifyDamageHookType.All,
                        modifiers);
                }
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
        // Keep this in sync with the damage variant above: card enchantments modify the complete
        // calculated block amount before global block listeners run.
        if (!MultiEnchantmentSupport.RequiresMultiEnchantmentLogic(card))
        {
            return true;
        }

        try
        {
            decimal calculatedBase = __instance.Calculate(target);
            decimal enchantedBase = MultiEnchantmentSupport.ApplyBlockEnchantments(card, calculatedBase, __instance.Props);
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

            decimal value = enchantedBase;
            if (runGlobalHooks)
            {
                if (TryGetPreviewOwner(card, out Player? owner) &&
                    TryGetPreviewCreature(owner, out Creature? ownerCreature))
                {
                    ICombatState? combatState = card.CombatState ?? ownerCreature.CombatState;
                    value = ModifyBlockInternal(
                        combatState,
                        ownerCreature,
                        value,
                        __instance.Props,
                        card,
                        null,
                        new List<AbstractModel>());
                }
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
            if (TryGetPreviewOwner(card, out Player? owner) &&
                TryGetPreviewRunState(owner, out IRunState? runState) &&
                TryGetPreviewCreature(owner, out Creature? ownerCreature))
            {
                ICombatState? combatState = card.CombatState ?? ownerCreature.CombatState;
                value = ApplyDamagePreviewDynamicVarAndGlobalHooks(
                    card,
                    "damage",
                    value,
                    __instance.Props,
                    target,
                    owner.Osty,
                    ModifyDamageHookType.All,
                    previewMode,
                    runGlobalHooks);
            }
            else
            {
                value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, __instance.Name, value);
            }
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
                $"Card={card.Id} failed: {ex}");
        }
    }

    [HarmonyPatch(typeof(CardTransformation), nameof(CardTransformation.GetReplacement))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void CardTransformationGetReplacementPostfix(CardTransformation __instance, ref CardModel? __result)
    {
        if (__result == null)
        {
            return;
        }

        try
        {
            MultiEnchantmentTransformApi.CopyCompatibleEnchantments(__instance.Original, __result);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Failed to preserve compatible enchantments during transform. " +
                $"Original={__instance.Original.Id} Replacement={__result.Id}: {ex}");
        }
    }

    [HarmonyPatch(typeof(NTransformPreview), nameof(NTransformPreview.Initialize))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    private static void TransformPreviewInitializePrefix(ref IEnumerable<CardTransformation> cardTransformations)
    {
        cardTransformations = cardTransformations
            .Select(CreateTransformPreviewTransformation)
            .ToList();
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

    [HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
    [HarmonyPostfix]
    private static void CardVisualsPostfix(NCard __instance)
    {
        // Display-only extra icons must also work on card-library / encyclopedia cards that have
        // no live enchantment instance. Sync after the full visual pass so the marker is present
        // even if vanilla skipped UpdateEnchantmentVisuals for an unenchanted card.
        MultiEnchantmentSupport.SyncExtraEnchantmentTabs(__instance);
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
            MultiEnchantmentSupport.RefreshExtraTabTransformOnly(__instance.CardNode);
        }
    }

    [HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.SetTargetScale))]
    [HarmonyPostfix]
    private static void HandCardHolderTargetScalePostfix(NHandCardHolder __instance)
    {
        if (__instance.CardNode != null)
        {
            MultiEnchantmentSupport.RefreshExtraTabTransformOnly(__instance.CardNode);
        }
    }

    [HarmonyPatch(typeof(NCardPlay), "CenterCard")]
    [HarmonyPostfix]
    private static void CardPlayCenterCardPostfix(NCardPlay __instance)
    {
        MultiEnchantmentSupport.RefreshExtraTabsPreferInPlace(__instance.Holder?.CardNode);
    }

    [HarmonyPatch(
        typeof(NTargetManager),
        nameof(NTargetManager.StartTargeting),
        new[] { typeof(TargetType), typeof(Control), typeof(TargetMode), typeof(Func<bool>), typeof(Func<Node, bool>) })]
    [HarmonyPostfix]
    private static void TargetManagerStartCardTargetingPostfix(Control control)
    {
        if (control is NCard cardNode)
        {
            MultiEnchantmentSupport.RefreshExtraTabsPreferInPlace(cardNode);
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

    [HarmonyPatch(typeof(NDeckHistoryEntry), "Reload")]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    private static void DeckHistoryEntryReloadPostfix(NDeckHistoryEntry __instance)
    {
        // Base-game source: NDeckHistoryEntry.Reload.
        // Vanilla uses only Card.Enchantment for the purple title and icon. If the primary slot is
        // empty but the sidecar restored extra enchantments, mirror the same one-icon treatment.
        CardModel? card = __instance.Card;
        if (card == null ||
            card.Enchantment != null ||
            !MultiEnchantmentSupport.TryGetFirstVisualState(card, out MultiEnchantmentSupport.EnchantmentVisualState? visualState))
        {
            return;
        }

        if (NDeckHistoryEntryTitleLabelField?.GetValue(__instance) is Label titleLabel)
        {
            titleLabel.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.purple);
        }

        if (NDeckHistoryEntryEnchantmentImageField?.GetValue(__instance) is TextureRect enchantmentImage)
        {
            enchantmentImage.Texture = visualState.Icon;
            enchantmentImage.Visible = true;
        }
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

    private static bool TryGetPreviewOwner(CardModel card, [NotNullWhen(true)] out Player? owner)
    {
        owner = card.Owner;
        return owner != null;
    }

    private static bool TryGetPreviewRunState(Player owner, [NotNullWhen(true)] out IRunState? runState)
    {
        runState = owner.RunState;
        return runState != null;
    }

    private static bool TryGetPreviewCreature(Player owner, [NotNullWhen(true)] out Creature? creature)
    {
        creature = owner.Creature;
        return creature != null;
    }

    private static bool TryGetPreviewCombatState(
        CardModel card,
        Creature ownerCreature,
        [NotNullWhen(true)] out ICombatState? combatState)
    {
        combatState = card.CombatState ?? ownerCreature.CombatState;
        return combatState != null;
    }

    private static CardTransformation CreateTransformPreviewTransformation(CardTransformation transformation)
    {
        try
        {
            CardModel original = transformation.Original;
            if (transformation.Replacement != null)
            {
                return new CardTransformation(
                    original,
                    CreateTransformPreviewReplacement(original, transformation.Replacement));
            }

            IEnumerable<CardModel> options = transformation.ReplacementOptions ??
                                             CardFactory.GetDefaultTransformationOptions(original, transformation.IsInCombat);
            return new CardTransformation(
                original,
                options.Select(option => CreateTransformPreviewReplacement(original, option)).ToList());
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Failed to build multi-enchantment transform preview for " +
                $"{transformation.Original.Id}. Falling back to vanilla preview. {ex}");
            return transformation;
        }
    }

    private static CardModel CreateTransformPreviewReplacement(CardModel source, CardModel replacement)
    {
        CardModel preview = replacement.IsMutable
            ? (CardModel)replacement.MutableClone()
            : replacement.ToMutable();

        if (MultiEnchantmentTransformApi.TryGetTransformCopySource(replacement, out CardModel? copiedSource))
        {
            if (copiedSource != null)
            {
                MultiEnchantmentTransformApi.MarkTransformCopyState(copiedSource, preview);
            }

            if (ReferenceEquals(copiedSource, source))
            {
                return preview;
            }
        }

        MultiEnchantmentTransformApi.CopyCompatibleEnchantments(source, preview);
        return preview;
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
            // Vanilla CloneRestSiteOption.OnSelect 每张带 Clone 的卡只克隆一次，与 Clone.Amount 无关。
            // PaelsGrowth 给 Clone Amount=4，旧代码会乘出 4 张副本；改用 instance count 保持 vanilla 语义。
            int cloneCount = Math.Max(1, MultiEnchantmentStackSupport.GetEnchantmentCount(card, typeof(Clone)));
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

    private static decimal ApplyDamagePreviewDynamicVarAndGlobalHooks(
        CardModel card,
        string varKey,
        decimal value,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        bool runGlobalHooks)
    {
        value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, varKey, value);
        if (!runGlobalHooks ||
            !TryGetPreviewOwner(card, out Player? owner) ||
            !TryGetPreviewRunState(owner, out IRunState? runState))
        {
            return value;
        }

        ICombatState? combatState = card.CombatState ?? owner.Creature?.CombatState;
        if (target == null && previewMode == CardPreviewMode.MultiCreatureTargeting)
        {
            return ModifyDamagePreviewMultiTarget(
                runState,
                combatState,
                dealer,
                value,
                props,
                card,
                modifyDamageHookType);
        }

        return ModifyDamageInternal(
            runState,
            combatState,
            target,
            dealer,
            value,
            props,
            card,
            modifyDamageHookType,
            new List<AbstractModel>());
    }

    private static decimal ModifyDamagePreviewMultiTarget(
        IRunState runState,
        ICombatState? combatState,
        Creature? dealer,
        decimal value,
        ValueProp props,
        CardModel cardSource,
        ModifyDamageHookType modifyDamageHookType)
    {
        if (!ShouldUseMultiTargetDamagePreview(cardSource))
        {
            return ModifyDamageInternal(
                runState,
                combatState,
                null,
                dealer,
                value,
                props,
                cardSource,
                modifyDamageHookType,
                new List<AbstractModel>());
        }

        bool allEqual = true;
        decimal? sharedValue = null;
        foreach (Creature enemy in combatState?.HittableEnemies ?? Array.Empty<Creature>())
        {
            decimal targetValue = ModifyDamageInternal(
                runState,
                combatState,
                enemy,
                dealer,
                value,
                props,
                cardSource,
                modifyDamageHookType,
                new List<AbstractModel>());
            if (!sharedValue.HasValue)
            {
                sharedValue = targetValue;
            }
            else if ((int)targetValue != (int)sharedValue.Value)
            {
                allEqual = false;
                break;
            }
        }

        return sharedValue.HasValue && allEqual ? Math.Max(0m, sharedValue.Value) : Math.Max(0m, value);
    }

    private static bool ShouldUseMultiTargetDamagePreview(CardModel cardSource)
    {
        TargetType targetType = cardSource.TargetType;
        if ((uint)(targetType - 3) > 1u)
        {
            return false;
        }

        CardPile? pile = cardSource.Pile;
        return pile != null && (pile.Type == PileType.Hand || pile.Type == PileType.Play);
    }

    private static decimal ApplyBlockPreviewDynamicVarAndGlobalHooks(
        CardModel card,
        string varKey,
        decimal value,
        ValueProp props,
        bool runGlobalHooks)
    {
        value = MultiEnchantmentSupport.ApplyDynamicVarEnchantments(card, varKey, value);
        if (!runGlobalHooks ||
            !TryGetPreviewOwner(card, out Player? owner) ||
            !TryGetPreviewCreature(owner, out Creature? ownerCreature))
        {
            return value;
        }

        ICombatState? combatState = card.CombatState ?? ownerCreature.CombatState;
        return ModifyBlockInternal(
            combatState,
            ownerCreature,
            value,
            props,
            card,
            null,
            new List<AbstractModel>());
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
            foreach (AbstractModel model in runState.IterateHookListeners(combatState).ToList())
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
            foreach (AbstractModel model in runState.IterateHookListeners(combatState).ToList())
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
        foreach (AbstractModel model in runState.IterateHookListeners(combatState).ToList())
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

        foreach (AbstractModel model in combatState.IterateHookListeners().ToList())
        {
            decimal add = model.ModifyBlockAdditive(target, value, props, cardSource, cardPlay);
            value += add;
            if (add != 0m)
            {
                modifiers.Add(model);
            }
        }

        foreach (AbstractModel model in combatState.IterateHookListeners().ToList())
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
        handDraw = MultiEnchantmentSupport.ApplyHandDrawContributions(state, player, handDraw);

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

    // === Battle history display customization ===================================================

    private static readonly FieldInfo? HistoryTipActionStatsField =
        AccessTools.Field(typeof(NMapPointHistoryHoverTip), "_actionStats");
    private static readonly FieldInfo? HistoryTipEnchantedLocField =
        AccessTools.Field(typeof(NMapPointHistoryHoverTip), "_enchanted");
    private static readonly FieldInfo? HistoryTipRewardRowsField =
        AccessTools.Field(typeof(NMapPointHistoryHoverTip), "_rewardRows");
    private static readonly FieldInfo? HistoryTipRewardContainerField =
        AccessTools.Field(typeof(NMapPointHistoryHoverTip), "_rewardStatsContainer");

    [ThreadStatic] private static List<CardEnchantmentHistoryEntry>? _savedCardsEnchanted;

    private readonly struct SeparatedHistoryEntry
    {
        public readonly CardEnchantmentHistoryEntry Entry;
        public readonly HistoryDisplayMode Mode;
        public readonly string? GroupHeader;
        public readonly HistoryTextFormatter? Formatter;

        public SeparatedHistoryEntry(
            CardEnchantmentHistoryEntry entry, HistoryDisplayMode mode,
            string? groupHeader, HistoryTextFormatter? formatter)
        {
            Entry = entry;
            Mode = mode;
            GroupHeader = groupHeader;
            Formatter = formatter;
        }
    }

    [ThreadStatic] private static List<SeparatedHistoryEntry>? _separatedHistoryEntries;

    [HarmonyPatch(typeof(NMapPointHistoryHoverTip), "PopulateRewardAndSkippedEntries")]
    [HarmonyPrefix]
    private static void PopulateRewardAndSkippedEntriesPrefix(PlayerMapPointHistoryEntry playerEntry)
    {
        _savedCardsEnchanted = null;
        _separatedHistoryEntries = null;

        List<CardEnchantmentHistoryEntry> original = playerEntry.CardsEnchanted;
        if (original.Count == 0) return;

        List<SeparatedHistoryEntry>? separated = null;

        for (int i = original.Count - 1; i >= 0; i--)
        {
            CardEnchantmentHistoryEntry entry = original[i];
            EnchantmentModel enchModel = SaveUtil.EnchantmentOrDeprecated(entry.Enchantment);
            Type enchType = enchModel.GetType();

            HistoryDisplayMode mode = EnchantmentRegistry.GetHistoryDisplayMode(enchType);
            if (mode == HistoryDisplayMode.Auto)
            {
                mode = HistoryDisplayMode.InRewards;
            }

            if (mode == HistoryDisplayMode.Hidden)
            {
                _savedCardsEnchanted ??= new List<CardEnchantmentHistoryEntry>(original);
                original.RemoveAt(i);
                continue;
            }

            if (mode is HistoryDisplayMode.InActions or HistoryDisplayMode.CustomGroup)
            {
                _savedCardsEnchanted ??= new List<CardEnchantmentHistoryEntry>(original);
                string? groupHeader = mode == HistoryDisplayMode.CustomGroup
                    ? EnchantmentRegistry.GetHistoryGroupHeader(enchType)
                    : null;
                HistoryTextFormatter? formatter = EnchantmentRegistry.GetHistoryTextFormatter(enchType);
                separated ??= new();
                separated.Add(new SeparatedHistoryEntry(entry, mode, groupHeader, formatter));
                original.RemoveAt(i);
                continue;
            }

            HistoryTextFormatter? rewFormatter = EnchantmentRegistry.GetHistoryTextFormatter(enchType);
            if (rewFormatter != null)
            {
                _savedCardsEnchanted ??= new List<CardEnchantmentHistoryEntry>(original);
                separated ??= new();
                separated.Add(new SeparatedHistoryEntry(entry, HistoryDisplayMode.InRewards, null, rewFormatter));
                original.RemoveAt(i);
            }
        }

        if (separated != null)
        {
            separated.Reverse();
            _separatedHistoryEntries = separated;
        }
    }

    [HarmonyPatch(typeof(NMapPointHistoryHoverTip), "PopulateRewardAndSkippedEntries")]
    [HarmonyPostfix]
    private static void PopulateRewardAndSkippedEntriesPostfix(
        NMapPointHistoryHoverTip __instance,
        PlayerMapPointHistoryEntry playerEntry)
    {
        if (_savedCardsEnchanted != null)
        {
            playerEntry.CardsEnchanted.Clear();
            playerEntry.CardsEnchanted.AddRange(_savedCardsEnchanted);
            _savedCardsEnchanted = null;
        }

        if (_separatedHistoryEntries == null) return;

        try
        {
            RichTextLabel? actionStats = HistoryTipActionStatsField?.GetValue(__instance) as RichTextLabel;
            LocString? enchantedLoc = HistoryTipEnchantedLocField?.GetValue(__instance) as LocString;
            List<RichTextLabel>? rewardRows = HistoryTipRewardRowsField?.GetValue(__instance) as List<RichTextLabel>;
            Control? rewardContainer = HistoryTipRewardContainerField?.GetValue(__instance) as Control;

            List<string>? rewardTexts = null;
            StringBuilder? actionText = null;
            Dictionary<string, List<string>>? customGroups = null;

            foreach (SeparatedHistoryEntry sep in _separatedHistoryEntries)
            {
                string cardTitle = CardModel.FromSerializable(sep.Entry.Card)?.Title ?? "";
                string enchTitle = SaveUtil.EnchantmentOrDeprecated(sep.Entry.Enchantment).Title.GetFormattedText() ?? "";

                string text;
                if (sep.Formatter != null)
                {
                    text = sep.Formatter(cardTitle, enchTitle)
                        ?? FormatEnchantedHistoryText(enchantedLoc, cardTitle, enchTitle);
                }
                else
                {
                    text = FormatEnchantedHistoryText(enchantedLoc, cardTitle, enchTitle);
                }

                switch (sep.Mode)
                {
                    case HistoryDisplayMode.InRewards:
                        rewardTexts ??= new();
                        rewardTexts.Add(text);
                        break;
                    case HistoryDisplayMode.InActions:
                        actionText ??= new();
                        actionText.Append(text).Append('\n');
                        break;
                    case HistoryDisplayMode.CustomGroup:
                        string header = sep.GroupHeader ?? "Enchantments";
                        customGroups ??= new();
                        if (!customGroups.TryGetValue(header, out List<string>? groupList))
                        {
                            groupList = new();
                            customGroups[header] = groupList;
                        }
                        groupList.Add(text);
                        break;
                }
            }

            if (rewardTexts != null && rewardRows is { Count: > 0 } && rewardContainer != null)
            {
                rewardContainer.Visible = true;
                foreach (string text in rewardTexts)
                {
                    rewardRows[0].Text += "\n\t" + text;
                }
            }

            if (actionText is { Length: > 0 } && actionStats != null)
            {
                string existing = actionStats.Text;
                string newText = actionText.ToString().TrimEnd('\n');
                actionStats.Text = string.IsNullOrEmpty(existing)
                    ? newText
                    : existing + "\n" + newText;
            }

            if (customGroups != null && actionStats != null)
            {
                StringBuilder sb = new();
                foreach (KeyValuePair<string, List<string>> kvp in customGroups)
                {
                    sb.Append('\n').Append(kvp.Key).Append('\n');
                    foreach (string text in kvp.Value)
                    {
                        sb.Append('\t').Append(text).Append('\n');
                    }
                }

                string existing = actionStats.Text;
                string groupText = sb.ToString().TrimEnd('\n');
                actionStats.Text = string.IsNullOrEmpty(existing)
                    ? groupText.TrimStart('\n')
                    : existing + groupText;
            }
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] History display postfix failed: {ex}");
        }
        finally
        {
            _separatedHistoryEntries = null;
        }
    }

    private static string FormatEnchantedHistoryText(LocString? enchantedLoc, string cardTitle, string enchTitle)
    {
        if (enchantedLoc != null)
        {
            enchantedLoc.Add("Icon", "[img=top]res://images/packed/sprite_fonts/card_icon.png[/img]");
            enchantedLoc.Add("Title1", cardTitle);
            enchantedLoc.Add("Title2", enchTitle);
            return enchantedLoc.GetFormattedText() ?? $"{cardTitle} → {enchTitle}";
        }

        return $"{cardTitle} → {enchTitle}";
    }
}

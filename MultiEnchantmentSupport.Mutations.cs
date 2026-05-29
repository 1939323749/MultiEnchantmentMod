using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

internal static partial class MultiEnchantmentSupport
{
    public static bool NormalizeCardEnchantmentStacks(CardModel card)
    {
        bool changed = false;
        HashSet<Type> seenDisallowDuplicateTypes = new();
        foreach (EnchantmentModel enchantment in GetEnchantments(card).ToList())
        {
            EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());
            if (behavior == EnchantmentStackBehavior.MergeAmount)
            {
                MultiEnchantmentStackSupport.InitializeMergedStackMetadata(enchantment);
                continue;
            }

            if (behavior == EnchantmentStackBehavior.DisallowDuplicate)
            {
                if (!seenDisallowDuplicateTypes.Add(enchantment.GetType()))
                {
                    changed |= RemoveAdditionalEnchantmentState(card, enchantment);
                    continue;
                }

                if (enchantment.Amount > 1)
                {
                    enchantment.Amount = 1;
                    MultiEnchantmentStackSupport.ClearMergedStackMetadata(enchantment);
                    RememberLastAppliedEnchantment(card, enchantment);
                    changed = true;
                }

                continue;
            }

            // Only existence stacks are safe to normalize from legacy "Amount > 1" cards here.
            // Duplicate-instance enchantments may use Amount as live per-instance state.
            if (behavior != EnchantmentStackBehavior.ExistenceStack || enchantment.Amount <= 1)
            {
                continue;
            }

            int extraInstanceCount = enchantment.Amount - 1;
            enchantment.Amount = 1;
            RememberLastAppliedEnchantment(card, enchantment);

            for (int i = 0; i < extraInstanceCount; i++)
            {
                EnchantmentModel clone = (EnchantmentModel)enchantment.ClonePreservingMutability();
                AttachAdditionalEnchantmentState(card, clone, 1, modifyCard: true, triggerChanged: false);
            }

            changed = true;
        }

        if (changed)
        {
            RebuildApplicationOrder(card);
        }

        return changed;
    }

    public static EnchantmentModel? ApplyEnchantment(EnchantmentModel enchantment, CardModel card, decimal amount)
    {
        return ApplyEnchantmentWithScopeOverride(enchantment, card, amount, scopeOverride: null);
    }

    internal static EnchantmentModel? ApplyEnchantmentWithScopeOverride(
        EnchantmentModel enchantment,
        CardModel card,
        decimal amount,
        EnchantmentScope? scopeOverride)
    {
        enchantment.AssertMutable();
        int appliedAmount = ValidateAndConvertStackAmount(amount, nameof(amount));

        bool isStackingExisting = MultiEnchantmentStackSupport.CanStackOnto(card, enchantment.GetType());
        bool canApply = isStackingExisting
            ? MultiEnchantmentStackSupport.PassesCanEnchantRulesIgnoringDuplicate(enchantment, card)
            : enchantment.CanEnchant(card);
        if (!canApply)
        {
            throw new InvalidOperationException($"Cannot enchant {card.Id} with {enchantment.Id}.");
        }

        SeedMissingApplicationOrder(card);

        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());
        EnchantmentModel? existing = GetEnchantment(card, enchantment.GetType());
        if (existing != null && behavior == EnchantmentStackBehavior.MergeAmount)
        {
            int addedAmount = appliedAmount;
            int previousTotalAmount = existing.Amount;
            existing.Amount += addedAmount;
            MultiEnchantmentStackSupport.AppendMergedStackAmount(existing, previousTotalAmount, addedAmount);
            MultiEnchantmentStackSupport.ApplyMergedAmountDelta(existing, addedAmount);
            MultiEnchantmentStackSupport.RefreshMergedEnchantmentState(existing);
            MultiEnchantmentScopeSupport.SetScopeOverrideOnApply(card, existing, scopeOverride);
            if (IsScopeEffectivelyPermanent(existing.GetType(), scopeOverride))
            {
                SyncDeckVersionEnchantment(card, existing.GetType(), addedAmount, behavior, scopeOverride);
            }
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
            card.FinalizeUpgradeInternal();
            RememberLastAppliedEnchantment(card, existing);
            AppendApplicationOrder(card, enchantment.Id);
            MultiEnchantmentScopeSupport.RefreshActiveStatuses(card);
            MultiEnchantmentStackSupport.RefreshDerivedState(card);
            TriggerEnchantmentChanged(card);
            RecordEnchantmentHistory(card, enchantment);
            return existing;
        }

        EnchantmentModel? applied = AttachNewEnchantmentStacks(
            card,
            enchantment,
            appliedAmount,
            modifyCard: true,
            triggerChanged: false,
            out int appliedStackCount,
            scopeOverride);
        if (applied == null)
        {
            return null;
        }

        if (IsScopeEffectivelyPermanent(applied.GetType(), scopeOverride))
        {
            SyncDeckVersionEnchantment(card, applied.GetType(), appliedStackCount, behavior, scopeOverride);
        }

        card.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(card);
        TriggerEnchantmentChanged(card);
        RecordEnchantmentHistory(card, enchantment);
        return applied;
    }

    public static EnchantmentModel? AddAdditionalEnchantment(CardModel card, EnchantmentModel enchantment, decimal amount, bool modifyCard, bool triggerChanged)
    {
        // Public "add extra enchantment" API means "apply new stacks now", not "restore a saved
        // instance state". Restores must go through RestoreAdditionalEnchantmentState().
        return AttachNewAdditionalEnchantmentStacks(
            card,
            enchantment,
            ValidateAndConvertStackAmount(amount, nameof(amount)),
            modifyCard,
            triggerChanged);
    }

    private static EnchantmentModel? AttachNewEnchantmentStacks(
        CardModel card,
        EnchantmentModel enchantment,
        int stackCount,
        bool modifyCard,
        bool triggerChanged,
        out int appliedStackCount,
        EnchantmentScope? scopeOverride = null)
    {
        appliedStackCount = 0;

        // New applications may need to fan out one requested stack count into multiple concrete
        // enchantment instances when the behavior is DuplicateInstance/ExistenceStack.
        enchantment.AssertMutable();
        card.AssertMutable();
        SeedMissingApplicationOrder(card);

        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());

        // Phase 4-9: enforce MaxInstances overflow policy before attaching new instances.
        int allowedStackCount = EnforceOverflowPolicy(card, enchantment.GetType(), stackCount, behavior);
        if (allowedStackCount <= 0)
        {
            return null;
        }

        appliedStackCount = allowedStackCount;

        if (ShouldFanOutAppliedStacks(behavior) && allowedStackCount > 1)
        {
            EnchantmentModel firstApplied = AttachEnchantmentState(
                card,
                enchantment,
                1,
                modifyCard,
                triggerChanged: false,
                scopeOverride);
            AppendApplicationOrder(card, enchantment.Id);
            for (int i = 1; i < allowedStackCount; i++)
            {
                EnchantmentModel extra = (EnchantmentModel)enchantment.ClonePreservingMutability();
                AttachEnchantmentState(card, extra, 1, modifyCard, triggerChanged: false, scopeOverride);
                AppendApplicationOrder(card, extra.Id);
            }

            if (triggerChanged)
            {
                TriggerEnchantmentChanged(card);
            }

            return firstApplied;
        }

        EnchantmentModel applied = AttachEnchantmentState(card, enchantment, allowedStackCount, modifyCard, triggerChanged, scopeOverride);
        AppendApplicationOrder(card, applied.Id);
        return applied;
    }

    private static EnchantmentModel? AttachNewAdditionalEnchantmentStacks(
        CardModel card,
        EnchantmentModel enchantment,
        int stackCount,
        bool modifyCard,
        bool triggerChanged,
        EnchantmentScope? scopeOverride = null)
    {
        enchantment.AssertMutable();
        card.AssertMutable();
        SeedMissingApplicationOrder(card);
        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());

        // Phase 4-9: enforce MaxInstances overflow policy before attaching new instances.
        int allowedStackCount = EnforceOverflowPolicy(card, enchantment.GetType(), stackCount, behavior);
        if (allowedStackCount <= 0)
        {
            return null;
        }

        if (ShouldFanOutAppliedStacks(behavior) && allowedStackCount > 1)
        {
            EnchantmentModel firstApplied = AttachAdditionalEnchantmentState(
                card,
                enchantment,
                1,
                modifyCard,
                triggerChanged: false,
                scopeOverride);
            AppendApplicationOrder(card, enchantment.Id);
            for (int i = 1; i < allowedStackCount; i++)
            {
                EnchantmentModel clone = (EnchantmentModel)enchantment.ClonePreservingMutability();
                AttachAdditionalEnchantmentState(
                    card,
                    clone,
                    1,
                    modifyCard,
                    triggerChanged: false,
                    scopeOverride);
                AppendApplicationOrder(card, clone.Id);
            }

            if (triggerChanged)
            {
                TriggerEnchantmentChanged(card);
            }

            return firstApplied;
        }

        EnchantmentModel applied = AttachAdditionalEnchantmentState(card, enchantment, allowedStackCount, modifyCard, triggerChanged, scopeOverride);
        AppendApplicationOrder(card, applied.Id);
        return applied;
    }

    private static EnchantmentModel AttachAdditionalEnchantmentState(
        CardModel card,
        EnchantmentModel enchantment,
        int amount,
        bool modifyCard,
        bool triggerChanged,
        EnchantmentScope? scopeOverride = null,
        bool dispatchAppliedLifecycle = true)
    {
        // Low-level exact-state attach. This method never interprets Amount as "how many more
        // stacks to create"; it attaches one concrete enchantment instance with the given state.
        enchantment.AssertMutable();
        card.AssertMutable();
        enchantment.ApplyInternal(card, amount);
        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        state.ExtraEnchantments.Add(enchantment);
        state.LastAppliedEnchantment = enchantment;
        MultiEnchantmentScopeSupport.SetScopeOverrideOnApply(card, enchantment, scopeOverride);

        if (modifyCard)
        {
            bool isFirstOfTypeOnCard = MultiEnchantmentStackSupport.GetEnchantmentCount(card, enchantment.GetType()) == 1;
            ApplyInitialEnchantmentState(enchantment, isFirstOfTypeOnCard);
            if (dispatchAppliedLifecycle)
            {
                MultiEnchantmentScopeSupport.DispatchOnApplied(card, enchantment);

                // Phase 5: notify siblings that a new enchantment joined the card.
                MultiEnchantmentScopeSupport.DispatchOnSiblingApplied(card, newcomer: enchantment);
            }
        }

        // Sync active-status predicate immediately so the enchantment dims on first appearance.
        MultiEnchantmentScopeSupport.RefreshActiveStatuses(card);

        if (triggerChanged)
        {
            TriggerEnchantmentChanged(card);
        }

        return enchantment;
    }

    private static EnchantmentModel RestoreAdditionalEnchantmentState(
        CardModel card,
        EnchantmentModel enchantment,
        bool modifyCard,
        bool triggerChanged,
        bool dispatchAppliedLifecycle = true)
    {
        // Mod source: cloning/loading an existing extra enchantment must preserve that instance's
        // live Amount. Duplicate-instance enchantments like Goopy use Amount as runtime state, not
        // "how many additional copies to fan out".
        return AttachAdditionalEnchantmentState(
            card,
            enchantment,
            enchantment.Amount,
            modifyCard,
            triggerChanged,
            scopeOverride: null,
            dispatchAppliedLifecycle);
    }

    /// <summary>
    /// Phase 4-9: enforces the configured <see cref="Api.StackOverflowPolicy"/> when attaching
    /// new instances would push the card past <see cref="Api.StackDefinition.MaxInstances"/>.
    /// For <c>ReplaceOldest</c> / <c>ReplaceNewest</c>, evicts existing instances in the right
    /// direction. For <c>Reject</c>, rejects the whole incoming batch if it would overflow.
    /// </summary>
    private static int EnforceOverflowPolicy(
        CardModel card,
        Type enchantmentType,
        int incomingCount,
        EnchantmentStackBehavior behavior)
    {
        if (behavior is not (EnchantmentStackBehavior.DuplicateInstance or EnchantmentStackBehavior.ExistenceStack))
        {
            return incomingCount;
        }

        int? cap = Api.Internal.EnchantmentRegistry.GetMaxInstances(enchantmentType);
        if (!cap.HasValue) return incomingCount;
        if (cap.Value <= 0)
        {
            MultiEnchantmentStackSupport.LogMaxInstancesRejection(
                enchantmentType,
                MultiEnchantmentStackSupport.GetEnchantmentCount(card, enchantmentType),
                cap.Value);
            return 0;
        }

        Api.StackOverflowPolicy policy = Api.Internal.EnchantmentRegistry.GetOverflowPolicy(enchantmentType);
        if (policy == Api.StackOverflowPolicy.Reject)
        {
            List<EnchantmentModel> rejectedExisting = GetEnchantments(card)
                .Where(e => e.GetType() == enchantmentType)
                .ToList();
            if (rejectedExisting.Count + incomingCount > cap.Value)
            {
                MultiEnchantmentStackSupport.LogMaxInstancesRejection(enchantmentType, rejectedExisting.Count, cap.Value);
                return 0;
            }

            return incomingCount;
        }

        // Snapshot current matching instances in card application order. ExtraEnchantments is
        // append-on-attach, so its order matches application order for that type.
        List<EnchantmentModel> existing = GetEnchantments(card)
            .Where(e => e.GetType() == enchantmentType)
            .ToList();
        int totalAfter = existing.Count + incomingCount;
        int evictionsNeeded = totalAfter - cap.Value;
        if (evictionsNeeded <= 0) return incomingCount;

        int evicted = 0;

        IEnumerable<EnchantmentModel> evictionOrder = policy == Api.StackOverflowPolicy.ReplaceOldest
            ? existing
            : ((IEnumerable<EnchantmentModel>)existing).Reverse();

        foreach (EnchantmentModel victim in evictionOrder)
        {
            // Skip the primary slot — vanilla `card.Enchantment` is owned by the upgrade pipeline
            // and shouldn't be removed here. ReplaceOldest/Newest applies only to the v2
            // multi-instance pool.
            if (ReferenceEquals(card.Enchantment, victim)) continue;

            RemoveEnchantmentInternal(
                card, victim, Api.RemovalReason.OverflowEvicted,
                bypassVeto: true, refreshCard: false, triggerChanged: false);
            evicted++;
            if (evicted >= evictionsNeeded)
            {
                break;
            }
        }

        int remainingOverflow = evictionsNeeded - evicted;
        if (remainingOverflow <= 0)
        {
            return incomingCount;
        }

        int allowedIncoming = incomingCount - remainingOverflow;
        if (allowedIncoming <= 0)
        {
            MultiEnchantmentStackSupport.LogMaxInstancesRejection(enchantmentType, existing.Count - evicted, cap.Value);
            return 0;
        }

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment] Could only evict {evicted} {enchantmentType.FullName} instance(s) " +
            $"before hitting primary-slot instances; applying {allowedIncoming} of {incomingCount} requested stack(s).");
        return allowedIncoming;
    }

    private static bool RemoveAdditionalEnchantmentState(CardModel card, EnchantmentModel enchantment)
    {
        if (!CardStates.TryGetValue(card, out CardEnchantmentState? state))
        {
            return false;
        }

        if (!state.ExtraEnchantments.Remove(enchantment))
        {
            return false;
        }

        RemoveOneApplicationOrder(state, enchantment.Id);
        state.ScopeStates.Remove(enchantment);
        state.PendingRemovals.RemoveAll(entry => ReferenceEquals(entry.Enchantment, enchantment));
        enchantment.ClearInternal();
        if (ReferenceEquals(state.LastAppliedEnchantment, enchantment))
        {
            state.LastAppliedEnchantment = null;
        }

        PruneEmptyCardState(card, state);
        return true;
    }

    public static void ClearAdditionalEnchantments(CardModel card, bool triggerChanged)
    {
        foreach (EnchantmentModel enchantment in GetOrderedEnchantmentsForRemoval(card)
                     .Where(static enchantment => !ReferenceEquals(enchantment.Card?.Enchantment, enchantment))
                     .ToList())
        {
            RemoveEnchantmentInternal(card, enchantment, RemovalReason.CardCleared, bypassVeto: true, refreshCard: false, triggerChanged: false);
        }

        MultiEnchantmentStackSupport.RefreshDerivedState(card);

        if (triggerChanged)
        {
            TriggerEnchantmentChanged(card);
        }
    }

    public static void CloneAdditionalEnchantments(CardModel source, CardModel clone)
    {
        bool changed = false;
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(source))
        {
            EnchantmentModel cloned = (EnchantmentModel)enchantment.ClonePreservingMutability();
            RestoreAdditionalEnchantmentState(clone, cloned, modifyCard: true, triggerChanged: false);
            CopyScopeState(source, enchantment, clone, cloned);
            changed = true;
        }

        CopyApplicationOrder(source, clone);

        changed = NormalizeCardEnchantmentStacks(clone) || changed;

        if (changed)
        {
            clone.FinalizeUpgradeInternal();
            MultiEnchantmentStackSupport.RefreshDerivedState(clone);
            TriggerEnchantmentChanged(clone);
        }
    }

    public static void CloneCompatibleEnchantments(CardModel source, CardModel target)
    {
        bool changed = false;
        foreach (EnchantmentModel enchantment in GetEnchantments(source))
        {
            EnchantmentModel cloned = (EnchantmentModel)enchantment.ClonePreservingMutability();
            if (!cloned.CanEnchant(target))
            {
                continue;
            }

            if (target.Enchantment == null)
            {
                AttachEnchantmentState(target, cloned, cloned.Amount, modifyCard: true, triggerChanged: false);
            }
            else
            {
                RestoreAdditionalEnchantmentState(target, cloned, modifyCard: true, triggerChanged: false);
            }

            CopyScopeState(source, enchantment, target, cloned);
            changed = true;
        }

        CopyApplicationOrder(source, target);

        changed = NormalizeCardEnchantmentStacks(target) || changed;

        if (changed)
        {
            target.FinalizeUpgradeInternal();
            MultiEnchantmentStackSupport.RefreshDerivedState(target);
            TriggerEnchantmentChanged(target);
        }
    }

    internal static bool RemoveEnchantmentInternal(
        CardModel card,
        EnchantmentModel enchantment,
        RemovalReason reason,
        bool bypassVeto,
        bool refreshCard = true,
        bool triggerChanged = true)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);

        bool isPrimary = ReferenceEquals(card.Enchantment, enchantment);
        bool isExtra = GetAdditionalEnchantments(card).Contains(enchantment);
        if (!isPrimary && !isExtra)
        {
            return false;
        }

        if (!bypassVeto)
        {
            EnchantmentEntry? lifecycle = EnchantmentRegistry.GetDefinitionEntry(
                enchantment.GetType(),
                static entry => entry.OnRemoved != null);
            if (lifecycle != null && !lifecycle.RunOnRemoved(card, enchantment, reason))
            {
                return false;
            }
        }

        CardEnchantmentState? state = CardStates.TryGetValue(card, out CardEnchantmentState? existingState)
            ? existingState
            : null;
        EnchantmentScope? removedOverrideScope = GetScopeOverride(state, enchantment);
        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());
        int? deckSyncedInstanceOrdinal = IsScopeEffectivelyPermanent(enchantment.GetType(), removedOverrideScope)
            ? GetDeckSyncedInstanceOrdinal(card, enchantment)
            : null;

        if (isPrimary)
        {
            if (CardModelClearEnchantmentInternalMethod == null)
            {
                throw new InvalidOperationException("Failed to access CardModel.ClearEnchantmentInternal.");
            }

            CardModelClearEnchantmentInternalMethod.Invoke(card, null);
        }
        else
        {
            // Phase 5: notify siblings before the enchantment is actually removed from the list,
            // so handlers see the leaving enchantment at its current state.
            MultiEnchantmentScopeSupport.DispatchOnSiblingRemoved(card, leaving: enchantment, reason);
            if (!RemoveAdditionalEnchantmentState(card, enchantment))
            {
                return false;
            }
        }

        state ??= CardStates.TryGetValue(card, out CardEnchantmentState? updatedState) ? updatedState : null;

        if (state != null)
        {
            if (isPrimary)
            {
                RebuildApplicationOrder(card);
            }
            state.ScopeStates.Remove(enchantment);
            state.PendingRemovals.RemoveAll(entry => ReferenceEquals(entry.Enchantment, enchantment));
            if (ReferenceEquals(state.LastAppliedEnchantment, enchantment))
            {
                state.LastAppliedEnchantment = null;
            }

            PruneEmptyCardState(card, state);
        }

        // Sync removal to deck version only for permanently-scoped enchantments. Transient
        // enchantments (UntilCombatEnds / UntilTurnEnds / LingerForTurns / MaxActivations)
        // were never synced to the deck version on apply, so removing them from the deck
        // version would silently destroy any permanent copy of the same type.
        if (IsScopeEffectivelyPermanent(enchantment.GetType(), removedOverrideScope))
        {
            SyncDeckVersionEnchantmentRemoval(
                card,
                enchantment.GetType(),
                behavior,
                deckSyncedInstanceOrdinal,
                Math.Max(1, enchantment.Amount));
        }

        if (refreshCard)
        {
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
            card.FinalizeUpgradeInternal();
            MultiEnchantmentStackSupport.RefreshDerivedState(card);
            if (triggerChanged)
            {
                TriggerEnchantmentChanged(card);
            }
        }

        return true;
    }

    private static void ApplyInitialEnchantmentState(EnchantmentModel enchantment, bool isFirstOfTypeOnCard)
    {
        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());
        if (behavior == EnchantmentStackBehavior.MergeAmount)
        {
            // Mod source: merged stacks are saved/cloned as one enchantment instance with Amount > 1.
            // Reconstruct their state from the total amount instead of calling ModifyCard(), because
            // ModifyCard() only replays OnEnchant() once regardless of Amount.
            MultiEnchantmentStackSupport.InitializeMergedStackMetadata(enchantment);
            MultiEnchantmentStackSupport.ApplyMergedAmountDelta(enchantment, enchantment.Amount);
            MultiEnchantmentStackSupport.RefreshMergedEnchantmentState(enchantment);
            return;
        }

        if (behavior == EnchantmentStackBehavior.ExistenceStack && !isFirstOfTypeOnCard)
        {
            // Mod source: existence-style stacks keep additional instances for later hooks/UI, but
            // only the first instance is allowed to mutate the card's base state via OnEnchant().
            enchantment.RecalculateValues();
            enchantment.Card.DynamicVars.RecalculateForUpgradeOrEnchant();
            return;
        }

        enchantment.ModifyCard();
    }

    private static EnchantmentModel AttachEnchantmentState(
        CardModel card,
        EnchantmentModel enchantment,
        int amount,
        bool modifyCard,
        bool triggerChanged,
        EnchantmentScope? scopeOverride = null)
    {
        enchantment.AssertMutable();
        card.AssertMutable();
        if (card.Enchantment == null)
        {
            // Match the base-game "primary enchantment" path first so downstream code that expects
            // CardModel.Enchantment to be populated continues to behave like vanilla.
            card.EnchantInternal(enchantment, amount);
            MultiEnchantmentScopeSupport.SetScopeOverrideOnApply(card, enchantment, scopeOverride);
            if (modifyCard)
            {
                ApplyInitialEnchantmentState(enchantment, isFirstOfTypeOnCard: true);
                MultiEnchantmentScopeSupport.DispatchOnApplied(card, enchantment);
            }

            // Sync active-status predicate immediately for the primary enchantment.
            MultiEnchantmentScopeSupport.RefreshActiveStatuses(card);

            RememberLastAppliedEnchantment(card, enchantment);
            return enchantment;
        }

        return AttachAdditionalEnchantmentState(card, enchantment, amount, modifyCard, triggerChanged);
    }

    private static bool ShouldFanOutAppliedStacks(EnchantmentStackBehavior behavior)
    {
        return behavior is EnchantmentStackBehavior.DuplicateInstance or EnchantmentStackBehavior.ExistenceStack;
    }

    private static int ValidateAndConvertStackAmount(decimal amount, string paramName)
    {
        // Vanilla CardCmd.Enchant accepts any decimal and forwards it to EnchantInternal without
        // validation, so the dev console's `enchant <id>` (with no explicit number) passes 0 and
        // vanilla treats that as "apply once". Mirror that ergonomic — coerce non-positive
        // amounts to 1 rather than throwing, otherwise our prefix throws, falls back to vanilla,
        // and vanilla's "already enchanted" check then refuses any different-type second
        // enchantment.
        if (amount <= 0)
        {
            return 1;
        }

        if (decimal.Truncate(amount) != amount)
        {
            throw new ArgumentOutOfRangeException(paramName, amount, "Enchantment amount must be a positive integer.");
        }

        if (amount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(paramName, amount, "Enchantment amount is too large.");
        }

        return decimal.ToInt32(amount);
    }
}

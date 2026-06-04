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
    public static void SetEnchantedValue(DynamicVar dynamicVar, decimal value)
    {
        EnchantedValueProperty?.SetValue(dynamicVar, value);
    }

    public static void ResetEnchantedValue(DynamicVar dynamicVar)
    {
        SetEnchantedValue(dynamicVar, dynamicVar.BaseValue);
    }

    public static void SyncEnchantVfxPresentation(
        Node vfxNode,
        CardModel? card,
        NCard? cardNode,
        TextureRect? templateIcon)
    {
        if (card == null ||
            cardNode == null ||
            templateIcon?.GetParent() is not TextureRect templateBadge ||
            templateBadge.GetParent() is not Node badgeRoot)
        {
            return;
        }

        ClearNamedChildren(badgeRoot, EnchantVfxViewportBadgePrefix);
        Node? cardBadgeRoot = cardNode.EnchantmentTab.GetParent();
        if (cardBadgeRoot == null)
        {
            return;
        }

        ClearNamedChildren(cardBadgeRoot, EnchantVfxStaticBadgePrefix);

        List<EnchantmentVisualState> visualStates = ConsumeEnchantVfxSnapshot(vfxNode, card);
        if (visualStates.Count == 0)
        {
            return;
        }

        Control primaryTab = cardNode.EnchantmentTab;
        Vector2 defaultPosition = NCardDefaultEnchantmentPositionField?.GetValue(cardNode) is Vector2 position
            ? position
            : Vector2.Zero;
        List<EnchantmentSlotLayout> slotLayouts = BuildEnchantmentSlotLayouts(
            cardNode,
            primaryTab,
            visualStates,
            defaultPosition);
        if (slotLayouts.Count != visualStates.Count)
        {
            return;
        }

        int animatedIndex = visualStates.Count - 1;
        ApplyEnchantmentVisualState(templateBadge, visualStates[animatedIndex]);
        templateBadge.Visible = true;
        templateBadge.Position = Vector2.Zero;

        ResizeEnchantVfxViewport(vfxNode, cardNode, templateBadge, slotLayouts[animatedIndex]);
        SyncEnchantVfxSparkles(vfxNode, slotLayouts[0].Position, slotLayouts[animatedIndex].Position);

        for (int i = 0; i < animatedIndex; i++)
        {
            Control badge = DuplicateEnchantmentTab(primaryTab);
            badge.Name = $"{EnchantVfxStaticBadgePrefix}{i}";
            cardBadgeRoot.AddChildSafely(badge);
            ApplyEnchantmentSlotLayout(badge, slotLayouts[i], visible: true);
            ApplyEnchantmentVisualState(badge, visualStates[i]);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the effective scope (override-first, then registry) is a
    /// persisted scope that survives combat. Only persisted-scope enchantments should be
    /// mirrored to <see cref="CardModel.DeckVersion"/>; transient ones (<c>UntilCombatEnds</c>,
    /// <c>UntilTurnEnds</c>, <c>LingerForTurns</c>, <c>MaxActivations</c>) should not, because
    /// the deck version is the pre-combat baseline and must not accumulate combat-only state.
    /// </summary>
    private static bool IsScopeEffectivelyPermanent(Type enchantmentType, EnchantmentScope? overrideScope)
    {
        if (overrideScope != null)
        {
            return overrideScope is EnchantmentScope.PermanentScope
                or EnchantmentScope.ConditionalActiveScope
                or EnchantmentScope.RemoveWhenScope;
        }

        return EnchantmentRegistry.IsPermanentScope(enchantmentType);
    }

    private static void SyncDeckVersionEnchantment(
        CardModel card,
        Type enchantmentType,
        int amount,
        EnchantmentStackBehavior behavior,
        EnchantmentScope? scopeOverride)
    {
        CardModel? deckVersion = card.DeckVersion;
        if (deckVersion == null || ReferenceEquals(deckVersion, card) || amount == 0)
        {
            return;
        }

        SeedMissingApplicationOrder(deckVersion);

        EnchantmentModel? existing = GetEnchantment(deckVersion, enchantmentType);
        if (existing != null && behavior == EnchantmentStackBehavior.MergeAmount)
        {
            int previousTotalAmount = existing.Amount;
            existing.Amount += amount;
            MultiEnchantmentStackSupport.AppendMergedStackAmount(existing, previousTotalAmount, amount);
            MultiEnchantmentStackSupport.ApplyMergedAmountDelta(existing, amount);
            MultiEnchantmentStackSupport.RefreshMergedEnchantmentState(existing);
            // Mirror the scope override so the deck instance's effective permanence matches the
            // combat card's. Otherwise GetDeckSyncedInstances filters by the registry default and
            // can desync from the combat-card ordinal during removal.
            MultiEnchantmentScopeSupport.SetScopeOverrideOnApply(deckVersion, existing, scopeOverride);
            RememberLastAppliedEnchantment(deckVersion, existing);
            AppendApplicationOrder(deckVersion, existing.Id);
        }
        else
        {
            ModelId modelId = ModelDb.GetId(enchantmentType);
            EnchantmentModel? model = ModelDb.GetById<EnchantmentModel>(modelId);
            if (model == null)
            {
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment] Could not mirror {enchantmentType.FullName ?? enchantmentType.Name} to DeckVersion because ModelDb.GetById({modelId}) returned null.");
                return;
            }

            EnchantmentModel mirrored = model.ToMutable();
            AttachNewEnchantmentStacks(
                choiceContext: null,
                deckVersion,
                mirrored,
                amount,
                modifyCard: true,
                triggerChanged: false,
                out _,
                scopeOverride);
        }

        deckVersion.DynamicVars.RecalculateForUpgradeOrEnchant();
        deckVersion.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(deckVersion);
        TriggerEnchantmentChanged(deckVersion);
    }

    private static EnchantmentScope? GetScopeOverride(CardEnchantmentState? state, EnchantmentModel enchantment)
    {
        return state != null && state.ScopeStates.TryGetValue(enchantment, out ScopeRuntimeState? scopeState)
            ? scopeState.OverrideScope
            : null;
    }

    private static int GetDeckSyncedInstanceOrdinal(CardModel card, EnchantmentModel target)
    {
        Type enchantmentType = target.GetType();
        int ordinal = 0;
        CardEnchantmentState? state = CardStates.TryGetValue(card, out CardEnchantmentState? existingState)
            ? existingState
            : null;

        foreach (EnchantmentModel enchantment in GetGameplayEnchantments(card))
        {
            if (enchantment.GetType() != enchantmentType)
            {
                continue;
            }

            EnchantmentScope? scopeOverride = GetScopeOverride(state, enchantment);
            if (!IsScopeEffectivelyPermanent(enchantmentType, scopeOverride))
            {
                continue;
            }

            if (ReferenceEquals(enchantment, target))
            {
                return ordinal;
            }

            ordinal++;
        }

        return -1;
    }

    private static IReadOnlyList<EnchantmentModel> GetDeckSyncedInstances(CardModel card, Type enchantmentType)
    {
        CardEnchantmentState? state = CardStates.TryGetValue(card, out CardEnchantmentState? existingState)
            ? existingState
            : null;

        return GetGameplayEnchantments(card)
            .Where(enchantment => enchantment.GetType() == enchantmentType
                && IsScopeEffectivelyPermanent(enchantmentType, GetScopeOverride(state, enchantment)))
            .ToList();
    }

    /// <summary>
    /// Mirrors a removal from a combat card onto its <see cref="CardModel.DeckVersion"/> so the
    /// enchantment does not reappear in the next combat. For <see cref="EnchantmentStackBehavior.MergeAmount"/>
    /// stacks, decrements by the removed amount; for instance-based stacks, removes one concrete instance.
    /// </summary>
    private static void SyncDeckVersionEnchantmentRemoval(
        CardModel card,
        Type enchantmentType,
        EnchantmentStackBehavior behavior,
        int? instanceOrdinal,
        int removedAmount)
    {
        CardModel? deckVersion = card.DeckVersion;
        if (deckVersion == null || ReferenceEquals(deckVersion, card))
        {
            return;
        }

        EnchantmentModel? existing = GetEnchantment(deckVersion, enchantmentType);
        if (existing == null)
        {
            return;
        }

        if (behavior == EnchantmentStackBehavior.MergeAmount)
        {
            int amountToRemove = Math.Max(1, removedAmount);
            if (existing.Amount <= amountToRemove)
            {
                RemoveEnchantmentInternal(deckVersion, existing, RemovalReason.Manual,
                    bypassVeto: true, refreshCard: true, triggerChanged: false);
            }
            else
            {
                existing.Amount -= amountToRemove;
                MultiEnchantmentStackSupport.RemoveMergedStackAmount(existing, amountToRemove);
                MultiEnchantmentStackSupport.RefreshMergedEnchantmentState(existing);
                deckVersion.DynamicVars.RecalculateForUpgradeOrEnchant();
                deckVersion.FinalizeUpgradeInternal();
                MultiEnchantmentStackSupport.RefreshDerivedState(deckVersion);
            }
        }
        else if (instanceOrdinal is >= 0)
        {
            IReadOnlyList<EnchantmentModel> instances = GetDeckSyncedInstances(deckVersion, enchantmentType);
            if (instanceOrdinal.Value < instances.Count)
            {
                EnchantmentModel target = instances[instanceOrdinal.Value];
                RemoveEnchantmentInternal(deckVersion, target, RemovalReason.Manual,
                    bypassVeto: true, refreshCard: true, triggerChanged: false);
            }
        }
    }
}

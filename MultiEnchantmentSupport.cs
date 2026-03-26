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

namespace MultiEnchantmentMod;

internal static class MultiEnchantmentSupport
{
    private const string SavePropertyName = nameof(MultiEnchantmentSaveCarrier.MultiEnchantmentData);
    private const string OrderSavePropertyName = nameof(MultiEnchantmentSaveCarrier.MultiEnchantmentOrderData);
    private const float ExtraSlotYOffset = 44f;
    private const string EnchantVfxViewportBadgePrefix = "MultiEnchantVfxViewportBadge";
    private const string EnchantVfxStaticBadgePrefix = "MultiEnchantVfxStaticBadge";
    private const string EnchantVfxSparklesBasePositionMeta = "_multi_enchant_sparkles_base_position";
    private const string EnchantVfxOverrideRestorePositionMeta = "_multi_enchant_vfx_override_restore_position";
    private const string EnchantVfxOverrideRestoreSizeMeta = "_multi_enchant_vfx_override_restore_size";
    private const string EnchantVfxOverrideRestoreActiveMeta = "_multi_enchant_vfx_override_restore_active";

    private static readonly ConditionalWeakTable<CardModel, CardEnchantmentState> CardStates = new();
    private static readonly ConditionalWeakTable<NCard, CardUiState> CardUiStates = new();
    private static readonly ConditionalWeakTable<Node, EnchantmentVfxSnapshotState> PendingEnchantVfxSnapshots = new();

    private static readonly FieldInfo? CardEnchantmentChangedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.EnchantmentChanged));
    private static readonly FieldInfo? CardCurrentTargetField =
        AccessTools.Field(typeof(CardModel), "_currentTarget");
    private static readonly FieldInfo? CardTemporaryStarCostsField =
        AccessTools.Field(typeof(CardModel), "_temporaryStarCosts");
    private static readonly FieldInfo? CardPlayedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.Played));
    private static readonly FieldInfo? CardStarCostChangedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.StarCostChanged));
    private static readonly FieldInfo? NCardForceUnpoweredPreviewField =
        AccessTools.Field(typeof(NCard), "_forceUnpoweredPreview");
    private static readonly FieldInfo? NCardPreviewTargetField =
        AccessTools.Field(typeof(NCard), "_previewTarget");
    private static readonly FieldInfo? NCardDefaultEnchantmentPositionField =
        AccessTools.Field(typeof(NCard), "_defaultEnchantmentPosition");
    private static readonly FieldInfo? NCardEnchantmentTabField =
        AccessTools.Field(typeof(NCard), "_enchantmentTab");
    private static readonly PropertyInfo? EnchantedValueProperty =
        typeof(DynamicVar).GetProperty(
            nameof(DynamicVar.EnchantedValue),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? CardModelOnPlayMethod =
        AccessTools.Method(typeof(CardModel), "OnPlay");
    private static readonly MethodInfo? CardModelGetResultPileTypeMethod =
        AccessTools.Method(typeof(CardModel), "GetResultPileType");
    private static readonly MethodInfo? CardModelPlayPowerCardFlyVfxMethod =
        AccessTools.Method(typeof(CardModel), "PlayPowerCardFlyVfx");

    private static readonly StringName ShaderH = new("h");
    private static readonly StringName ShaderS = new("s");
    private static readonly StringName ShaderV = new("v");

    public static void Initialize()
    {
        SavedPropertiesTypeCache.InjectTypeIntoCache(typeof(MultiEnchantmentSaveCarrier));
    }

    public static IEnumerable<EnchantmentModel> GetEnchantments(CardModel? card)
    {
        if (card == null)
        {
            yield break;
        }

        if (card.Enchantment != null)
        {
            yield return card.Enchantment;
        }

        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card))
        {
            yield return enchantment;
        }
    }

    internal static IEnumerable<EnchantmentVisualState> GetOrderedVisualStates(CardModel? card)
    {
        foreach (OrderedVisualEntry entry in GetOrderedVisualEntries(card))
        {
            yield return entry.VisualState;
        }
    }

    public static IReadOnlyList<EnchantmentModel> GetAdditionalEnchantments(CardModel? card)
    {
        if (card == null)
        {
            return Array.Empty<EnchantmentModel>();
        }

        return CardStates.TryGetValue(card, out CardEnchantmentState? state)
            ? state.ExtraEnchantments
            : Array.Empty<EnchantmentModel>();
    }

    public static bool HasEnchantment<T>(CardModel? card) where T : EnchantmentModel
    {
        return GetEnchantments(card).Any(static enchantment => enchantment is T);
    }

    public static bool ShouldOfferCloneRestSiteOption(Player player)
    {
        return player.Deck.Cards.Any(HasEnchantment<Clone>);
    }

    public static EnchantmentModel? GetEnchantment(CardModel? card, Type enchantmentType)
    {
        return GetEnchantments(card).FirstOrDefault(enchantment => enchantment.GetType() == enchantmentType);
    }

    public static bool ShouldGlowGold(CardModel card)
    {
        return GetAdditionalEnchantments(card).Any(static enchantment => enchantment.ShouldGlowGold);
    }

    public static bool ShouldGlowRed(CardModel card)
    {
        return GetAdditionalEnchantments(card).Any(static enchantment => enchantment.ShouldGlowRed);
    }

    public static bool ShouldStartAtBottomOfDrawPile(CardModel card)
    {
        return GetEnchantments(card).Any(static enchantment => enchantment.ShouldStartAtBottomOfDrawPile);
    }

    public static bool HasAnyEnchantments(CardModel? card)
    {
        return card?.Enchantment != null || GetAdditionalEnchantments(card).Count > 0;
    }

    public static int GetReplayCount(CardModel card)
    {
        int replayCount = card.BaseReplayCount;
        foreach (OrderedEnchantmentEntry entry in GetOrderedEnchantmentEntries(card))
        {
            replayCount = EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantPlayCount(replayCount));
        }

        return replayCount;
    }

    public static void RecalculateAdditionalEnchantments(CardModel card)
    {
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card))
        {
            enchantment.RecalculateValues();
        }
    }

    public static decimal ApplyDamageEnchantments(CardModel? card, decimal damage, ValueProp props, ModifyDamageHookType hookType)
    {
        decimal result = damage;
        foreach (OrderedEnchantmentEntry entry in GetOrderedEnchantmentEntries(card))
        {
            if (hookType.HasFlag(ModifyDamageHookType.Additive))
            {
                result += EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantDamageAdditive(result, props));
            }

            if (hookType.HasFlag(ModifyDamageHookType.Multiplicative))
            {
                result *= EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantDamageMultiplicative(result, props));
            }
        }

        return result;
    }

    public static decimal ApplyBlockEnchantments(CardModel? card, decimal block, ValueProp props)
    {
        decimal result = block;
        foreach (OrderedEnchantmentEntry entry in GetOrderedEnchantmentEntries(card))
        {
            result += EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantBlockAdditive(result, props));
            result *= EvaluateWithEffectiveAmount(entry, enchantment => enchantment.EnchantBlockMultiplicative(result, props));
        }

        return result;
    }

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

    public static EnchantmentModel ApplyEnchantment(EnchantmentModel enchantment, CardModel card, decimal amount)
    {
        enchantment.AssertMutable();
        if (!enchantment.CanEnchant(card))
        {
            throw new InvalidOperationException($"Cannot enchant {card.Id} with {enchantment.Id}.");
        }

        SeedMissingApplicationOrder(card);

        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());
        int appliedAmount = (int)amount;
        EnchantmentModel? existing = GetEnchantment(card, enchantment.GetType());
        if (existing != null && behavior == EnchantmentStackBehavior.MergeAmount)
        {
            int addedAmount = appliedAmount;
            int previousTotalAmount = existing.Amount;
            existing.Amount += addedAmount;
            MultiEnchantmentStackSupport.AppendMergedStackAmount(existing, previousTotalAmount, addedAmount);
            MultiEnchantmentStackSupport.ApplyMergedAmountDelta(existing, addedAmount);
            MultiEnchantmentStackSupport.RefreshMergedEnchantmentState(existing);
            SyncDeckVersionEnchantment(card, existing.GetType(), addedAmount, behavior);
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
            card.FinalizeUpgradeInternal();
            RememberLastAppliedEnchantment(card, existing);
            AppendApplicationOrder(card, enchantment.Id);
            MultiEnchantmentStackSupport.RefreshDerivedState(card);
            TriggerEnchantmentChanged(card);
            RecordEnchantmentHistory(card, enchantment.Id);
            return existing;
        }

        EnchantmentModel applied = AttachNewEnchantmentStacks(
            card,
            enchantment,
            appliedAmount,
            modifyCard: true,
            triggerChanged: false);

        SyncDeckVersionEnchantment(card, applied.GetType(), appliedAmount, behavior);
        card.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(card);
        TriggerEnchantmentChanged(card);
        RecordEnchantmentHistory(card, enchantment.Id);
        return applied;
    }

    public static EnchantmentModel AddAdditionalEnchantment(CardModel card, EnchantmentModel enchantment, decimal amount, bool modifyCard, bool triggerChanged)
    {
        // Public "add extra enchantment" API means "apply new stacks now", not "restore a saved
        // instance state". Restores must go through RestoreAdditionalEnchantmentState().
        return AttachNewAdditionalEnchantmentStacks(
            card,
            enchantment,
            (int)amount,
            modifyCard,
            triggerChanged);
    }

    private static EnchantmentModel AttachNewEnchantmentStacks(
        CardModel card,
        EnchantmentModel enchantment,
        int stackCount,
        bool modifyCard,
        bool triggerChanged)
    {
        // New applications may need to fan out one requested stack count into multiple concrete
        // enchantment instances when the behavior is DuplicateInstance/ExistenceStack.
        enchantment.AssertMutable();
        card.AssertMutable();
        SeedMissingApplicationOrder(card);

        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());
        if (ShouldFanOutAppliedStacks(behavior) && stackCount > 1)
        {
            EnchantmentModel firstApplied = AttachEnchantmentState(
                card,
                enchantment,
                1,
                modifyCard,
                triggerChanged: false);
            AppendApplicationOrder(card, enchantment.Id);
            for (int i = 1; i < stackCount; i++)
            {
                EnchantmentModel extra = (EnchantmentModel)enchantment.ClonePreservingMutability();
                AttachEnchantmentState(card, extra, 1, modifyCard, triggerChanged: false);
                AppendApplicationOrder(card, extra.Id);
            }

            if (triggerChanged)
            {
                TriggerEnchantmentChanged(card);
            }

            return firstApplied;
        }

        EnchantmentModel applied = AttachEnchantmentState(card, enchantment, stackCount, modifyCard, triggerChanged);
        AppendApplicationOrder(card, applied.Id);
        return applied;
    }

    private static EnchantmentModel AttachNewAdditionalEnchantmentStacks(
        CardModel card,
        EnchantmentModel enchantment,
        int stackCount,
        bool modifyCard,
        bool triggerChanged)
    {
        enchantment.AssertMutable();
        card.AssertMutable();
        SeedMissingApplicationOrder(card);
        EnchantmentStackBehavior behavior = MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType());
        if (ShouldFanOutAppliedStacks(behavior) && stackCount > 1)
        {
            EnchantmentModel firstApplied = AttachAdditionalEnchantmentState(
                card,
                enchantment,
                1,
                modifyCard,
                triggerChanged: false);
            AppendApplicationOrder(card, enchantment.Id);
            for (int i = 1; i < stackCount; i++)
            {
                EnchantmentModel clone = (EnchantmentModel)enchantment.ClonePreservingMutability();
                AttachAdditionalEnchantmentState(
                    card,
                    clone,
                    1,
                    modifyCard,
                    triggerChanged: false);
                AppendApplicationOrder(card, clone.Id);
            }

            if (triggerChanged)
            {
                TriggerEnchantmentChanged(card);
            }

            return firstApplied;
        }

        EnchantmentModel applied = AttachAdditionalEnchantmentState(card, enchantment, stackCount, modifyCard, triggerChanged);
        AppendApplicationOrder(card, applied.Id);
        return applied;
    }

    private static EnchantmentModel AttachAdditionalEnchantmentState(
        CardModel card,
        EnchantmentModel enchantment,
        int amount,
        bool modifyCard,
        bool triggerChanged)
    {
        // Low-level exact-state attach. This method never interprets Amount as "how many more
        // stacks to create"; it attaches one concrete enchantment instance with the given state.
        enchantment.AssertMutable();
        card.AssertMutable();
        enchantment.ApplyInternal(card, amount);
        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        state.ExtraEnchantments.Add(enchantment);
        state.LastAppliedEnchantment = enchantment;

        if (modifyCard)
        {
            bool isFirstOfTypeOnCard = MultiEnchantmentStackSupport.GetEnchantmentCount(card, enchantment.GetType()) == 1;
            ApplyInitialEnchantmentState(enchantment, isFirstOfTypeOnCard);
        }

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
        bool triggerChanged)
    {
        // Mod source: cloning/loading an existing extra enchantment must preserve that instance's
        // live Amount. Duplicate-instance enchantments like Goopy use Amount as runtime state, not
        // "how many additional copies to fan out".
        return AttachAdditionalEnchantmentState(
            card,
            enchantment,
            enchantment.Amount,
            modifyCard,
            triggerChanged);
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

        enchantment.ClearInternal();
        if (ReferenceEquals(state.LastAppliedEnchantment, enchantment))
        {
            state.LastAppliedEnchantment = null;
        }

        return true;
    }

    public static void ClearAdditionalEnchantments(CardModel card, bool triggerChanged)
    {
        if (!CardStates.TryGetValue(card, out CardEnchantmentState? state))
        {
            return;
        }

        foreach (EnchantmentModel enchantment in state.ExtraEnchantments)
        {
            enchantment.ClearInternal();
        }

        state.ExtraEnchantments.Clear();
        CardStates.Remove(card);
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

    public static void SerializeAdditionalEnchantments(CardModel card, SerializableCard save)
    {
        // Base-game source: CardModel.ToSerializable only persists the primary enchantment.
        // Extra enchantments are stored in SavedProperties so old saves remain readable.
        List<SerializableEnchantment> extras = GetAdditionalEnchantments(card)
            .Select(static enchantment => enchantment.ToSerializable())
            .ToList();

        if (extras.Count == 0)
        {
            SerializeApplicationOrder(card, save);
            return;
        }

        save.Props ??= new SavedProperties();
        save.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();

        string payload = JsonSerializer.Serialize(extras);
        SavedProperties.SavedProperty<string> property = new(SavePropertyName, payload);
        int existingIndex = save.Props.strings.FindIndex(saved => saved.name == SavePropertyName);
        if (existingIndex >= 0)
        {
            save.Props.strings[existingIndex] = property;
        }
        else
        {
            save.Props.strings.Add(property);
        }

        SerializeApplicationOrder(card, save);
    }

    public static void DeserializeAdditionalEnchantments(SerializableCard save, CardModel card)
    {
        // Base-game source: CardModel.FromSerializable only restores the primary enchantment.
        // This path must stay tolerant of missing/renamed mod data so one bad extra enchantment
        // does not invalidate the whole card or run.
        if (!TryGetSavedString(save.Props, SavePropertyName, out string payload) || string.IsNullOrWhiteSpace(payload))
        {
            DeserializeApplicationOrder(save, card);
            return;
        }

        List<SerializableEnchantment>? extras;
        try
        {
            extras = JsonSerializer.Deserialize<List<SerializableEnchantment>>(payload);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error($"Failed to deserialize extra enchantments for card {card.Id}: {ex}");
            RemoveSavedString(save.Props, SavePropertyName);
            DeserializeApplicationOrder(save, card);
            return;
        }

        if (extras == null || extras.Count == 0)
        {
            DeserializeApplicationOrder(save, card);
            return;
        }

        bool changed = false;
        foreach (SerializableEnchantment serializable in extras)
        {
            try
            {
                EnchantmentModel enchantment = EnchantmentModel.FromSerializable(serializable);
                RestoreAdditionalEnchantmentState(card, enchantment, modifyCard: true, triggerChanged: false);
                changed = true;
            }
            catch (Exception ex)
            {
                MultiEnchantmentMod.Logger.Error(
                    $"Failed to restore extra enchantment {serializable.Id} on card {card.Id}: {ex}");
            }
        }

        if (changed)
        {
            DeserializeApplicationOrder(save, card);
            NormalizeCardEnchantmentStacks(card);
            TriggerEnchantmentChanged(card);
            card.FinalizeUpgradeInternal();
            MultiEnchantmentStackSupport.RefreshDerivedState(card);
        }
        else
        {
            DeserializeApplicationOrder(save, card);
        }
    }

    private static void SerializeApplicationOrder(CardModel card, SerializableCard save)
    {
        IReadOnlyList<ModelId> order = GetApplicationOrder(card);
        if (order.Count == 0)
        {
            RemoveSavedString(save.Props, OrderSavePropertyName);
            return;
        }

        save.Props ??= new SavedProperties();
        save.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
        string payload = JsonSerializer.Serialize(order);
        SavedProperties.SavedProperty<string> property = new(OrderSavePropertyName, payload);
        int existingIndex = save.Props.strings.FindIndex(saved => saved.name == OrderSavePropertyName);
        if (existingIndex >= 0)
        {
            save.Props.strings[existingIndex] = property;
        }
        else
        {
            save.Props.strings.Add(property);
        }
    }

    private static void DeserializeApplicationOrder(SerializableCard save, CardModel card)
    {
        if (!TryGetSavedString(save.Props, OrderSavePropertyName, out string payload) || string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        List<ModelId>? order;
        try
        {
            order = JsonSerializer.Deserialize<List<ModelId>>(payload);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error($"Failed to deserialize enchantment order for card {card.Id}: {ex}");
            RemoveSavedString(save.Props, OrderSavePropertyName);
            return;
        }

        if (order == null || order.Count == 0)
        {
            return;
        }

        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        state.ApplicationOrder.Clear();
        state.ApplicationOrder.AddRange(order);
    }

    public static void AppendAdditionalExtraCardText(CardModel card, ref string description)
    {
        HashSet<(Type EnchantmentType, string Text)> seenLines = new();
        if (TryGetFormattedExtraCardText(card.Enchantment, out string rawPrimaryText) &&
            TryGetFormattedExtraCardTextForDescription(card, card.Enchantment, out string primaryText))
        {
            if (rawPrimaryText != primaryText)
            {
                description = description.Replace(
                    "[purple]" + rawPrimaryText + "[/purple]",
                    "[purple]" + primaryText + "[/purple]");
            }

            seenLines.Add((card.Enchantment!.GetType(), primaryText));
        }

        List<string> lines = new();
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card))
        {
            if (!TryGetFormattedExtraCardTextForDescription(card, enchantment, out string text))
            {
                continue;
            }

            if (!seenLines.Add((enchantment.GetType(), text)))
            {
                continue;
            }

            lines.Add("[purple]" + text + "[/purple]");
        }

        if (lines.Count > 0)
        {
            description = string.Join('\n', new[] { description }.Concat(lines).Where(static line => !string.IsNullOrEmpty(line)));
        }
    }

    public static IEnumerable<IHoverTip> AppendAdditionalHoverTips(CardModel card, IEnumerable<IHoverTip> original)
    {
        return original.Concat(GetAdditionalEnchantments(card).SelectMany(static enchantment => enchantment.HoverTips)).Distinct();
    }

    public static void UpdateAdditionalEnchantmentPreviews(NCard cardNode, CardPreviewMode previewMode)
    {
        CardModel? model = cardNode.Model;
        if (model == null)
        {
            return;
        }

        bool forceUnpoweredPreview = NCardForceUnpoweredPreviewField?.GetValue(cardNode) is bool value && value;
        if (forceUnpoweredPreview)
        {
            return;
        }

        Creature? previewTarget = NCardPreviewTargetField?.GetValue(cardNode) as Creature;
        Creature? target = previewTarget ?? model.CurrentTarget;
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(model))
        {
            enchantment.DynamicVars.ClearPreview();
            model.UpdateDynamicVarPreview(previewMode, target, enchantment.DynamicVars);
        }
    }

    public static void SyncExtraEnchantmentTabs(NCard cardNode)
    {
        if (!GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        CardModel? model = cardNode.Model;
        if (model == null)
        {
            ClearCardUi(cardNode);
            return;
        }

        IReadOnlyList<EnchantmentModel> extras = GetAdditionalEnchantments(model);
        List<EnchantmentVisualState> visualStates = MultiEnchantmentStackSupport.ExpandVisualStates(model).ToList();
        CardUiState uiState = CardUiStates.GetOrCreateValue(cardNode);
        SubscribeExtraStatusHandlers(cardNode, uiState, extras);

        Control? primaryTab = NCardEnchantmentTabField?.GetValue(cardNode) as Control;
        Vector2 defaultPosition = NCardDefaultEnchantmentPositionField?.GetValue(cardNode) is Vector2 position
            ? position
            : Vector2.Zero;

        if (primaryTab == null || primaryTab.GetParent() == null)
        {
            ClearCardUi(cardNode);
            return;
        }

        if (visualStates.Count == 0)
        {
            ClearCardUi(cardNode);
            return;
        }

        // Base-game source: NCard.UpdateEnchantmentVisuals.
        // Reconstruct the primary tab layout exactly like vanilla, then reuse the resulting slot
        // geometry everywhere else so centered/queued cards and enchant VFX all agree on which row
        // each enchantment occupies.
        List<EnchantmentSlotLayout> slotLayouts = BuildEnchantmentSlotLayouts(
            cardNode,
            primaryTab,
            visualStates.Count,
            defaultPosition);

        ApplyEnchantmentVisualState(primaryTab, visualStates[0]);

        while (uiState.ExtraTabs.Count < visualStates.Count - 1)
        {
            Control tab = (Control)primaryTab.Duplicate();
            tab.Name = $"MultiEnchantmentTab{uiState.ExtraTabs.Count + 1}";
            if (tab.Material != null)
            {
                tab.Material = (Material)tab.Material.Duplicate();
            }

            primaryTab.GetParent().AddChildSafely(tab);
            uiState.ExtraTabs.Add(tab);
        }

        for (int i = 0; i < uiState.ExtraTabs.Count; i++)
        {
            Control tab = uiState.ExtraTabs[i];
            if (i >= visualStates.Count - 1)
            {
                tab.Visible = false;
                continue;
            }

            EnchantmentVisualState visualState = visualStates[i + 1];
            ApplyEnchantmentSlotLayout(tab, slotLayouts[i + 1], primaryTab.Visible);
            ApplyEnchantmentVisualState(tab, visualState);
        }
    }

    public static void ClearCardUi(NCard cardNode)
    {
        ClearTransientEnchantVfxUi(cardNode);

        if (!CardUiStates.TryGetValue(cardNode, out CardUiState? state))
        {
            return;
        }

        foreach ((EnchantmentModel enchantment, Action handler) in state.StatusHandlers.ToArray())
        {
            enchantment.StatusChanged -= handler;
        }

        foreach (Control tab in state.ExtraTabs)
        {
            if (GodotObject.IsInstanceValid(tab))
            {
                tab.QueueFreeSafely();
            }
        }

        state.ExtraTabs.Clear();
        state.StatusHandlers.Clear();
        CardUiStates.Remove(cardNode);
    }

    public static void HideExtraEnchantmentTabs(NCard? cardNode)
    {
        if (cardNode == null || !CardUiStates.TryGetValue(cardNode, out CardUiState? state))
        {
            return;
        }

        foreach (Control tab in state.ExtraTabs)
        {
            tab.Visible = false;
        }
    }

    public static void RefreshExtraEnchantmentTabs(NCard? cardNode)
    {
        if (cardNode == null || !GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        SyncExtraEnchantmentTabs(cardNode);
    }

    public static void CaptureEnchantVfxSnapshot(Node? vfxNode, CardModel? card)
    {
        if (vfxNode == null || card == null)
        {
            return;
        }

        EnchantmentVfxSnapshotState state = PendingEnchantVfxSnapshots.GetOrCreateValue(vfxNode);
        state.VisualStates = BuildEnchantVfxVisualStates(card);
    }

    public static IEnumerable<AbstractModel> AppendRunStateExtraEnchantments(RunState runState, IEnumerable<AbstractModel> original)
    {
        foreach (AbstractModel model in original)
        {
            yield return model;
        }

        foreach (Player player in runState.Players.Where(static player => player.IsActiveForHooks))
        {
            foreach (CardModel card in player.Deck.Cards.Where(static card => !card.HasBeenRemovedFromState))
            {
                foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card))
                {
                    yield return enchantment;
                }
            }
        }
    }

    public static IEnumerable<AbstractModel> AppendCombatStateExtraEnchantments(CombatState combatState, IEnumerable<AbstractModel> original)
    {
        foreach (AbstractModel model in original)
        {
            yield return model;
        }

        foreach (Player player in combatState.Players.Where(static player => player.IsActiveForHooks && player.PlayerCombatState != null))
        {
            foreach (CardModel card in player.PlayerCombatState!.AllCards.Where(static card => !card.HasBeenRemovedFromState))
            {
                foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card))
                {
                    yield return enchantment;
                }
            }
        }
    }

    public static async Task RunAdditionalEnchantmentsOnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // Base-game source: CardModel.OnPlayWrapper.
        // Extra enchantments must execute in the same phase as the primary enchantment's OnPlay so
        // cards/relics/powers observing AfterCardPlayed see the post-OnPlay state consistently.
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(cardPlay.Card).ToList())
        {
            choiceContext.PushModel(enchantment);
            await enchantment.OnPlay(choiceContext, cardPlay);
            enchantment.InvokeExecutionFinished();
            choiceContext.PopModel(enchantment);
        }
    }

    public static async Task OnPlayWrapperWithMultiEnchantments(
        CardModel card,
        PlayerChoiceContext choiceContext,
        Creature? target,
        bool isAutoPlay,
        ResourceInfo resources,
        bool skipCardPileVisuals)
    {
        // Base-game source: CardModel.OnPlayWrapper in STS2 v0.99.1.
        // This copy stays intentionally close to vanilla. The only functional change is inserting
        // extra-enchantment OnPlay execution immediately after the primary enchantment OnPlay.
        CombatState combatState = card.CombatState;
        choiceContext.PushModel(card);
        await CombatManager.Instance.WaitForUnpause();
        SetCurrentTargetForMultiEnchantmentPatch(card, target);
        if (!isAutoPlay)
        {
            await CardPileCmd.AddDuringManualCardPlay(card);
        }
        else
        {
            await CardPileCmd.Add(card, PileType.Play, CardPilePosition.Bottom, null, skipCardPileVisuals);
            if (!skipCardPileVisuals)
            {
                await Cmd.CustomScaledWait(0.25f, 0.35f);
            }
        }

        (PileType resultPileType, CardPilePosition resultPilePosition) =
            Hook.ModifyCardPlayResultPileTypeAndPosition(
                combatState,
                card,
                isAutoPlay,
                resources,
                GetResultPileTypeForMultiEnchantmentPatch(card),
                CardPilePosition.Bottom,
                out IEnumerable<AbstractModel> modifiers);

        foreach (AbstractModel item in modifiers)
        {
            await item.AfterModifyingCardPlayResultPileOrPosition(card, resultPileType, resultPilePosition);
        }

        int playCount = card.GetEnchantedReplayCount() + 1;
        playCount = Hook.ModifyCardPlayCount(combatState, card, playCount, target, out List<AbstractModel> modifyingModels);
        await Hook.AfterModifyingCardPlayCount(combatState, card, modifyingModels);

        ulong playStartTime = Time.GetTicksMsec();
        for (int i = 0; i < playCount; i++)
        {
            if (card.Type == CardType.Power)
            {
                await PlayPowerCardFlyVfxForMultiEnchantmentPatch(card);
            }
            else if (i > 0)
            {
                NCard? nCard = NCard.FindOnTable(card);
                if (nCard != null)
                {
                    await nCard.AnimMultiCardPlay();
                }
            }

            CardPlay cardPlay = new()
            {
                Card = card,
                Target = target,
                ResultPile = resultPileType,
                Resources = resources,
                IsAutoPlay = isAutoPlay,
                PlayIndex = i,
                PlayCount = playCount,
            };

            await Hook.BeforeCardPlayed(combatState, cardPlay);
            CombatManager.Instance.History.CardPlayStarted(combatState, cardPlay);
            await OnPlayForMultiEnchantmentPatch(card, choiceContext, cardPlay);
            card.InvokeExecutionFinished();
            if (card.Enchantment != null)
            {
                await card.Enchantment.OnPlay(choiceContext, cardPlay);
                card.Enchantment.InvokeExecutionFinished();
            }

            await RunAdditionalEnchantmentsOnPlay(choiceContext, cardPlay);

            if (card.Affliction != null)
            {
                AfflictionModel affliction = card.Affliction;
                await affliction.OnPlay(choiceContext, target);
                affliction.InvokeExecutionFinished();
            }

            CombatManager.Instance.History.CardPlayFinished(combatState, cardPlay);
            if (CombatManager.Instance.IsInProgress)
            {
                await Hook.AfterCardPlayed(combatState, choiceContext, cardPlay);
            }
        }

        if (!skipCardPileVisuals)
        {
            float elapsed = (float)(Time.GetTicksMsec() - playStartTime) / 1000f;
            await Cmd.CustomScaledWait(0.15f - elapsed, 0.3f - elapsed);
        }

        CardPile? pile = card.Pile;
        if (pile != null && pile.Type == PileType.Play)
        {
            switch (resultPileType)
            {
                case PileType.None:
                    await CardPileCmd.RemoveFromCombat(card, skipCardPileVisuals);
                    break;
                case PileType.Exhaust:
                    await CardCmd.Exhaust(choiceContext, card, causedByEthereal: false, skipCardPileVisuals);
                    break;
                default:
                    await CardPileCmd.Add(card, resultPileType, resultPilePosition, null, skipCardPileVisuals);
                    break;
            }
        }

        await CombatManager.Instance.CheckForEmptyHand(choiceContext, card.Owner);
        if (card.EnergyCost.AfterCardPlayedCleanup())
        {
            card.InvokeEnergyCostChanged();
        }

        if (ClearTemporaryStarCostsOnPlay(card))
        {
            InvokeStarCostChangedForMultiEnchantmentPatch(card);
        }

        SetCurrentTargetForMultiEnchantmentPatch(card, null);
        InvokePlayedForMultiEnchantmentPatch(card);
        choiceContext.PopModel(card);
    }

    public static void SetEnchantedValue(DynamicVar dynamicVar, decimal value)
    {
        EnchantedValueProperty?.SetValue(dynamicVar, value);
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
            visualStates.Count,
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
            Control badge = (Control)primaryTab.Duplicate();
            badge.Name = $"{EnchantVfxStaticBadgePrefix}{i}";
            badge.UniqueNameInOwner = false;
            if (badge.Material != null)
            {
                badge.Material = (Material)badge.Material.Duplicate();
            }

            cardBadgeRoot.AddChildSafely(badge);
            ApplyEnchantmentSlotLayout(badge, slotLayouts[i], visible: true);
            ApplyEnchantmentVisualState(badge, visualStates[i]);
        }
    }

    public static EnchantmentModel? GetMostRecentlyAppliedEnchantment(CardModel? card)
    {
        if (card == null)
        {
            return null;
        }

        if (CardStates.TryGetValue(card, out CardEnchantmentState? state) &&
            state.LastAppliedEnchantment?.Card == card)
        {
            return state.LastAppliedEnchantment;
        }

        IReadOnlyList<EnchantmentModel> extras = GetAdditionalEnchantments(card);
        if (extras.Count > 0)
        {
            return extras[^1];
        }

        return card.Enchantment;
    }

    private static IReadOnlyList<ModelId> GetApplicationOrder(CardModel? card)
    {
        if (card == null || !CardStates.TryGetValue(card, out CardEnchantmentState? state))
        {
            return Array.Empty<ModelId>();
        }

        return state.ApplicationOrder;
    }

    private static void AppendApplicationOrder(CardModel card, ModelId enchantmentId)
    {
        CardStates.GetOrCreateValue(card).ApplicationOrder.Add(enchantmentId);
    }

    private static void RebuildApplicationOrder(CardModel card)
    {
        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        state.ApplicationOrder.Clear();
        state.ApplicationOrder.AddRange(
            GetEnchantments(card)
                .SelectMany(static enchantment =>
                    Enumerable.Repeat(enchantment.Id, MultiEnchantmentStackSupport.GetVisualStackCount(enchantment))));
    }

    private static void CopyApplicationOrder(CardModel source, CardModel target)
    {
        if (!CardStates.TryGetValue(source, out CardEnchantmentState? sourceState) ||
            sourceState.ApplicationOrder.Count == 0)
        {
            return;
        }

        CardEnchantmentState targetState = CardStates.GetOrCreateValue(target);
        targetState.ApplicationOrder.Clear();
        targetState.ApplicationOrder.AddRange(sourceState.ApplicationOrder);
    }

    private static void SeedMissingApplicationOrder(CardModel card)
    {
        if (!HasAnyEnchantments(card))
        {
            return;
        }

        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        if (state.ApplicationOrder.Count > 0)
        {
            return;
        }

        state.ApplicationOrder.AddRange(
            GetDefaultOrderedEnchantmentEntries(card).Select(static entry => entry.Enchantment.Id));
    }

    private static List<OrderedEnchantmentEntry> GetOrderedEnchantmentEntries(CardModel? card)
    {
        return OrderEntries(
            card,
            GetDefaultOrderedEnchantmentEntries(card),
            static entry => entry.Enchantment.Id);
    }

    private static List<OrderedEnchantmentEntry> GetDefaultOrderedEnchantmentEntries(CardModel? card)
    {
        List<OrderedEnchantmentEntry> entries = new();
        HashSet<Type> handledMergedTypes = new();
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            if (MultiEnchantmentStackSupport.GetBehavior(enchantment.GetType()) == EnchantmentStackBehavior.MergeAmount)
            {
                if (!handledMergedTypes.Add(enchantment.GetType()) ||
                    !MultiEnchantmentStackSupport.TryGetMergedStackAmounts(enchantment, out int[] stackAmounts))
                {
                    continue;
                }

                entries.AddRange(stackAmounts.Select(stackAmount => new OrderedEnchantmentEntry(enchantment, stackAmount)));
                continue;
            }

            entries.Add(new OrderedEnchantmentEntry(enchantment, enchantment.Amount));
        }

        return entries;
    }

    private static List<OrderedVisualEntry> GetDefaultOrderedVisualEntries(CardModel? card)
    {
        List<OrderedVisualEntry> entries = new();
        HashSet<Type> handledTypes = new();
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            if (!handledTypes.Add(enchantment.GetType()))
            {
                continue;
            }

            EnchantmentStackSnapshot snapshot = MultiEnchantmentStackSupport.GetSnapshot(enchantment);
            foreach (EnchantmentStackSlice slice in snapshot.VisualSlices)
            {
                entries.Add(new OrderedVisualEntry(
                    enchantment.Id,
                    new EnchantmentVisualState(
                        enchantment.Icon,
                        GetDisplayAmount(enchantment, slice.Amount),
                        enchantment.ShowAmount,
                        slice.Status)));
            }
        }

        return entries;
    }

    private static List<OrderedVisualEntry> GetOrderedVisualEntries(CardModel? card)
    {
        return OrderEntries(
            card,
            GetDefaultOrderedVisualEntries(card),
            static entry => entry.EnchantmentId);
    }

    private static List<TEntry> OrderEntries<TEntry>(
        CardModel? card,
        List<TEntry> defaultEntries,
        Func<TEntry, ModelId> idSelector)
    {
        if (card == null)
        {
            return defaultEntries;
        }

        IReadOnlyList<ModelId> order = GetApplicationOrder(card);
        if (order.Count == 0 || order.Count != defaultEntries.Count)
        {
            return defaultEntries;
        }

        Dictionary<ModelId, Queue<TEntry>> entriesById = new();
        foreach (TEntry entry in defaultEntries)
        {
            ModelId enchantmentId = idSelector(entry);
            if (!entriesById.TryGetValue(enchantmentId, out Queue<TEntry>? queue))
            {
                queue = new Queue<TEntry>();
                entriesById[enchantmentId] = queue;
            }

            queue.Enqueue(entry);
        }

        List<TEntry> orderedEntries = new(order.Count);
        foreach (ModelId enchantmentId in order)
        {
            if (!entriesById.TryGetValue(enchantmentId, out Queue<TEntry>? queue) ||
                queue.Count == 0)
            {
                return defaultEntries;
            }

            orderedEntries.Add(queue.Dequeue());
        }

        return entriesById.Values.Any(static queue => queue.Count > 0)
            ? defaultEntries
            : orderedEntries;
    }

    private static int GetDisplayAmount(OrderedEnchantmentEntry entry)
    {
        return EvaluateWithEffectiveAmount(entry, enchantment => enchantment.DisplayAmount);
    }

    private static int GetDisplayAmount(EnchantmentModel enchantment, int effectiveAmount)
    {
        return EvaluateWithEffectiveAmount(
            new OrderedEnchantmentEntry(enchantment, effectiveAmount),
            static value => value.DisplayAmount);
    }

    private static T EvaluateWithEffectiveAmount<T>(OrderedEnchantmentEntry entry, Func<EnchantmentModel, T> evaluator)
    {
        EnchantmentModel enchantment = entry.Enchantment;
        if (entry.EffectiveAmount == enchantment.Amount)
        {
            return evaluator(enchantment);
        }

        int originalAmount = enchantment.Amount;
        enchantment.Amount = entry.EffectiveAmount;
        try
        {
            return evaluator(enchantment);
        }
        finally
        {
            enchantment.Amount = originalAmount;
        }
    }

    public static bool HaveSameEnchantments(CardModel? left, CardModel? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        List<OrderedEnchantmentEntry> leftEnchantments = GetOrderedEnchantmentEntries(left);
        List<OrderedEnchantmentEntry> rightEnchantments = GetOrderedEnchantmentEntries(right);
        if (leftEnchantments.Count != rightEnchantments.Count)
        {
            return false;
        }

        for (int i = 0; i < leftEnchantments.Count; i++)
        {
            OrderedEnchantmentEntry leftEnchantment = leftEnchantments[i];
            OrderedEnchantmentEntry rightEnchantment = rightEnchantments[i];
            if (!HaveSameEnchantmentState(leftEnchantment, rightEnchantment))
            {
                return false;
            }
        }

        return true;
    }

    public static int GetEnchantmentsHashCode(CardModel? card)
    {
        HashCode hash = new();
        foreach (OrderedEnchantmentEntry enchantment in GetOrderedEnchantmentEntries(card))
        {
            AddEnchantmentStateToHash(ref hash, enchantment);
        }

        return hash.ToHashCode();
    }

    private static bool HaveSameEnchantmentState(OrderedEnchantmentEntry left, OrderedEnchantmentEntry right)
    {
        // Multiplayer card grouping must compare gameplay-relevant state, not just model ID.
        // Status affects behavior immediately, and Props can carry per-enchantment saved state.
        if (!left.Enchantment.Id.Equals(right.Enchantment.Id) ||
            left.EffectiveAmount != right.EffectiveAmount ||
            left.Enchantment.Status != right.Enchantment.Status)
        {
            return false;
        }

        string leftProps = left.Enchantment.Props == null ? string.Empty : JsonSerializer.Serialize(left.Enchantment.Props);
        string rightProps = right.Enchantment.Props == null ? string.Empty : JsonSerializer.Serialize(right.Enchantment.Props);
        return leftProps == rightProps;
    }

    private static void AddEnchantmentStateToHash(ref HashCode hash, OrderedEnchantmentEntry enchantment)
    {
        hash.Add(enchantment.Enchantment.Id);
        hash.Add(enchantment.EffectiveAmount);
        hash.Add(enchantment.Enchantment.Status);
        hash.Add(enchantment.Enchantment.Props == null ? string.Empty : JsonSerializer.Serialize(enchantment.Enchantment.Props));
    }

    private static bool TryGetSavedString(SavedProperties? properties, string propertyName, out string value)
    {
        value = string.Empty;
        if (properties?.strings == null)
        {
            return false;
        }

        foreach (SavedProperties.SavedProperty<string> property in properties.strings)
        {
            if (property.name == propertyName)
            {
                value = property.value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetFormattedExtraCardText(EnchantmentModel? enchantment, out string text)
    {
        text = string.Empty;
        string? formatted = enchantment?.DynamicExtraCardText?.GetFormattedText();
        if (string.IsNullOrEmpty(formatted))
        {
            return false;
        }

        text = formatted;
        return true;
    }

    private static bool TryGetFormattedExtraCardTextForDescription(CardModel card, EnchantmentModel? enchantment, out string text)
    {
        if (!TryGetFormattedExtraCardText(enchantment, out text))
        {
            return false;
        }

        if (enchantment != null &&
            MultiEnchantmentStackSupport.TryFormatExtraCardText(enchantment, text, out string formattedText))
        {
            text = formattedText;
            return true;
        }

        if (enchantment is Goopy)
        {
            int goopyCount = GetEnchantments(card)
                .OfType<Goopy>()
                .Count(static goopy => goopy.DynamicExtraCardText != null);
            if (goopyCount > 1)
            {
                text = text.Replace("[blue]1[/blue]", $"[blue]{goopyCount}[/blue]", StringComparison.Ordinal);
            }
        }

        return true;
    }

    private static void RemoveSavedString(SavedProperties? properties, string propertyName)
    {
        properties?.strings?.RemoveAll(property => property.name == propertyName);
    }

    private static void RecordEnchantmentHistory(CardModel card, ModelId enchantmentId)
    {
        if (card.Pile == null)
        {
            return;
        }

        card.Owner.RunState.CurrentMapPointHistoryEntry?
            .GetEntry(card.Owner.NetId)
            .CardsEnchanted
            .Add(new CardEnchantmentHistoryEntry(card, enchantmentId));
    }

    private static void TriggerEnchantmentChanged(CardModel card)
    {
        if (CardEnchantmentChangedField?.GetValue(card) is Action action)
        {
            action();
        }
    }

    private static void RememberLastAppliedEnchantment(CardModel card, EnchantmentModel enchantment)
    {
        CardStates.GetOrCreateValue(card).LastAppliedEnchantment = enchantment;
    }

    public static Task OnPlayForMultiEnchantmentPatch(CardModel card, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (CardModelOnPlayMethod?.Invoke(card, new object[] { choiceContext, cardPlay }) is Task task)
        {
            return task;
        }

        throw new InvalidOperationException("Failed to invoke CardModel.OnPlay.");
    }

    public static PileType GetResultPileTypeForMultiEnchantmentPatch(CardModel card)
    {
        if (CardModelGetResultPileTypeMethod?.Invoke(card, null) is PileType pileType)
        {
            return pileType;
        }

        throw new InvalidOperationException("Failed to invoke CardModel.GetResultPileType.");
    }

    public static Task PlayPowerCardFlyVfxForMultiEnchantmentPatch(CardModel card)
    {
        if (CardModelPlayPowerCardFlyVfxMethod?.Invoke(card, null) is Task task)
        {
            return task;
        }

        throw new InvalidOperationException("Failed to invoke CardModel.PlayPowerCardFlyVfx.");
    }

    public static void InvokeStarCostChangedForMultiEnchantmentPatch(CardModel card)
    {
        if (CardStarCostChangedField?.GetValue(card) is Action action)
        {
            action();
        }
    }

    public static void InvokePlayedForMultiEnchantmentPatch(CardModel card)
    {
        if (CardPlayedField?.GetValue(card) is Action action)
        {
            action();
        }
    }

    public static void SetCurrentTargetForMultiEnchantmentPatch(CardModel card, Creature? target)
    {
        if (CardCurrentTargetField == null)
        {
            throw new InvalidOperationException("Failed to access CardModel._currentTarget.");
        }

        card.AssertMutable();
        CardCurrentTargetField.SetValue(card, target);
    }

    private static bool ClearTemporaryStarCostsOnPlay(CardModel card)
    {
        if (CardTemporaryStarCostsField?.GetValue(card) is not System.Collections.IList temporaryStarCosts)
        {
            return false;
        }

        List<object> toRemove = new();
        foreach (object item in temporaryStarCosts)
        {
            PropertyInfo? clearsWhenPlayedProperty = item.GetType().GetProperty("ClearsWhenCardIsPlayed");
            if (clearsWhenPlayedProperty?.GetValue(item) is bool clears && clears)
            {
                toRemove.Add(item);
            }
        }

        foreach (object item in toRemove)
        {
            temporaryStarCosts.Remove(item);
        }

        return toRemove.Count > 0;
    }

    private static void SyncDeckVersionEnchantment(
        CardModel card,
        Type enchantmentType,
        int amount,
        EnchantmentStackBehavior behavior)
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
            RememberLastAppliedEnchantment(deckVersion, existing);
            AppendApplicationOrder(deckVersion, existing.Id);
        }
        else
        {
            EnchantmentModel mirrored =
                ModelDb.GetById<EnchantmentModel>(ModelDb.GetId(enchantmentType)).ToMutable();
            AttachNewEnchantmentStacks(deckVersion, mirrored, amount, modifyCard: true, triggerChanged: false);
        }

        deckVersion.DynamicVars.RecalculateForUpgradeOrEnchant();
        deckVersion.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(deckVersion);
        TriggerEnchantmentChanged(deckVersion);
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
        bool triggerChanged)
    {
        enchantment.AssertMutable();
        card.AssertMutable();
        if (card.Enchantment == null)
        {
            // Match the base-game "primary enchantment" path first so downstream code that expects
            // CardModel.Enchantment to be populated continues to behave like vanilla.
            card.EnchantInternal(enchantment, amount);
            if (modifyCard)
            {
                ApplyInitialEnchantmentState(enchantment, isFirstOfTypeOnCard: true);
            }

            RememberLastAppliedEnchantment(card, enchantment);
            return enchantment;
        }

        return AttachAdditionalEnchantmentState(card, enchantment, amount, modifyCard, triggerChanged);
    }

    private static bool ShouldFanOutAppliedStacks(EnchantmentStackBehavior behavior)
    {
        return behavior is EnchantmentStackBehavior.DuplicateInstance or EnchantmentStackBehavior.ExistenceStack;
    }

    public static Task HandleGoopyAfterCardPlayed(Goopy goopy, PlayerChoiceContext context, CardPlay cardPlay)
    {
        if (cardPlay.Card != goopy.Card)
        {
            return Task.CompletedTask;
        }

        goopy.Amount++;
        RememberLastAppliedEnchantment(goopy.Card, goopy);
        goopy.Card.DynamicVars.RecalculateForUpgradeOrEnchant();
        goopy.Card.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(goopy.Card);
        TriggerEnchantmentChanged(goopy.Card);

        CardModel? deckVersion = goopy.Card.DeckVersion;
        if (deckVersion == null || ReferenceEquals(deckVersion, goopy.Card))
        {
            return Task.CompletedTask;
        }

        // Mod source: once Goopy is allowed to stack, its Amount becomes per-instance persistent
        // growth state, not "how many Goopies exist on the card". Mirror the matching instance on
        // DeckVersion instead of adding a new merged stack.
        List<Goopy> combatGoopies = GetEnchantments(goopy.Card).OfType<Goopy>().ToList();
        int goopyIndex = combatGoopies.IndexOf(goopy);
        if (goopyIndex < 0)
        {
            return Task.CompletedTask;
        }

        List<Goopy> deckGoopies = GetEnchantments(deckVersion).OfType<Goopy>().ToList();
        Goopy mirroredGoopy;
        if (goopyIndex < deckGoopies.Count)
        {
            mirroredGoopy = deckGoopies[goopyIndex];
        }
        else
        {
            mirroredGoopy = (Goopy)ModelDb.GetById<EnchantmentModel>(goopy.Id).ToMutable();
            AttachEnchantmentState(deckVersion, mirroredGoopy, 1, modifyCard: true, triggerChanged: false);
        }

        mirroredGoopy.Amount = goopy.Amount;
        RememberLastAppliedEnchantment(deckVersion, mirroredGoopy);
        deckVersion.DynamicVars.RecalculateForUpgradeOrEnchant();
        deckVersion.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(deckVersion);
        TriggerEnchantmentChanged(deckVersion);

        return Task.CompletedTask;
    }

    private static List<EnchantmentVisualState> ConsumeEnchantVfxSnapshot(Node vfxNode, CardModel card)
    {
        if (PendingEnchantVfxSnapshots.TryGetValue(vfxNode, out EnchantmentVfxSnapshotState? state) &&
            state.VisualStates.Count > 0)
        {
            List<EnchantmentVisualState> snapshot = state.VisualStates;
            PendingEnchantVfxSnapshots.Remove(vfxNode);
            return snapshot;
        }

        return BuildEnchantVfxVisualStates(card);
    }

    private static List<EnchantmentVisualState> BuildEnchantVfxVisualStates(CardModel card)
    {
        return GetOrderedVisualStates(card).ToList();
    }

    private static void ApplyEnchantmentVisualState(Control tab, EnchantmentVisualState visualState)
    {
        TextureRect? icon = tab.GetNodeOrNull<TextureRect>("Icon");
        MegaLabel? label = tab.GetNodeOrNull<MegaLabel>("Label");
        if (icon != null)
        {
            icon.Texture = visualState.Icon;
        }

        if (label != null)
        {
            label.SetTextAutoSize(visualState.DisplayAmount.ToString());
            label.Visible = visualState.ShowAmount;
        }

        ApplyStatusToTab(tab, icon, label, visualState.Status);
    }

    private static List<EnchantmentSlotLayout> BuildEnchantmentSlotLayouts(
        NCard cardNode,
        Control primaryTab,
        int slotCount,
        Vector2 defaultPosition)
    {
        List<EnchantmentSlotLayout> layouts = new(slotCount);
        if (slotCount <= 0)
        {
            return layouts;
        }

        CardModel? model = cardNode.Model;
        Vector2 expectedPrimaryPosition = model != null && (model.HasStarCostX || model.CurrentStarCost >= 0)
            ? defaultPosition
            : defaultPosition + Vector2.Up * 45f;
        Vector2 primaryPosition = primaryTab.Position == Vector2.Zero && expectedPrimaryPosition != Vector2.Zero
            ? expectedPrimaryPosition
            : primaryTab.Position;
        float rowOffset = GetExtraEnchantmentRowOffset(primaryTab);

        for (int i = 0; i < slotCount; i++)
        {
            layouts.Add(new EnchantmentSlotLayout(
                primaryPosition + Vector2.Down * (i * rowOffset),
                primaryTab.Scale,
                primaryTab.Rotation,
                primaryTab.PivotOffset,
                primaryTab.ZIndex,
                primaryTab.TopLevel));
        }

        return layouts;
    }

    private static void ApplyEnchantmentSlotLayout(Control tab, EnchantmentSlotLayout layout, bool visible)
    {
        tab.Visible = visible;
        tab.Position = layout.Position;
        tab.Scale = layout.Scale;
        tab.Rotation = layout.Rotation;
        tab.PivotOffset = layout.PivotOffset;
        tab.ZIndex = layout.ZIndex;
        tab.TopLevel = layout.TopLevel;
    }

    private static void SubscribeExtraStatusHandlers(NCard cardNode, CardUiState uiState, IReadOnlyList<EnchantmentModel> extras)
    {
        foreach ((EnchantmentModel enchantment, Action handler) in uiState.StatusHandlers.ToArray())
        {
            if (extras.Contains(enchantment))
            {
                continue;
            }

            enchantment.StatusChanged -= handler;
            uiState.StatusHandlers.Remove(enchantment);
        }

        foreach (EnchantmentModel enchantment in extras)
        {
            if (uiState.StatusHandlers.ContainsKey(enchantment))
            {
                continue;
            }

            void Handler()
            {
                if (GodotObject.IsInstanceValid(cardNode))
                {
                    SyncExtraEnchantmentTabs(cardNode);
                }
            }

            enchantment.StatusChanged += Handler;
            uiState.StatusHandlers[enchantment] = Handler;
        }
    }

    private static void ApplyStatusToTab(Control tab, TextureRect? icon, MegaLabel? label, EnchantmentStatus status)
    {
        if (status == EnchantmentStatus.Disabled)
        {
            tab.Modulate = new Color(1f, 1f, 1f, 0.9f);
            if (tab.Material is ShaderMaterial shader)
            {
                shader.SetShaderParameter(ShaderH, 0.25);
                shader.SetShaderParameter(ShaderS, 0.1);
                shader.SetShaderParameter(ShaderV, 0.6);
            }

            if (icon != null)
            {
                icon.UseParentMaterial = true;
            }

            if (label != null)
            {
                label.SelfModulate = StsColors.gray;
            }
        }
        else
        {
            tab.Modulate = Colors.White;
            if (tab.Material is ShaderMaterial shader)
            {
                shader.SetShaderParameter(ShaderH, 0.25);
                shader.SetShaderParameter(ShaderS, 0.4);
                shader.SetShaderParameter(ShaderV, 0.6);
            }

            if (icon != null)
            {
                icon.UseParentMaterial = false;
            }

            if (label != null)
            {
                label.SelfModulate = Colors.White;
            }
        }
    }

    private static void ClearNamedChildren(Node parent, string prefix)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child.Name.ToString().StartsWith(prefix, StringComparison.Ordinal))
            {
                child.QueueFreeSafely();
            }
        }
    }

    private static void ClearTransientEnchantVfxUi(NCard cardNode)
    {
        if (!GodotObject.IsInstanceValid(cardNode) || !cardNode.IsNodeReady())
        {
            return;
        }

        Control? enchantmentTab = NCardEnchantmentTabField?.GetValue(cardNode) as Control;
        TextureRect? vfxOverride = cardNode.GetNodeOrNull<TextureRect>("%EnchantmentVfxOverride");

        Node? badgeRoot = enchantmentTab?.GetParent();
        if (badgeRoot != null)
        {
            ClearNamedChildren(badgeRoot, EnchantVfxStaticBadgePrefix);
        }

        if (vfxOverride != null)
        {
            RestoreEnchantVfxOverrideDefaults(vfxOverride);
        }
    }

    private static void CaptureEnchantVfxOverrideRestoreState(TextureRect vfxOverride)
    {
        if (vfxOverride.HasMeta(EnchantVfxOverrideRestoreActiveMeta))
        {
            return;
        }

        vfxOverride.SetMeta(EnchantVfxOverrideRestorePositionMeta, vfxOverride.Position);
        vfxOverride.SetMeta(EnchantVfxOverrideRestoreSizeMeta, vfxOverride.Size);
        vfxOverride.SetMeta(EnchantVfxOverrideRestoreActiveMeta, true);
    }

    private static void RestoreEnchantVfxOverrideDefaults(TextureRect vfxOverride)
    {
        if (!vfxOverride.HasMeta(EnchantVfxOverrideRestoreActiveMeta))
        {
            return;
        }

        if (vfxOverride.HasMeta(EnchantVfxOverrideRestorePositionMeta))
        {
            vfxOverride.Position = vfxOverride.GetMeta(EnchantVfxOverrideRestorePositionMeta).AsVector2();
        }

        if (vfxOverride.HasMeta(EnchantVfxOverrideRestoreSizeMeta))
        {
            vfxOverride.Size = vfxOverride.GetMeta(EnchantVfxOverrideRestoreSizeMeta).AsVector2();
        }

        vfxOverride.RemoveMeta(EnchantVfxOverrideRestorePositionMeta);
        vfxOverride.RemoveMeta(EnchantVfxOverrideRestoreSizeMeta);
        vfxOverride.RemoveMeta(EnchantVfxOverrideRestoreActiveMeta);
    }

    private static void SyncEnchantVfxSparkles(Node vfxNode, Vector2 baseSlotPosition, Vector2 animatedSlotPosition)
    {
        GpuParticles2D? sparkles = vfxNode.GetNodeOrNull<GpuParticles2D>("%EnchantmentAppearSparkles");
        if (sparkles == null)
        {
            return;
        }

        Vector2 basePosition = sparkles.HasMeta(EnchantVfxSparklesBasePositionMeta)
            ? sparkles.GetMeta(EnchantVfxSparklesBasePositionMeta).AsVector2()
            : sparkles.Position;
        if (!sparkles.HasMeta(EnchantVfxSparklesBasePositionMeta))
        {
            sparkles.SetMeta(EnchantVfxSparklesBasePositionMeta, basePosition);
        }

        sparkles.Position = basePosition + (animatedSlotPosition - baseSlotPosition);
    }

    private static float GetExtraEnchantmentRowOffset(Control primaryTab)
    {
        return Math.Max(ExtraSlotYOffset, primaryTab.Size.Y * primaryTab.Scale.Y);
    }

    private static void ResizeEnchantVfxViewport(
        Node vfxNode,
        NCard cardNode,
        TextureRect templateBadge,
        EnchantmentSlotLayout slotLayout)
    {
        SubViewport? viewport = vfxNode.GetNodeOrNull<SubViewport>("%EnchantmentViewport");
        if (viewport == null)
        {
            return;
        }

        int targetWidth = Mathf.CeilToInt(templateBadge.Size.X * templateBadge.Scale.X);
        int targetHeight = Mathf.CeilToInt(templateBadge.Size.Y * templateBadge.Scale.Y);

        viewport.Size = new Vector2I(targetWidth, targetHeight);
        TextureRect vfxOverride = cardNode.EnchantmentVfxOverride;
        CaptureEnchantVfxOverrideRestoreState(vfxOverride);
        // Base-game source: NCard.OnReturnedFromPool does not restore EnchantmentVfxOverride's
        // rect, and NCard is pooled. Always assign an absolute position from the current card tab
        // instead of accumulating offsets on reused card nodes.
        vfxOverride.Position = slotLayout.Position;
        vfxOverride.Size = new Vector2(targetWidth, targetHeight);
    }

    private sealed class CardEnchantmentState
    {
        public List<EnchantmentModel> ExtraEnchantments { get; } = new();
        public List<ModelId> ApplicationOrder { get; } = new();
        public EnchantmentModel? LastAppliedEnchantment { get; set; }
    }

    private sealed class CardUiState
    {
        public List<Control> ExtraTabs { get; } = new();
        public Dictionary<EnchantmentModel, Action> StatusHandlers { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class EnchantmentVfxSnapshotState
    {
        public List<EnchantmentVisualState> VisualStates { get; set; } = new();
    }

    internal sealed record EnchantmentVisualState(
        Texture2D Icon,
        int DisplayAmount,
        bool ShowAmount,
        EnchantmentStatus Status);

    private readonly record struct EnchantmentSlotLayout(
        Vector2 Position,
        Vector2 Scale,
        float Rotation,
        Vector2 PivotOffset,
        int ZIndex,
        bool TopLevel);

    private readonly record struct OrderedEnchantmentEntry(
        EnchantmentModel Enchantment,
        int EffectiveAmount);

    private readonly record struct OrderedVisualEntry(
        ModelId EnchantmentId,
        EnchantmentVisualState VisualState);

    private sealed class MultiEnchantmentSaveCarrier
    {
        [SavedProperty]
        public string MultiEnchantmentData { get; set; } = string.Empty;

        [SavedProperty]
        public string MultiEnchantmentOrderData { get; set; } = string.Empty;

        [SavedProperty]
        public int[] MultiEnchantmentMergedStackAmounts { get; set; } = Array.Empty<int>();
    }
}

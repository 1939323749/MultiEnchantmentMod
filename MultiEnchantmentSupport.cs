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
    private const float ExtraSlotYOffset = 44f;

    private static readonly ConditionalWeakTable<CardModel, CardEnchantmentState> CardStates = new();
    private static readonly ConditionalWeakTable<NCard, CardUiState> CardUiStates = new();
    private static readonly ConditionalWeakTable<Node, EnchantmentVfxSnapshotState> PendingEnchantVfxSnapshots = new();

    private static readonly FieldInfo? CardEnchantmentChangedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.EnchantmentChanged));
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
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            replayCount = enchantment.EnchantPlayCount(replayCount);
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
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            if (hookType.HasFlag(ModifyDamageHookType.Additive))
            {
                result += enchantment.EnchantDamageAdditive(result, props);
            }

            if (hookType.HasFlag(ModifyDamageHookType.Multiplicative))
            {
                result *= enchantment.EnchantDamageMultiplicative(result, props);
            }
        }

        return result;
    }

    public static decimal ApplyBlockEnchantments(CardModel? card, decimal block, ValueProp props)
    {
        decimal result = block;
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            result += enchantment.EnchantBlockAdditive(result, props);
            result *= enchantment.EnchantBlockMultiplicative(result, props);
        }

        return result;
    }

    public static EnchantmentModel ApplyEnchantment(EnchantmentModel enchantment, CardModel card, decimal amount)
    {
        enchantment.AssertMutable();
        if (!enchantment.CanEnchant(card))
        {
            throw new InvalidOperationException($"Cannot enchant {card.Id} with {enchantment.Id}.");
        }

        EnchantmentModel? existing = GetEnchantment(card, enchantment.GetType());
        if (existing != null)
        {
            existing.Amount += (int)amount;
            existing.RecalculateValues();
            SyncDeckVersionEnchantment(card, existing.GetType(), (int)amount);
            card.DynamicVars.RecalculateForUpgradeOrEnchant();
            card.FinalizeUpgradeInternal();
            RememberLastAppliedEnchantment(card, existing);
            TriggerEnchantmentChanged(card);
            RecordEnchantmentHistory(card, enchantment.Id);
            return existing;
        }

        EnchantmentModel applied;
        if (card.Enchantment == null)
        {
            // Match the base-game "primary enchantment" path first so downstream code that expects
            // CardModel.Enchantment to be populated continues to behave like vanilla.
            card.EnchantInternal(enchantment, amount);
            enchantment.ModifyCard();
            applied = enchantment;
            RememberLastAppliedEnchantment(card, applied);
        }
        else
        {
            applied = AddAdditionalEnchantment(card, enchantment, amount, modifyCard: true, triggerChanged: true);
        }

        SyncDeckVersionEnchantment(card, applied.GetType(), (int)amount);
        card.FinalizeUpgradeInternal();
        RecordEnchantmentHistory(card, enchantment.Id);
        return applied;
    }

    public static EnchantmentModel AddAdditionalEnchantment(CardModel card, EnchantmentModel enchantment, decimal amount, bool modifyCard, bool triggerChanged)
    {
        enchantment.AssertMutable();
        card.AssertMutable();
        enchantment.ApplyInternal(card, amount);
        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        state.ExtraEnchantments.Add(enchantment);
        state.LastAppliedEnchantment = enchantment;

        if (modifyCard)
        {
            enchantment.ModifyCard();
        }

        if (triggerChanged)
        {
            TriggerEnchantmentChanged(card);
        }

        return enchantment;
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
            AddAdditionalEnchantment(clone, cloned, cloned.Amount, modifyCard: true, triggerChanged: false);
            changed = true;
        }

        if (changed)
        {
            clone.FinalizeUpgradeInternal();
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
                target.EnchantInternal(cloned, cloned.Amount);
                cloned.ModifyCard();
                RememberLastAppliedEnchantment(target, cloned);
            }
            else
            {
                AddAdditionalEnchantment(target, cloned, cloned.Amount, modifyCard: true, triggerChanged: false);
            }

            changed = true;
        }

        if (changed)
        {
            target.FinalizeUpgradeInternal();
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
    }

    public static void DeserializeAdditionalEnchantments(SerializableCard save, CardModel card)
    {
        // Base-game source: CardModel.FromSerializable only restores the primary enchantment.
        // This path must stay tolerant of missing/renamed mod data so one bad extra enchantment
        // does not invalidate the whole card or run.
        if (!TryGetSavedString(save.Props, SavePropertyName, out string payload) || string.IsNullOrWhiteSpace(payload))
        {
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
            return;
        }

        if (extras == null || extras.Count == 0)
        {
            return;
        }

        bool changed = false;
        foreach (SerializableEnchantment serializable in extras)
        {
            try
            {
                EnchantmentModel enchantment = EnchantmentModel.FromSerializable(serializable);
                AddAdditionalEnchantment(card, enchantment, serializable.Amount, modifyCard: true, triggerChanged: false);
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
            TriggerEnchantmentChanged(card);
            card.FinalizeUpgradeInternal();
        }
    }

    public static void AppendAdditionalExtraCardText(CardModel card, ref string description)
    {
        List<string> lines = GetAdditionalEnchantments(card)
            .Select(static enchantment => enchantment.DynamicExtraCardText?.GetFormattedText())
            .Where(static text => !string.IsNullOrEmpty(text))
            .Select(static text => "[purple]" + text + "[/purple]")
            .ToList();

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
        CardModel? model = cardNode.Model;
        if (model == null)
        {
            ClearCardUi(cardNode);
            return;
        }

        IReadOnlyList<EnchantmentModel> extras = GetAdditionalEnchantments(model);
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

        // Base-game source: NCard.UpdateEnchantmentVisuals.
        // Reconstruct the primary tab layout exactly like vanilla, then anchor extra tabs to the
        // live primary tab rect so centered/targeting cards keep the full stack visible even when
        // other gameplay code moves or reuses the same card node without re-running our sync.
        Vector2 expectedPrimaryPosition = (model.HasStarCostX || model.CurrentStarCost >= 0)
            ? defaultPosition
            : defaultPosition + Vector2.Up * 45f;
        Vector2 primaryPosition = primaryTab.Position == Vector2.Zero && expectedPrimaryPosition != Vector2.Zero
            ? expectedPrimaryPosition
            : primaryTab.Position;
        float rowOffset = GetExtraEnchantmentRowOffset(primaryTab);

        while (uiState.ExtraTabs.Count < extras.Count)
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
            if (i >= extras.Count)
            {
                tab.Visible = false;
                continue;
            }

            EnchantmentModel enchantment = extras[i];
            TextureRect? icon = tab.GetNodeOrNull<TextureRect>("Icon");
            MegaLabel? label = tab.GetNodeOrNull<MegaLabel>("Label");

            tab.Visible = primaryTab.Visible;
            tab.Position = primaryPosition + Vector2.Down * ((i + 1) * rowOffset);
            tab.Scale = primaryTab.Scale;
            tab.Rotation = primaryTab.Rotation;
            tab.PivotOffset = primaryTab.PivotOffset;
            tab.Modulate = primaryTab.Modulate;
            tab.ZIndex = primaryTab.ZIndex;
            tab.TopLevel = primaryTab.TopLevel;

            if (icon != null)
            {
                icon.Texture = enchantment.Icon;
            }

            if (label != null)
            {
                label.SetTextAutoSize(enchantment.DisplayAmount.ToString());
                label.Visible = enchantment.ShowAmount;
            }

            ApplyStatusToTab(tab, icon, label, enchantment.Status);
        }
    }

    public static void ClearCardUi(NCard cardNode)
    {
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

    public static void CaptureEnchantVfxSnapshot(Node? vfxNode, CardModel? card)
    {
        if (vfxNode == null || card == null)
        {
            return;
        }

        EnchantmentVfxSnapshotState state = PendingEnchantVfxSnapshots.GetOrCreateValue(vfxNode);
        state.Badges = GetEnchantments(card)
            .Select(static enchantment => new EnchantmentVfxBadgeState(
                enchantment.Icon,
                enchantment.DisplayAmount,
                enchantment.ShowAmount,
                enchantment.Status))
            .ToList();
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

    public static async Task AfterCardPlayedWithAdditionalEnchantments(CombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // Base-game source: Hook.AfterCardPlayed.
        // Vanilla already runs card.Enchantment.OnPlay during CardModel.OnPlay; we only add the
        // extra enchantments here, then preserve the original AfterCardPlayed / Late listener order.
        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(cardPlay.Card).ToList())
        {
            choiceContext.PushModel(enchantment);
            await enchantment.OnPlay(choiceContext, cardPlay);
            enchantment.InvokeExecutionFinished();
            choiceContext.PopModel(enchantment);
        }

        List<AbstractModel> listeners = combatState.IterateHookListeners().ToList();

        foreach (AbstractModel model in listeners)
        {
            choiceContext.PushModel(model);
            await model.AfterCardPlayed(choiceContext, cardPlay);
            model.InvokeExecutionFinished();
            choiceContext.PopModel(model);
        }

        foreach (AbstractModel model in listeners)
        {
            choiceContext.PushModel(model);
            await model.AfterCardPlayedLate(choiceContext, cardPlay);
            model.InvokeExecutionFinished();
            choiceContext.PopModel(model);
        }
    }

    public static void SetEnchantedValue(DynamicVar dynamicVar, decimal value)
    {
        EnchantedValueProperty?.SetValue(dynamicVar, value);
    }

    public static void SyncEnchantVfxBadges(Node vfxNode, CardModel? card, TextureRect? templateIcon)
    {
        if (card == null ||
            templateIcon?.GetParent() is not TextureRect templateBadge ||
            templateBadge.GetParent() is not Node badgeRoot)
        {
            return;
        }

        foreach (Node child in badgeRoot.GetChildren())
        {
            if (child.Name.ToString().StartsWith("MultiEnchantVfx", StringComparison.Ordinal))
            {
                child.QueueFreeSafely();
            }
        }

        List<EnchantmentVfxBadgeState> badges = ConsumeEnchantVfxSnapshot(vfxNode, card);
        if (badges.Count == 0)
        {
            return;
        }

        // Base-game source: NCardEnchantVfx._Ready plus scenes/vfx/vfx_card_enchant.tscn.
        // The banner texture lives on EnchantmentInViewport, with Icon/Label as children. Duplicate
        // the full badge node so extra entries keep the banner art instead of rendering a bare icon.
        Vector2 badgeBasePosition = Vector2.Zero;
        float rowOffset = GetEnchantVfxRowOffset(templateBadge);
        ResizeEnchantVfxViewport(vfxNode, templateBadge, badges.Count, rowOffset);

        ApplyEnchantVfxBadge(templateBadge, badges[0], badgeBasePosition, 0, rowOffset);

        for (int i = 1; i < badges.Count; i++)
        {
            EnchantmentVfxBadgeState badgeState = badges[i];
            TextureRect badge = (TextureRect)templateBadge.Duplicate();
            badge.Name = $"MultiEnchantVfxBadge{i}";
            badge.UniqueNameInOwner = false;
            if (badge.Material != null)
            {
                badge.Material = (Material)badge.Material.Duplicate();
            }

            badgeRoot.AddChildSafely(badge);
            ApplyEnchantVfxBadge(badge, badgeState, badgeBasePosition, i, rowOffset);
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

        List<EnchantmentModel> leftEnchantments = GetEnchantments(left).ToList();
        List<EnchantmentModel> rightEnchantments = GetEnchantments(right).ToList();
        if (leftEnchantments.Count != rightEnchantments.Count)
        {
            return false;
        }

        for (int i = 0; i < leftEnchantments.Count; i++)
        {
            EnchantmentModel leftEnchantment = leftEnchantments[i];
            EnchantmentModel rightEnchantment = rightEnchantments[i];
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
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            AddEnchantmentStateToHash(ref hash, enchantment);
        }

        return hash.ToHashCode();
    }

    private static bool HaveSameEnchantmentState(EnchantmentModel left, EnchantmentModel right)
    {
        // Multiplayer card grouping must compare gameplay-relevant state, not just model ID.
        // Status affects behavior immediately, and Props can carry per-enchantment saved state.
        if (!left.Id.Equals(right.Id) ||
            left.Amount != right.Amount ||
            left.Status != right.Status)
        {
            return false;
        }

        string leftProps = left.Props == null ? string.Empty : JsonSerializer.Serialize(left.Props);
        string rightProps = right.Props == null ? string.Empty : JsonSerializer.Serialize(right.Props);
        return leftProps == rightProps;
    }

    private static void AddEnchantmentStateToHash(ref HashCode hash, EnchantmentModel enchantment)
    {
        hash.Add(enchantment.Id);
        hash.Add(enchantment.Amount);
        hash.Add(enchantment.Status);
        hash.Add(enchantment.Props == null ? string.Empty : JsonSerializer.Serialize(enchantment.Props));
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

    private static void SyncDeckVersionEnchantment(CardModel card, Type enchantmentType, int amount)
    {
        CardModel? deckVersion = card.DeckVersion;
        if (deckVersion == null || ReferenceEquals(deckVersion, card) || amount == 0)
        {
            return;
        }

        EnchantmentModel? existing = GetEnchantment(deckVersion, enchantmentType);
        if (existing != null)
        {
            existing.Amount += amount;
            existing.RecalculateValues();
            RememberLastAppliedEnchantment(deckVersion, existing);
        }
        else
        {
            EnchantmentModel mirrored =
                ModelDb.GetById<EnchantmentModel>(ModelDb.GetId(enchantmentType)).ToMutable();
            if (deckVersion.Enchantment == null)
            {
                deckVersion.EnchantInternal(mirrored, amount);
                mirrored.ModifyCard();
                RememberLastAppliedEnchantment(deckVersion, mirrored);
            }
            else
            {
                AddAdditionalEnchantment(deckVersion, mirrored, amount, modifyCard: true, triggerChanged: false);
            }
        }

        deckVersion.DynamicVars.RecalculateForUpgradeOrEnchant();
        deckVersion.FinalizeUpgradeInternal();
        TriggerEnchantmentChanged(deckVersion);
    }

    public static Task HandleGoopyAfterCardPlayed(Goopy goopy, PlayerChoiceContext context, CardPlay cardPlay)
    {
        if (cardPlay.Card != goopy.Card)
        {
            return Task.CompletedTask;
        }

        goopy.Amount++;
        // Base-game source: Goopy.AfterCardPlayed.
        // Vanilla increments DeckVersion.Enchantment directly, which breaks once Goopy can live in
        // either the primary slot or any extra slot. Reuse the mod's mirror-sync path so the deck
        // copy is updated or created consistently, especially for combat-time enchanting tests.
        SyncDeckVersionEnchantment(goopy.Card, typeof(Goopy), 1);
        TriggerEnchantmentChanged(goopy.Card);

        return Task.CompletedTask;
    }

    private static List<EnchantmentVfxBadgeState> ConsumeEnchantVfxSnapshot(Node vfxNode, CardModel card)
    {
        if (PendingEnchantVfxSnapshots.TryGetValue(vfxNode, out EnchantmentVfxSnapshotState? state) &&
            state.Badges.Count > 0)
        {
            List<EnchantmentVfxBadgeState> snapshot = state.Badges;
            PendingEnchantVfxSnapshots.Remove(vfxNode);
            return snapshot;
        }

        return GetEnchantments(card)
            .Select(static enchantment => new EnchantmentVfxBadgeState(
                enchantment.Icon,
                enchantment.DisplayAmount,
                enchantment.ShowAmount,
                enchantment.Status))
            .ToList();
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

    private static void ApplyEnchantVfxBadge(
        TextureRect badge,
        EnchantmentVfxBadgeState badgeState,
        Vector2 badgeBasePosition,
        int index,
        float rowOffset)
    {
        TextureRect? icon = badge.GetNodeOrNull<TextureRect>("Icon");
        MegaLabel? label = badge.GetNodeOrNull<MegaLabel>("Label");

        badge.Position = badgeBasePosition + Vector2.Down * (index * rowOffset);

        if (icon == null)
        {
            return;
        }

        icon.Texture = badgeState.Icon;
        if (label != null)
        {
            label.SetTextAutoSize(badgeState.DisplayAmount.ToString());
            label.Visible = badgeState.ShowAmount;
        }

        ApplyStatusToTab(badge, icon, label, badgeState.Status);
    }

    private static float GetEnchantVfxRowOffset(TextureRect badge)
    {
        return Math.Max(ExtraSlotYOffset, badge.Size.Y * badge.Scale.Y);
    }

    private static float GetExtraEnchantmentRowOffset(Control primaryTab)
    {
        return Math.Max(ExtraSlotYOffset, primaryTab.Size.Y * primaryTab.Scale.Y);
    }

    private static void ResizeEnchantVfxViewport(
        Node vfxNode,
        TextureRect templateBadge,
        int badgeCount,
        float rowOffset)
    {
        SubViewport? viewport = vfxNode.GetNodeOrNull<SubViewport>("%EnchantmentViewport");
        NCard? cardNode = vfxNode.GetChildren().OfType<NCard>().FirstOrDefault();
        if (viewport == null || cardNode == null)
        {
            return;
        }

        int targetWidth = Mathf.CeilToInt(templateBadge.Size.X * templateBadge.Scale.X);
        int targetHeight = Mathf.CeilToInt(
            (templateBadge.Size.Y * templateBadge.Scale.Y) +
            ((badgeCount - 1) * rowOffset));

        viewport.Size = new Vector2I(targetWidth, targetHeight);
        TextureRect vfxOverride = cardNode.EnchantmentVfxOverride;
        // Base-game source: NCard.OnReturnedFromPool does not restore EnchantmentVfxOverride's
        // rect, and NCard is pooled. Always assign an absolute position from the current card tab
        // instead of accumulating offsets on reused card nodes.
        vfxOverride.Position = cardNode.EnchantmentTab.Position;
        vfxOverride.Size = new Vector2(targetWidth, targetHeight);
    }

    private sealed class CardEnchantmentState
    {
        public List<EnchantmentModel> ExtraEnchantments { get; } = new();
        public EnchantmentModel? LastAppliedEnchantment { get; set; }
    }

    private sealed class CardUiState
    {
        public List<Control> ExtraTabs { get; } = new();
        public Dictionary<EnchantmentModel, Action> StatusHandlers { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class EnchantmentVfxSnapshotState
    {
        public List<EnchantmentVfxBadgeState> Badges { get; set; } = new();
    }

    private sealed record EnchantmentVfxBadgeState(
        Texture2D Icon,
        int DisplayAmount,
        bool ShowAmount,
        EnchantmentStatus Status);

    private sealed class MultiEnchantmentSaveCarrier
    {
        [SavedProperty]
        public string MultiEnchantmentData { get; set; } = string.Empty;
    }
}

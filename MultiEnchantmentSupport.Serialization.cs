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
    public static void SerializeAdditionalEnchantments(CardModel card, SerializableCard save)
    {
        // Base-game source: CardModel.ToSerializable only persists the primary enchantment.
        // Extra enchantments are stored in SavedProperties so old saves remain readable.
        List<SerializableEnchantment> extras = GetAdditionalEnchantments(card)
            .Select(static enchantment => enchantment.ToSerializable())
            .ToList();

        if (extras.Count == 0)
        {
            RemoveSavedString(save.Props, SavePropertyName);
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
        List<string> lines = new();

        bool hasRawPrimaryText = TryGetFormattedExtraCardText(card.Enchantment, out string rawPrimaryText);
        bool hasPrimaryText = TryGetFormattedExtraCardTextForDescription(card, card.Enchantment, out string primaryText);
        if (hasPrimaryText && card.Enchantment != null)
        {
            if (hasRawPrimaryText && rawPrimaryText != primaryText)
            {
                description = description.Replace(
                    "[purple]" + rawPrimaryText + "[/purple]",
                    "[purple]" + primaryText + "[/purple]");
            }
            else if (!hasRawPrimaryText)
            {
                lines.Add("[purple]" + primaryText + "[/purple]");
            }

            seenLines.Add((card.Enchantment.GetType(), primaryText));
        }

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
        // Phase 1.5 T1.5.1: inactive enchantments (gated via .WhenActive(false) / scope predicates)
        // should appear absent in the UI. Skip their hover tips so authors get the "doesn't exist"
        // semantic they expect — consistent with the IsActive gating already applied to damage /
        // block / dynamic-var pipelines.
        return original.Concat(GetAdditionalEnchantments(card)
            .Where(enchantment => MultiEnchantmentScopeSupport.IsActive(card, enchantment))
            .SelectMany(static enchantment => enchantment.HoverTips)).Distinct();
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
        bool hasBaseText = TryGetFormattedExtraCardText(enchantment, out string baseText);
        text = hasBaseText ? baseText : string.Empty;

        if (enchantment != null &&
            MultiEnchantmentStackSupport.TryFormatExtraCardText(enchantment, text, out string formattedText) &&
            !string.IsNullOrEmpty(formattedText))
        {
            text = formattedText;
            return true;
        }

        if (!hasBaseText)
        {
            text = string.Empty;
            return false;
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

    private static void RecordEnchantmentHistory(CardModel card, EnchantmentModel enchantment)
    {
        if (card.Pile == null)
        {
            return;
        }

        Type enchantmentType = enchantment.GetType();
        HistoryDisplayMode mode = EnchantmentRegistry.GetHistoryDisplayMode(enchantmentType);
        if (mode == HistoryDisplayMode.Hidden)
        {
            return;
        }

        if (mode == HistoryDisplayMode.Auto && !EnchantmentRegistry.IsPermanentScope(enchantmentType))
        {
            return;
        }

        card.Owner.RunState.CurrentMapPointHistoryEntry?
            .GetEntry(card.Owner.NetId)
            .CardsEnchanted
            .Add(new CardEnchantmentHistoryEntry(card, enchantment.Id));
    }
}

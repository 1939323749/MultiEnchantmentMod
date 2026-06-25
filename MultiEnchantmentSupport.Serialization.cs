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
using static MultiEnchantmentMod.SafeLog;

namespace MultiEnchantmentMod;

internal static partial class MultiEnchantmentSupport
{
    private static readonly JsonSerializerOptions ExtraEnchantmentJsonOptions = new()
    {
        IncludeFields = true,
    };
    private static readonly object ExtraCardTextWarningLock = new();
    private static readonly HashSet<Type> ExtraCardTextFormatWarningTypes = new();

    // Per-card snapshot of the extra enchantments (and their application order) the save intends,
    // captured at FromSerializable time. The post-load re-assert (RunManager.Launch postfix) reads
    // it back to repair any extra that a third-party CardModel.EnchantInternal replay dropped AFTER
    // our postfix ran. Keyed by the live CardModel that FromSerializable built — the same instance
    // that is placed into the run deck, so the Launch pass finds it again.
    private sealed class DesiredExtrasSnapshot
    {
        public List<SerializableEnchantment> Extras { get; set; } = new();
        public List<ModelId>? Order { get; set; }
    }

    private static readonly ConditionalWeakTable<CardModel, DesiredExtrasSnapshot> DesiredExtrasSnapshots = new();

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

        string payload = JsonSerializer.Serialize(extras, ExtraEnchantmentJsonOptions);
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
            extras = JsonSerializer.Deserialize<List<SerializableEnchantment>>(payload, ExtraEnchantmentJsonOptions);
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Error($"Failed to deserialize extra enchantments for card {GetSafeCardId(card)}: {ex}");
            Telemetry.TelemetryCollector.NoteDeserializationFailure(
                $"{SavePropertyName}:payload", GetSafeCardId(card), ex);
            RemoveSavedString(save.Props, SavePropertyName);
            DeserializeApplicationOrder(save, card);
            return;
        }

        if (extras == null || extras.Count == 0)
        {
            DeserializeApplicationOrder(save, card);
            return;
        }

        // Snapshot the desired extras + order keyed by this exact CardModel so the post-load
        // re-assert (RunManager.Launch postfix) can re-derive them and repair any extra dropped by
        // a third-party CardModel.EnchantInternal replay that runs AFTER this postfix.
        DesiredExtrasSnapshots.AddOrUpdate(card, new DesiredExtrasSnapshot
        {
            Extras = extras,
            Order = TryReadApplicationOrder(save),
        });

        // Diff-based, add-missing-only restore (idempotent): on a normal load nothing is present
        // yet so every extra is attached; if some are already present (e.g. a re-entrant restore)
        // only the per-type shortfall is attached and nothing is duplicated.
        bool changed = ReconcileAdditionalEnchantments(card, extras);

        DeserializeApplicationOrder(save, card);
        if (changed)
        {
            NormalizeCardEnchantmentStacks(card);
            TriggerEnchantmentChanged(card);
            card.FinalizeUpgradeInternal();
            MultiEnchantmentStackSupport.RefreshDerivedState(card);
        }
    }

    /// <summary>
    /// Idempotently attaches the extra enchantments in <paramref name="desired"/> that are missing
    /// from the card's EXTRA store. <paramref name="desired"/> is the extras-only blob
    /// (SerializeAdditionalEnchantments persists GetAdditionalEnchantments, never the primary
    /// card.Enchantment), so the comparison is against the extra store only. Counting the primary
    /// slot here would make a type that also sits in the primary look already-satisfied and silently
    /// drop its legitimate extra on every load. For each enchantment id it attaches only the missing
    /// copies (desired count minus extras already present), so:
    /// <list type="bullet">
    /// <item>a re-entrant restore or an already-repaired card is a no-op;</item>
    /// <item>multi-instance types (DuplicateInstance / ExistenceStack) keep their full extra count;</item>
    /// <item>nothing is ever duplicated, removed, or written to <c>card.Enchantment</c> — the primary
    /// slot is vanilla's responsibility, so this pass cannot repair a clobbered primary.</item>
    /// </list>
    /// Returns true if at least one instance was attached. Used by both the FromSerializable restore
    /// and the post-load re-assert. Each materialize/attach is guarded so one bad entry cannot abort
    /// the rest of the load.
    /// </summary>
    private static bool ReconcileAdditionalEnchantments(CardModel card, IReadOnlyList<SerializableEnchantment> desired)
    {
        if (card == null || desired == null || desired.Count == 0)
        {
            return false;
        }

        // Count the EXTRA instances already present, keyed by enchantment id. Extras-only, never the
        // primary slot (see the summary): the desired blob lists extras, so the shortfall must be
        // measured against extras alone.
        Dictionary<string, int> presentExtras = new(StringComparer.Ordinal);
        foreach (EnchantmentModel extra in GetAdditionalEnchantments(card))
        {
            string id = GetSafeModelId(extra.Id);
            presentExtras.TryGetValue(id, out int count);
            presentExtras[id] = count + 1;
        }

        // Group desired entries by enchantment id (entries sharing an id are the same enchantment; a
        // type recurs only for DuplicateInstance / ExistenceStack). Null entries are skipped
        // defensively. Insertion order is preserved so trailing-entry top-up stays deterministic.
        Dictionary<string, List<SerializableEnchantment>> desiredById = new(StringComparer.Ordinal);
        foreach (SerializableEnchantment serializable in desired)
        {
            if (serializable is not { } entry)
            {
                continue;
            }

            string id = GetSafeModelId(entry.Id);
            if (!desiredById.TryGetValue(id, out List<SerializableEnchantment>? grouped))
            {
                grouped = new List<SerializableEnchantment>();
                desiredById[id] = grouped;
            }

            grouped.Add(entry);
        }

        bool added = false;
        foreach ((string id, List<SerializableEnchantment> entries) in desiredById)
        {
            presentExtras.TryGetValue(id, out int present);
            int shortfall = entries.Count - present;

            // Attach only the missing copies, taking the trailing entries so a partially-present
            // multi-instance type tops up rather than re-adding the copies it already has. (For a
            // full drop present == 0 and the originals are restored in order, preserving each
            // instance's Amount; a non-trailing partial drop of a DuplicateInstance type restores
            // the count but not necessarily the exact per-instance Amount — acceptable, rare.)
            for (int i = 0; i < shortfall; i++)
            {
                int entryIndex = Math.Min(entries.Count - 1, present + i);
                SerializableEnchantment serializable = entries[entryIndex];
                try
                {
                    EnchantmentModel enchantment = EnchantmentModel.FromSerializable(serializable);
                    RestoreAdditionalEnchantmentState(
                        card,
                        enchantment,
                        modifyCard: true,
                        triggerChanged: false,
                        dispatchAppliedLifecycle: false);
                    added = true;
                }
                catch (Exception ex)
                {
                    MultiEnchantmentMod.Logger.Error(
                        $"Failed to restore extra enchantment {GetSafeModelId(serializable.Id)} on card {GetSafeCardId(card)}: {ex}");
                    Telemetry.TelemetryCollector.NoteDeserializationFailure(
                        GetSafeModelId(serializable.Id), GetSafeCardId(card), ex);
                }
            }
        }

        return added;
    }

    /// <summary>
    /// Post-load re-assert entry point (called per deck card from the RunManager.Launch postfix).
    /// Re-derives the card's intended extras from the snapshot captured during FromSerializable and
    /// idempotently re-attaches any a later third-party EnchantInternal replay dropped. No-op when
    /// the card carried no extras (no snapshot) or when nothing is missing. Returns true if it
    /// repaired anything. Pure data mutation of a deck card only — never fires enchant lifecycle
    /// hooks, enqueues actions, or consumes RNG (multiplayer-checksum safe; see the Launch postfix).
    /// </summary>
    internal static bool ReassertExtraEnchantmentsFromSnapshot(CardModel? card)
    {
        if (card == null || !DesiredExtrasSnapshots.TryGetValue(card, out DesiredExtrasSnapshot? snapshot))
        {
            return false;
        }

        if (!ReconcileAdditionalEnchantments(card, snapshot.Extras))
        {
            return false;
        }

        ApplyApplicationOrder(card, snapshot.Order);
        NormalizeCardEnchantmentStacks(card);
        TriggerEnchantmentChanged(card);
        card.FinalizeUpgradeInternal();
        MultiEnchantmentStackSupport.RefreshDerivedState(card);
        return true;
    }

    private static void ApplyApplicationOrder(CardModel card, IReadOnlyList<ModelId>? order)
    {
        if (order == null || order.Count == 0)
        {
            return;
        }

        CardEnchantmentState state = CardStates.GetOrCreateValue(card);
        state.ApplicationOrder.Clear();
        state.ApplicationOrder.AddRange(order);
    }

    // Best-effort parse of the saved application order for the snapshot. The authoritative parse
    // (with telemetry on failure) stays in DeserializeApplicationOrder; this copy must not throw.
    private static List<ModelId>? TryReadApplicationOrder(SerializableCard save)
    {
        if (!TryGetSavedString(save.Props, OrderSavePropertyName, out string payload) || string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<ModelId>>(payload);
        }
        catch
        {
            return null;
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
            MultiEnchantmentMod.Logger.Error($"Failed to deserialize enchantment order for card {GetSafeCardId(card)}: {ex}");
            Telemetry.TelemetryCollector.NoteDeserializationFailure(
                $"{OrderSavePropertyName}:payload", GetSafeCardId(card), ex);
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
            string rawPrimaryLine = WrapExtraCardText(rawPrimaryText, preserveBbCode: false);
            string primaryLine = FormatExtraCardTextLine(card.Enchantment, primaryText);
            if (hasRawPrimaryText && rawPrimaryLine != primaryLine)
            {
                description = description.Replace(rawPrimaryLine, primaryLine);
            }
            else if (!hasRawPrimaryText)
            {
                lines.Add(primaryLine);
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

            lines.Add(FormatExtraCardTextLine(enchantment, text));
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
        List<IHoverTip> tips = original.ToList();
        tips.AddRange(GetAdditionalEnchantments(card)
            .Where(enchantment => MultiEnchantmentScopeSupport.IsActive(card, enchantment))
            .SelectMany(static enchantment => enchantment.HoverTips));

        // Display-only provider markers are not stored enchantments, so surface their hover tips
        // here too — mirroring how vanilla shows Enchantment.HoverTips at the card level — so a
        // marker icon can explain itself instead of being a mystery glyph.
        tips.AddRange(GetDisplayOnlyMarkerHoverTips(card));

        return tips.Distinct().ToList();
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
        object? extraCardText = enchantment?.DynamicExtraCardText;
        if (extraCardText == null)
        {
            return false;
        }

        try
        {
            string? formatted = enchantment?.DynamicExtraCardText?.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                text = formatted;
                return true;
            }
        }
        catch (Exception ex)
        {
            LogExtraCardTextFormatFailureOnce(enchantment, ex);
        }

        string? raw = ExtractPlainLocalizationText(extraCardText);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        text = raw;
        return true;
    }

    private static void LogExtraCardTextFormatFailureOnce(EnchantmentModel? enchantment, Exception ex)
    {
        Type? enchantmentType = enchantment?.GetType();
        if (enchantmentType == null)
        {
            return;
        }

        lock (ExtraCardTextWarningLock)
        {
            if (!ExtraCardTextFormatWarningTypes.Add(enchantmentType))
            {
                return;
            }
        }

        MultiEnchantmentMod.Logger.Warn(
            $"[MultiEnchantment] Failed to format extra card text for {enchantmentType.FullName}; " +
            $"falling back to raw text when available. {ex.GetType().Name}: {ex.Message}");
    }

    private static string? ExtractPlainLocalizationText(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        string? rawText = TryInvokeLocalizationStringMethod(value, "GetRawText");
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            return rawText;
        }

        foreach (string memberName in new[]
                 {
                     "RawText",
                     "Raw",
                     "Text",
                     "Value",
                     "Key",
                     "LocalizationKey",
                     "LocKey",
                 })
        {
            object? memberValue = GetLocalizationPropertyOrFieldValue(value, memberName);
            if (memberValue is string memberText && !string.IsNullOrWhiteSpace(memberText))
            {
                return memberText;
            }
        }

        return null;
    }

    private static string? TryInvokeLocalizationStringMethod(object? value, string methodName)
    {
        if (value == null)
        {
            return null;
        }

        try
        {
            string? text = value.GetType()
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
                ?.Invoke(value, null)?.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static object? GetLocalizationPropertyOrFieldValue(object? target, string memberName)
    {
        if (target == null)
        {
            return null;
        }

        try
        {
            Type type = target.GetType();
            return type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target)
                   ?? type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatExtraCardTextLine(EnchantmentModel enchantment, string text) =>
        WrapExtraCardText(
            text,
            EnchantmentRegistry.GetPresentationStyle(enchantment.GetType()).PreserveExtraTextBbCode);

    private static string WrapExtraCardText(string text, bool preserveBbCode) =>
        preserveBbCode
            ? text
            : "[purple]" + text + "[/purple]";

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

        Player? owner = card.Owner;
        IRunState? runState = owner?.RunState;
        if (owner == null || runState == null)
        {
            return;
        }

        runState.CurrentMapPointHistoryEntry?
            .GetEntry(owner.NetId)?
            .CardsEnchanted
            .Add(new CardEnchantmentHistoryEntry(card, enchantment.Id));
    }
}

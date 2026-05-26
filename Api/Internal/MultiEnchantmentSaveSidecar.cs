using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MultiEnchantmentMod.Api.Internal;

internal static class MultiEnchantmentSaveSidecar
{
    private const string FileName = "multi_enchantment_save_sidecar.json";
    private const string Prefix = "MultiEnchantment";

    // Special MultiEnchantment-prefixed SavedProperty kept in the main save (NOT stripped to
    // sidecar). Holds a stable per-EnchantmentModel GUID so the v2 sidecar key (`v2e:{guid}`)
    // can be looked up at load time before any other multi-enchant state is restored. Survives
    // the SerializableEnchantment ↔ EnchantmentModel round trip via the vanilla SavedProperties
    // bag; size is one ~36-char string per enchantment, negligible.
    internal const string InstanceIdPropertyName = "MultiEnchantmentInstanceId";

    private const string CardKeyPrefix = "v2c:";
    private const string EnchantmentKeyPrefix = "v2e:";

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static SidecarDocument document = new();
    private static bool loaded;
    private static bool dirty;

    internal static bool IsMultiEnchantmentProperty(string? name) =>
        name != null && name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// True for the one MultiEnchantment-prefixed property we deliberately leave in the main
    /// vanilla save: <see cref="InstanceIdPropertyName"/>. Strip routines exclude it via this
    /// check so it remains accessible at load time for sidecar lookup.
    /// </summary>
    private static bool ShouldStripFromMainSave(string? name) =>
        IsMultiEnchantmentProperty(name) &&
        !string.Equals(name, InstanceIdPropertyName, StringComparison.Ordinal);

    internal static void Reload()
    {
        lock (Sync)
        {
            loaded = false;
            EnsureLoadedLocked();
        }
    }

    internal static void CaptureCard(CardModel card, SerializableCard save)
    {
        if (card == null || save == null)
        {
            return;
        }

        CaptureSerializableCard(save);
    }

    internal static void CaptureEnchantment(EnchantmentModel enchantment, SerializableEnchantment save)
    {
        if (enchantment == null || save == null)
        {
            return;
        }

        CaptureSerializableEnchantment(save);

        // Mirror the (possibly freshly generated) instance id back onto the live model so the
        // next in-session ToSerializable produces the same key and we don't orphan the sidecar
        // entry we just wrote. Idempotent — Upsert overwrites the same value when present.
        if (TryReadInstanceId(save) is { } instanceId)
        {
            enchantment.Props ??= new SavedProperties();
            enchantment.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
            Upsert(enchantment.Props.strings, InstanceIdPropertyName, instanceId);
        }
    }

    internal static void CaptureSerializableCard(SerializableCard card)
    {
        if (card == null)
        {
            return;
        }

        if (TryExtractPayload(card.Props, out SavedPropertiesPayload payload))
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                document.Cards[BuildCardKey(card)] = payload;
                dirty = true;
            }
        }

        CaptureSerializableEnchantment(card.Enchantment);
        CaptureNestedProperties(card.Props);
    }

    internal static void CaptureSerializableEnchantment(SerializableEnchantment? enchantment)
    {
        if (enchantment == null)
        {
            return;
        }

        if (TryExtractPayload(enchantment.Props, out SavedPropertiesPayload payload))
        {
            // Allocate or recover the stable instance id. Done lazily here (rather than at
            // model creation) so legacy in-memory enchantments that pre-date this code path
            // get an id the first time they're serialized — keeping the migration boundary
            // local to the sidecar.
            string instanceId = EnsureInstanceId(enchantment);
            lock (Sync)
            {
                EnsureLoadedLocked();
                document.Enchantments[$"{EnchantmentKeyPrefix}{instanceId}"] = payload;
                dirty = true;
            }
        }

        CaptureNestedProperties(enchantment.Props);
    }

    internal static void RestoreInto(SerializableCard save)
    {
        if (save == null)
        {
            return;
        }

        SavedPropertiesPayload? payload;
        string v2Key = BuildCardKey(save);
        string legacyKey = BuildLegacyCardKey(save);
        bool migratedFromLegacy = false;
        lock (Sync)
        {
            EnsureLoadedLocked();
            if (!document.Cards.TryGetValue(v2Key, out payload) &&
                document.Cards.TryGetValue(legacyKey, out payload))
            {
                // Legacy hit — promote to v2 key so subsequent reads / saves use the stable
                // schema. Keep the legacy entry around for now; a future release will drop
                // it after enough players have migrated.
                document.Cards[v2Key] = payload;
                dirty = true;
                migratedFromLegacy = true;
            }
        }

        if (payload != null)
        {
            SavedProperties? props = save.Props;
            ApplyPayload(ref props, payload);
            save.Props = props;

            if (migratedFromLegacy)
            {
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment][SaveSidecar] Migrated legacy card key {legacyKey} → {v2Key}.");
            }
        }

        RestoreInto(save.Enchantment);
    }

    internal static void RestoreInto(SerializableEnchantment? save)
    {
        if (save == null)
        {
            return;
        }

        SavedPropertiesPayload? payload = null;
        string? v2Key = BuildEnchantmentKey(save);
        string legacyKey = BuildLegacyEnchantmentKey(save);
        bool migratedFromLegacy = false;

        lock (Sync)
        {
            EnsureLoadedLocked();
            if (v2Key != null && document.Enchantments.TryGetValue(v2Key, out payload))
            {
                // direct v2 hit
            }
            else if (document.Enchantments.TryGetValue(legacyKey, out payload))
            {
                // Legacy hit — assign an instance id now (if the save didn't carry one) and
                // rewrite the payload under v2 so this enchantment migrates exactly once.
                string instanceId = EnsureInstanceId(save);
                document.Enchantments[$"{EnchantmentKeyPrefix}{instanceId}"] = payload;
                dirty = true;
                migratedFromLegacy = true;
            }
        }

        if (payload != null)
        {
            SavedProperties? props = save.Props;
            ApplyPayload(ref props, payload);
            save.Props = props;

            if (migratedFromLegacy)
            {
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment][SaveSidecar] Migrated legacy enchantment key {legacyKey} → v2.");
            }
        }
    }

    internal static void PrepareRunForDisk(SerializableRun save)
    {
        if (save == null)
        {
            return;
        }

        foreach (SerializableCard card in EnumerateCards(save))
        {
            CaptureSerializableCard(card);
            StripForDisk(card);
        }

        Flush();
    }

    internal static void StripForDisk(SerializableCard card)
    {
        if (card == null)
        {
            return;
        }

        StripProperties(card.Props);
        StripForDisk(card.Enchantment);
        StripNestedProperties(card.Props);
        if (IsEmpty(card.Props))
        {
            card.Props = null;
        }
    }

    internal static void StripForDisk(SerializableEnchantment? enchantment)
    {
        if (enchantment == null)
        {
            return;
        }

        StripProperties(enchantment.Props);
        StripNestedProperties(enchantment.Props);
        if (IsEmpty(enchantment.Props))
        {
            enchantment.Props = null;
        }
    }

    internal static void Flush()
    {
        lock (Sync)
        {
            EnsureLoadedLocked();
            FlushLocked();
        }
    }

private static bool TryExtractPayload(SavedProperties? props, out SavedPropertiesPayload payload)
    {
        payload = new SavedPropertiesPayload();
        if (props?.strings != null)
        {
            foreach (SavedProperties.SavedProperty<string> property in props.strings)
            {
                // InstanceId stays in the main save as a lookup index; the sidecar payload only
                // carries the rich state. Skipping it here also keeps the sidecar JSON minimal.
                if (ShouldStripFromMainSave(property.name))
                {
                    payload.Strings[property.name] = property.value;
                }
            }
        }

        if (props?.intArrays != null)
        {
            foreach (SavedProperties.SavedProperty<int[]> property in props.intArrays)
            {
                if (ShouldStripFromMainSave(property.name))
                {
                    payload.IntArrays[property.name] = property.value.ToArray();
                }
            }
        }

        return payload.HasAny;
    }

    private static void ApplyPayload(ref SavedProperties? props, SavedPropertiesPayload payload)
    {
        if (!payload.HasAny)
        {
            return;
        }

        props ??= new SavedProperties();
        if (payload.Strings.Count > 0)
        {
            props.strings ??= new List<SavedProperties.SavedProperty<string>>();
            foreach ((string name, string value) in payload.Strings)
            {
                Upsert(props.strings, name, value);
            }
        }

        if (payload.IntArrays.Count > 0)
        {
            props.intArrays ??= new List<SavedProperties.SavedProperty<int[]>>();
            foreach ((string name, int[] value) in payload.IntArrays)
            {
                Upsert(props.intArrays, name, value.ToArray());
            }
        }
    }

    private static void StripProperties(SavedProperties? props)
    {
        if (props == null)
        {
            return;
        }

        props.strings?.RemoveAll(property => ShouldStripFromMainSave(property.name));
        props.intArrays?.RemoveAll(property => ShouldStripFromMainSave(property.name));
    }

    private static void CaptureNestedProperties(SavedProperties? props)
    {
        if (props == null)
        {
            return;
        }

        if (props.cards != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard> property in props.cards)
            {
                CaptureSerializableCard(property.value);
            }
        }

        if (props.cardArrays != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard[]> property in props.cardArrays)
            {
                foreach (SerializableCard card in property.value)
                {
                    CaptureSerializableCard(card);
                }
            }
        }
    }

    private static void StripNestedProperties(SavedProperties? props)
    {
        if (props == null)
        {
            return;
        }

        if (props.cards != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard> property in props.cards)
            {
                StripForDisk(property.value);
            }
        }

        if (props.cardArrays != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard[]> property in props.cardArrays)
            {
                foreach (SerializableCard card in property.value)
                {
                    StripForDisk(card);
                }
            }
        }
    }

    private static IEnumerable<SerializableCard> EnumerateCards(SerializableRun save)
    {
        if (save.Players != null)
        {
            foreach (SerializablePlayer player in save.Players)
            {
                if (player.Deck == null)
                {
                    continue;
                }

                foreach (SerializableCard card in player.Deck)
                {
                    yield return card;
                }
            }
        }

        if (save.PreFinishedRoom?.ExtraRewards == null)
        {
            yield break;
        }

        foreach (List<SerializableReward> rewards in save.PreFinishedRoom.ExtraRewards.Values)
        {
            foreach (SerializableReward reward in rewards)
            {
                if (reward.SpecialCard != null)
                {
                    yield return reward.SpecialCard;
                }
            }
        }
    }

    /// <summary>
    /// v2 card key. <c>SerializableCard.Id</c> is the game-assigned GUID for the card model and
    /// is stable across save/load, so we don't need to mix in upgrade / floor / primary
    /// enchantment state (all of which can drift mid-run, orphaning the entry).
    /// </summary>
    private static string BuildCardKey(SerializableCard card) =>
        $"{CardKeyPrefix}{card.Id}";

    /// <summary>
    /// v2 enchantment key. Derived from the per-instance GUID stored as
    /// <see cref="InstanceIdPropertyName"/> on the enchantment's SavedProperties. The capture
    /// path generates one if absent. Returns <c>null</c> when no instance id can be assigned
    /// (e.g. SerializableEnchantment with null Props on a read-only restore path).
    /// </summary>
    private static string? BuildEnchantmentKey(SerializableEnchantment enchantment) =>
        TryReadInstanceId(enchantment) is { } id ? $"{EnchantmentKeyPrefix}{id}" : null;

    /// <summary>
    /// Legacy card key from the pre-v2 (composite Id+upgrade+primary+floor) scheme. Used as a
    /// migration fallback when v2 lookup misses.
    /// </summary>
    private static string BuildLegacyCardKey(SerializableCard card)
    {
        string primary = card.Enchantment == null ? "none" : BuildLegacyEnchantmentKey(card.Enchantment);
        return $"{card.Id}#u{card.CurrentUpgradeLevel}#e{primary}#f{card.FloorAddedToDeck?.ToString() ?? "-"}";
    }

    private static string BuildLegacyEnchantmentKey(SerializableEnchantment enchantment) =>
        $"{enchantment.Id}#a{enchantment.Amount}";

    private static string? TryReadInstanceId(SerializableEnchantment enchantment)
    {
        if (enchantment.Props?.strings == null)
        {
            return null;
        }

        foreach (SavedProperties.SavedProperty<string> property in enchantment.Props.strings)
        {
            if (string.Equals(property.name, InstanceIdPropertyName, StringComparison.Ordinal))
            {
                return string.IsNullOrEmpty(property.value) ? null : property.value;
            }
        }

        return null;
    }

    private static string EnsureInstanceId(SerializableEnchantment enchantment)
    {
        if (TryReadInstanceId(enchantment) is { } existing)
        {
            return existing;
        }

        enchantment.Props ??= new SavedProperties();
        enchantment.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
        string id = Guid.NewGuid().ToString("N");
        enchantment.Props.strings.Add(new SavedProperties.SavedProperty<string>(InstanceIdPropertyName, id));
        return id;
    }

    private static void Upsert<T>(List<SavedProperties.SavedProperty<T>> list, string name, T value)
    {
        SavedProperties.SavedProperty<T> property = new(name, value);
        int index = list.FindIndex(existing => string.Equals(existing.name, name, StringComparison.Ordinal));
        if (index >= 0)
        {
            list[index] = property;
        }
        else
        {
            list.Add(property);
        }
    }

    private static bool IsEmpty(SavedProperties? props)
    {
        if (props == null)
        {
            return true;
        }

        return IsEmpty(props.ints) &&
               IsEmpty(props.bools) &&
               IsEmpty(props.strings) &&
               IsEmpty(props.intArrays) &&
               IsEmpty(props.modelIds) &&
               IsEmpty(props.cards) &&
               IsEmpty(props.cardArrays);
    }

    private static bool IsEmpty<T>(IReadOnlyCollection<T>? list)
    {
        return list == null || list.Count == 0;
    }

    private static void EnsureLoadedLocked()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        string path = GetGlobalPath();
        if (!File.Exists(path))
        {
            document = new SidecarDocument();
            return;
        }

        try
        {
            document = JsonSerializer.Deserialize<SidecarDocument>(File.ReadAllText(path), JsonOptions) ?? new SidecarDocument();
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][SaveSidecar] Failed to load sidecar; starting empty. {ex.GetBaseException().Message}");
            document = new SidecarDocument();
        }
    }

    private static void FlushLocked()
    {
        if (!dirty)
        {
            return;
        }

        string path = GetGlobalPath();
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
            dirty = false;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][SaveSidecar] Failed to flush sidecar. {ex.GetBaseException().Message}");
        }
    }

    private static string GetGlobalPath()
    {
        string localPath;
        try
        {
            localPath = SaveManager.Instance.IsProfileInitialized
                ? $"{UserDataPathProvider.GetProfileScopedBasePath(SaveManager.Instance.CurrentProfileId)}/{FileName}"
                : $"user://{FileName}";
        }
        catch
        {
            localPath = $"user://{FileName}";
        }

        return ProjectSettings.GlobalizePath(localPath);
    }

    private sealed class SidecarDocument
    {
        [JsonPropertyName("cards")]
        public Dictionary<string, SavedPropertiesPayload> Cards { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("enchantments")]
        public Dictionary<string, SavedPropertiesPayload> Enchantments { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class SavedPropertiesPayload
    {
        [JsonPropertyName("strings")]
        public Dictionary<string, string> Strings { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("int_arrays")]
        public Dictionary<string, int[]> IntArrays { get; set; } = new(StringComparer.Ordinal);

        [JsonIgnore]
        public bool HasAny => Strings.Count > 0 || IntArrays.Count > 0;
    }
}

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
            lock (Sync)
            {
                EnsureLoadedLocked();
                document.Enchantments[BuildEnchantmentKey(enchantment)] = payload;
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
        lock (Sync)
        {
            EnsureLoadedLocked();
            document.Cards.TryGetValue(BuildCardKey(save), out payload);
        }

        if (payload != null)
        {
            SavedProperties? props = save.Props;
            ApplyPayload(ref props, payload);
            save.Props = props;
        }

        RestoreInto(save.Enchantment);
    }

    internal static void RestoreInto(SerializableEnchantment? save)
    {
        if (save == null)
        {
            return;
        }

        SavedPropertiesPayload? payload;
        lock (Sync)
        {
            EnsureLoadedLocked();
            document.Enchantments.TryGetValue(BuildEnchantmentKey(save), out payload);
        }

        if (payload != null)
        {
            SavedProperties? props = save.Props;
            ApplyPayload(ref props, payload);
            save.Props = props;
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

    internal static void PruneStale(IEnumerable<CardModel> liveCards)
    {
        if (liveCards == null)
        {
            return;
        }

        HashSet<string> liveIds = liveCards.Select(card => card.Id.ToString()).ToHashSet(StringComparer.Ordinal);
        lock (Sync)
        {
            EnsureLoadedLocked();
            foreach (string key in document.Cards.Keys.Where(key => !liveIds.Any(id => key.Contains(id, StringComparison.Ordinal))).ToList())
            {
                document.Cards.Remove(key);
                dirty = true;
            }

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
                if (IsMultiEnchantmentProperty(property.name))
                {
                    payload.Strings[property.name] = property.value;
                }
            }
        }

        if (props?.intArrays != null)
        {
            foreach (SavedProperties.SavedProperty<int[]> property in props.intArrays)
            {
                if (IsMultiEnchantmentProperty(property.name))
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

        props.strings?.RemoveAll(property => IsMultiEnchantmentProperty(property.name));
        props.intArrays?.RemoveAll(property => IsMultiEnchantmentProperty(property.name));
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

    private static string BuildCardKey(SerializableCard card)
    {
        string primary = card.Enchantment == null ? "none" : BuildEnchantmentKey(card.Enchantment);
        return $"{card.Id}#u{card.CurrentUpgradeLevel}#e{primary}#f{card.FloorAddedToDeck?.ToString() ?? "-"}";
    }

    private static string BuildEnchantmentKey(SerializableEnchantment enchantment)
    {
        return $"{enchantment.Id}#a{enchantment.Amount}";
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

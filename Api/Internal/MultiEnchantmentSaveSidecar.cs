using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private const string MultiplayerFileName = "multi_enchantment_save_sidecar_mp.json";
    private const string Prefix = "MultiEnchantment";

    // MultiEnchantment-prefixed lookup properties kept in the main save (NOT stripped to
    // sidecar). They hold stable per-instance GUIDs so v2 sidecar keys can be looked up at
    // load time before any other multi-enchant state is restored.
    internal const string InstanceIdPropertyName = "MultiEnchantmentInstanceId";
    internal const string CardInstanceIdPropertyName = "MultiEnchantmentCardInstanceId";

    private const string CardKeyPrefix = "v2c:";
    private const string EnchantmentKeyPrefix = "v2e:";

    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<CardModel, CardInstanceIdState> CardInstanceIds = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions EmbeddedExtraEnchantmentJsonOptions = new()
    {
        IncludeFields = true,
    };

    private static SidecarDocument document = new();
    private static bool loaded;
    private static bool dirty;

    // Which on-disk file the current document maps to. SP and MP runs share the same static
    // document slot but persist to separate files so a saved MP run can't bloat or collide with
    // the SP sidecar. Set by Reload / PrepareRunForDisk before any load or flush.
    private static bool currentScopeMultiplayer;

    internal static bool IsMultiEnchantmentProperty(string? name) =>
        name != null && name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// True for MultiEnchantment-prefixed lookup properties we deliberately leave in the main
    /// vanilla save. Strip routines exclude these values so they remain accessible at load time
    /// for sidecar lookup.
    /// </summary>
    private static bool IsLookupProperty(string? name) =>
        string.Equals(name, InstanceIdPropertyName, StringComparison.Ordinal) ||
        string.Equals(name, CardInstanceIdPropertyName, StringComparison.Ordinal);

    private static bool ShouldStripFromMainSave(string? name) =>
        IsMultiEnchantmentProperty(name) &&
        !IsLookupProperty(name);

    private static bool IsRuntimeLookupProperty(string? name) =>
        IsLookupProperty(name);

    internal static void Reload(bool multiplayer)
    {
        lock (Sync)
        {
            currentScopeMultiplayer = multiplayer;
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

        CaptureSerializableCard(save, card, clearStaleEntries: true);
    }

    internal static void CaptureEnchantment(EnchantmentModel enchantment, SerializableEnchantment save)
    {
        if (enchantment == null || save == null)
        {
            return;
        }

        CaptureSerializableEnchantment(save, clearStaleEntries: true);

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

        CaptureSerializableCard(card, liveCard: null, clearStaleEntries: true);
    }

    private static void CaptureSerializableCard(SerializableCard card, CardModel? liveCard, bool clearStaleEntries)
    {
        if (TryExtractPayload(card.Props, out SavedPropertiesPayload payload))
        {
            string instanceId = EnsureCardInstanceId(card, liveCard);
            lock (Sync)
            {
                EnsureLoadedLocked();
                document.Cards[BuildCardKey(instanceId)] = payload;
                RemoveUnsafeModelIdCardKeyLocked(card);
                dirty = true;
            }
        }
        else
        {
            string? instanceId = TryGetKnownCardInstanceId(card, liveCard);
            lock (Sync)
            {
                EnsureLoadedLocked();
                bool removed = false;
                if (clearStaleEntries && instanceId != null)
                {
                    removed |= document.Cards.Remove(BuildCardKey(instanceId));
                }

                removed |= RemoveUnsafeModelIdCardKeyLocked(card);
                if (removed)
                {
                    dirty = true;
                }
            }
        }

        CaptureSerializableEnchantment(card.Enchantment, clearStaleEntries);
        CaptureNestedProperties(card.Props, clearStaleEntries);
    }

    internal static void CaptureSerializableEnchantment(SerializableEnchantment? enchantment)
    {
        CaptureSerializableEnchantment(enchantment, clearStaleEntries: true);
    }

    private static void CaptureSerializableEnchantment(SerializableEnchantment? enchantment, bool clearStaleEntries)
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
        else if (clearStaleEntries && BuildEnchantmentKey(enchantment) is { } staleKey)
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                if (document.Enchantments.Remove(staleKey))
                {
                    dirty = true;
                }
            }
        }

        CaptureNestedProperties(enchantment.Props, clearStaleEntries);
    }

    internal static void RestoreInto(SerializableCard save, CardModel? liveCard = null)
    {
        if (save == null)
        {
            return;
        }

        RestoreSerializableCard(save, liveCard, allowSidecarMutations: true);
    }

    private static void RestoreSerializableCard(
        SerializableCard save,
        CardModel? liveCard,
        bool allowSidecarMutations)
    {
        SavedPropertiesPayload? payload = null;
        string? cardInstanceId = TryGetKnownCardInstanceId(save, liveCard);
        string v2Key = cardInstanceId == null ? string.Empty : BuildCardKey(cardInstanceId);
        string legacyKey = BuildLegacyCardKey(save);
        bool migratedFromLegacy = false;
        lock (Sync)
        {
            EnsureLoadedLocked();
            if (!string.IsNullOrEmpty(v2Key) && document.Cards.TryGetValue(v2Key, out payload))
            {
                RememberCardInstanceId(liveCard, cardInstanceId!);
            }
            else if (document.Cards.TryGetValue(legacyKey, out payload))
            {
                if (allowSidecarMutations)
                {
                    // Legacy composite-key hit. Move it to a per-card instance key; do not read
                    // from or preserve the unsafe ModelId-only key because it leaks across runs.
                    cardInstanceId = EnsureCardInstanceId(save, liveCard);
                    v2Key = BuildCardKey(cardInstanceId);
                    document.Cards[v2Key] = payload;
                    document.Cards.Remove(legacyKey);
                    RemoveUnsafeModelIdCardKeyLocked(save);
                    dirty = true;
                    migratedFromLegacy = true;
                }
            }
            else if (allowSidecarMutations && RemoveUnsafeModelIdCardKeyLocked(save))
            {
                dirty = true;
            }
        }

        if (payload != null)
        {
            SavedProperties? props = save.Props;
            ApplyPayload(ref props, payload);
            save.Props = props;
            if (allowSidecarMutations && cardInstanceId != null)
            {
                UpsertCardInstanceId(save, cardInstanceId);
            }

            if (migratedFromLegacy)
            {
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment][SaveSidecar] Migrated legacy card key {legacyKey} → {v2Key}.");
            }
        }

        RestoreSerializableEnchantment(save.Enchantment, allowSidecarMutations);
        RestoreNestedProperties(save.Props, allowSidecarMutations);
    }

    internal static void RestoreInto(SerializableEnchantment? save)
    {
        if (save == null)
        {
            return;
        }

        RestoreSerializableEnchantment(save, allowSidecarMutations: true);
    }

    private static void RestoreSerializableEnchantment(
        SerializableEnchantment? save,
        bool allowSidecarMutations)
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
                if (allowSidecarMutations)
                {
                    string instanceId = EnsureInstanceId(save);
                    document.Enchantments[$"{EnchantmentKeyPrefix}{instanceId}"] = payload;
                    dirty = true;
                    migratedFromLegacy = true;
                }
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

    internal static void PrepareRunForDisk(
        SerializableRun save,
        bool multiplayer,
        bool clearStaleEntries = true)
    {
        if (save == null)
        {
            return;
        }

        // Rebuild this scope's sidecar from an empty document on every save. Starting fresh means
        // entries for cards/enchantments no longer in the run can't accumulate as orphans, and we
        // never persist another profile's stale in-memory document after a profile switch. The
        // capture+strip loop below repopulates it from the current run before the flush.
        lock (Sync)
        {
            currentScopeMultiplayer = multiplayer;
            document = new SidecarDocument();
            loaded = true;
            dirty = true;
        }

        foreach (SerializableCard card in EnumerateCards(save))
        {
            CaptureSerializableCard(card, liveCard: null, clearStaleEntries: clearStaleEntries);
            StripForDisk(card);
        }

        Flush();
    }

    internal static void RestoreRunFromDisk(SerializableRun save)
    {
        if (save == null)
        {
            return;
        }

        foreach (SerializableCard card in EnumerateCards(save))
        {
            RestoreSerializableCard(card, liveCard: null, allowSidecarMutations: false);
            StripSidecarStorageProperties(card);
        }
    }

    private static void RestoreNestedProperties(SavedProperties? props, bool allowSidecarMutations)
    {
        if (props == null)
        {
            return;
        }

        if (props.cards != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard> property in props.cards)
            {
                RestoreSerializableCard(property.value, liveCard: null, allowSidecarMutations);
            }
        }

        if (props.cardArrays == null)
        {
            return;
        }

        foreach (SavedProperties.SavedProperty<SerializableCard[]> property in props.cardArrays)
        {
            foreach (SerializableCard nested in property.value)
            {
                RestoreSerializableCard(nested, liveCard: null, allowSidecarMutations);
            }
        }
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

    private static void StripSidecarStorageProperties(SerializableCard card)
    {
        if (card == null)
        {
            return;
        }

        StripProperties(card.Props, IsRuntimeLookupProperty);
        StripSidecarStorageProperties(card.Enchantment);
        StripEmbeddedExtraEnchantmentLookupProperties(card.Props);
        StripNestedProperties(card.Props, StripSidecarStorageProperties);
        if (IsEmpty(card.Props))
        {
            card.Props = null;
        }
    }

    private static void StripSidecarStorageProperties(SerializableEnchantment? enchantment)
    {
        if (enchantment == null)
        {
            return;
        }

        StripProperties(enchantment.Props, IsRuntimeLookupProperty);
        StripNestedProperties(enchantment.Props, StripSidecarStorageProperties);
        if (IsEmpty(enchantment.Props))
        {
            enchantment.Props = null;
        }
    }

    private static void StripEmbeddedExtraEnchantmentLookupProperties(SavedProperties? props)
    {
        if (props?.strings == null)
        {
            return;
        }

        int index = props.strings.FindIndex(property =>
            string.Equals(property.name, MultiEnchantmentSupport.SavePropertyName, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        string payload = props.strings[index].value;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            List<SerializableEnchantment>? extras =
                JsonSerializer.Deserialize<List<SerializableEnchantment>>(payload, EmbeddedExtraEnchantmentJsonOptions);
            if (extras == null || extras.Count == 0)
            {
                return;
            }

            foreach (SerializableEnchantment extra in extras)
            {
                StripSidecarStorageProperties(extra);
            }

            props.strings[index] = new SavedProperties.SavedProperty<string>(
                MultiEnchantmentSupport.SavePropertyName,
                JsonSerializer.Serialize(extras, EmbeddedExtraEnchantmentJsonOptions));
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][SaveSidecar] Failed to sanitize embedded extra enchantments: {ex.GetBaseException().Message}");
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
                // InstanceId / CardInstanceId stay in the main save as lookup indexes; the
                // sidecar payload only carries rich state. Skipping them here also keeps the
                // sidecar JSON minimal.
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
        StripProperties(props, ShouldStripFromMainSave);
    }

    private static void StripProperties(SavedProperties? props, Func<string?, bool> shouldStrip)
    {
        if (props == null)
        {
            return;
        }

        props.strings?.RemoveAll(property => shouldStrip(property.name));
        props.intArrays?.RemoveAll(property => shouldStrip(property.name));
    }

    private static void CaptureNestedProperties(SavedProperties? props, bool clearStaleEntries)
    {
        if (props == null)
        {
            return;
        }

        if (props.cards != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard> property in props.cards)
            {
                CaptureSerializableCard(property.value, liveCard: null, clearStaleEntries: clearStaleEntries);
            }
        }

        if (props.cardArrays != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard[]> property in props.cardArrays)
            {
                foreach (SerializableCard card in property.value)
                {
                    CaptureSerializableCard(card, liveCard: null, clearStaleEntries: clearStaleEntries);
                }
            }
        }
    }

    private static void StripNestedProperties(SavedProperties? props)
    {
        StripNestedProperties(props, StripForDisk);
    }

    private static void StripNestedProperties(SavedProperties? props, Action<SerializableCard> stripCard)
    {
        if (props == null)
        {
            return;
        }

        if (props.cards != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard> property in props.cards)
            {
                stripCard(property.value);
            }
        }

        if (props.cardArrays != null)
        {
            foreach (SavedProperties.SavedProperty<SerializableCard[]> property in props.cardArrays)
            {
                foreach (SerializableCard card in property.value)
                {
                    stripCard(card);
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

    private static string BuildCardKey(string instanceId) =>
        $"{CardKeyPrefix}{instanceId}";

    private static string BuildUnsafeModelIdCardKey(SerializableCard card) =>
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

    private static string? TryReadCardInstanceId(SerializableCard card)
    {
        if (card.Props?.strings == null)
        {
            return null;
        }

        foreach (SavedProperties.SavedProperty<string> property in card.Props.strings)
        {
            if (string.Equals(property.name, CardInstanceIdPropertyName, StringComparison.Ordinal))
            {
                return string.IsNullOrEmpty(property.value) ? null : property.value;
            }
        }

        return null;
    }

    private static string? TryGetKnownCardInstanceId(SerializableCard card, CardModel? liveCard)
    {
        if (TryReadCardInstanceId(card) is { } saved)
        {
            RememberCardInstanceId(liveCard, saved);
            return saved;
        }

        if (liveCard != null &&
            CardInstanceIds.TryGetValue(liveCard, out CardInstanceIdState? state) &&
            !string.IsNullOrEmpty(state.InstanceId))
        {
            UpsertCardInstanceId(card, state.InstanceId);
            return state.InstanceId;
        }

        return null;
    }

    private static string EnsureCardInstanceId(SerializableCard card, CardModel? liveCard)
    {
        if (TryGetKnownCardInstanceId(card, liveCard) is { } existing)
        {
            return existing;
        }

        string id = Guid.NewGuid().ToString("N");
        UpsertCardInstanceId(card, id);
        RememberCardInstanceId(liveCard, id);
        return id;
    }

    private static void UpsertCardInstanceId(SerializableCard card, string id)
    {
        card.Props ??= new SavedProperties();
        card.Props.strings ??= new List<SavedProperties.SavedProperty<string>>();
        Upsert(card.Props.strings, CardInstanceIdPropertyName, id);
    }

    private static void RememberCardInstanceId(CardModel? card, string id)
    {
        if (card == null)
        {
            return;
        }

        CardInstanceIds.GetOrCreateValue(card).InstanceId = id;
    }

    private static bool RemoveUnsafeModelIdCardKeyLocked(SerializableCard card)
    {
        return document.Cards.Remove(BuildUnsafeModelIdCardKey(card));
    }

    private static bool RemoveMalformedCardInstanceKeysLocked()
    {
        bool removed = false;
        foreach (string key in document.Cards.Keys.Where(IsMalformedCardInstanceKey).ToArray())
        {
            removed |= document.Cards.Remove(key);
        }

        return removed;
    }

    private static bool IsMalformedCardInstanceKey(string key)
    {
        if (!key.StartsWith(CardKeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string instanceId = key[CardKeyPrefix.Length..];
        return !Guid.TryParseExact(instanceId, "N", out _);
    }

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
        Upsert(enchantment.Props.strings, InstanceIdPropertyName, id);
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
        string path = GetGlobalPath(currentScopeMultiplayer);
        if (File.Exists(path))
        {
            bool parsed = TryLoadDocument(path, out SidecarDocument loadedDocument);
            document = loadedDocument;
            if (parsed && RemoveMalformedCardInstanceKeysLocked())
            {
                dirty = true;
            }

            return;
        }

        // MP file missing: one-time migration seed from the legacy shared single-player file so a
        // multiplayer run that was in progress before the SP/MP file split doesn't lose its extra
        // enchantments on the first post-upgrade load. Instance keys are GUIDs, so any mixed-in SP
        // entries are harmless; the next MP save rebuilds the file as MP-only.
        if (currentScopeMultiplayer)
        {
            string legacyPath = GetGlobalPath(multiplayer: false);
            if (File.Exists(legacyPath) && TryLoadDocument(legacyPath, out SidecarDocument seeded))
            {
                document = seeded;
                return;
            }
        }

        document = new SidecarDocument();
    }

    private static bool TryLoadDocument(string path, out SidecarDocument result)
    {
        try
        {
            result = JsonSerializer.Deserialize<SidecarDocument>(File.ReadAllText(path), JsonOptions)
                ?? new SidecarDocument();
            return true;
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment][SaveSidecar] Failed to load sidecar; starting empty. {ex.GetBaseException().Message}");
            result = new SidecarDocument();
            return false;
        }
    }

    private static void FlushLocked()
    {
        if (!dirty)
        {
            return;
        }

        string path = GetGlobalPath(currentScopeMultiplayer);
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

    private static string GetGlobalPath(bool multiplayer)
    {
        string fileName = multiplayer ? MultiplayerFileName : FileName;
        string localPath;
        try
        {
            localPath = SaveManager.Instance.IsProfileInitialized
                ? $"{UserDataPathProvider.GetProfileScopedBasePath(SaveManager.Instance.CurrentProfileId)}/{fileName}"
                : $"user://{fileName}";
        }
        catch
        {
            localPath = $"user://{fileName}";
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

    private sealed class CardInstanceIdState
    {
        public string InstanceId { get; set; } = string.Empty;
    }
}

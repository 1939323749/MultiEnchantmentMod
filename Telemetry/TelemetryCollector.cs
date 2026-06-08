using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod.Telemetry;

internal static class TelemetryCollector
{
    private static string? _sessionId = Guid.NewGuid().ToString("D");
    private static bool _sessionSent;
    private static bool _sessionSendStarted;
    private static IRunState? _currentRunState;
    private static string? _runId;
    private static string? _runIdentityKey;
    private static string? _runInstanceId;
    private static string? _runSeed;
    private static string? _runCharacterName;
    private static int _runIndex;
    private static int _runCombatCount;
    private static bool _runStartedInCurrentSession;
    private static bool _runEndedSent;
    private static bool _pendingLossCombatForRunSummary;
    private static string? _pendingLossRunIdentityKey;
    private static bool? _pendingRunIsMultiplayer;
    private static bool? _runIsMultiplayer;
    private static int? _runPlayerCount;
    private static TelemetryHashCache? _runtimeHashCache;

    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── Application source hint (set by API entry points) ─────────────
    private static readonly AsyncLocal<string?> ApplicationSourceHint = new();

    /// <summary>
    /// Set by legacy internal entry points before calling into mutation methods.
    /// Prefer <see cref="PushApplicationSource"/> so the hint is restored on exceptions / awaits.
    /// </summary>
    internal static void SetApplicationSource(string source) => ApplicationSourceHint.Value = source;

    internal static IDisposable PushApplicationSource(string source)
    {
        string? previous = ApplicationSourceHint.Value;
        ApplicationSourceHint.Value = source;
        return new ApplicationSourceScope(previous);
    }

    // ── Combat counters (reset each combat) ──────────────────────────────
    private static int _totalApplications;
    private static int _totalRemovals;
    private static int _maxEnchantmentsOnCard;
    private static int _eventBusPublishCount;
    private static int _deserializationFailureCount;
    private static int _enchantedCardPlays;
    private static readonly Dictionary<string, int> EnchantmentTypeCounts = new();
    private static readonly Dictionary<string, int> ThirdPartyEnchantmentCounts = new();
    private static readonly Dictionary<string, int> ApplicationSourceCounts = new();
    private static readonly Dictionary<string, int> EnchantedCardPlayCounts = new();
    private static readonly List<EnchantApplicationRecord> EnchantApplications = new();
    private static readonly List<DeserializationFailureRecord> DeserializationFailures = new();
    // Type name → localized title cache for this combat (avoids repeated reflection)
    private static readonly Dictionary<string, string> EnchantmentTitleCache = new();

    // ── Session data (set once at startup) ───────────────────────────────

    /// <summary>
    /// Queues session data exactly once. Called at first BeforeCombatStart, after
    /// AssemblyScanner.SealRegistryIfNeeded() has ensured all third-party mods have registered.
    /// </summary>
    internal static string SessionId => _sessionId ??= Guid.NewGuid().ToString("D");
    internal static bool SessionDataQueued => _sessionSent;

    internal static void SendSessionDataOnce()
    {
        if (_sessionSent || _sessionSendStarted || !TelemetryConfig.IsEnabled) return;
        _sessionSendStarted = true;

        TelemetryReporter.EnqueueBackgroundWork(SendSessionDataOnceAsync);
    }

    private static Task SendSessionDataOnceAsync()
    {
        DiagLog($"=== SendSessionDataOnce started, DiagLogPath={TelemetryDiagnostics.LogPath} ===");

        try
        {
            _sessionId = SessionId;

            string osPlatform = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows) ? "Windows"
                : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Linux) ? "Linux"
                : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX) ? "macOS" : "Unknown";

            var loadedMods = CollectLoadedMods();
            var harmonyConflicts = CollectHarmonyConflicts();
            var unregisteredMods = CollectUnregisteredEnchantmentMods();
            var registeredTypes = CollectRegisteredEnchantmentTypes();
            var apiCompatibilityResults = CollectApiCompatibilityResults();
            var catalogData = BuildModCatalogData(loadedMods, out string catalogHash);
            string environmentHash = ComputeStableHash(new
            {
                catalog_hash = catalogHash,
                harmony_conflicts = StableSortForHash(harmonyConflicts),
                api_compatibility_results = StableSortForHash(apiCompatibilityResults),
                unregistered_enchantment_mods = StableSortForHash(unregisteredMods),
            });

            // Environment data (large JSONB) is stored once per unique hash in a
            // separate table. Sessions only reference the hash — saves ~96% per row.
            var environmentData = new
            {
                environment_hash = environmentHash,
                registered_enchantment_count = registeredTypes.Count,
                registered_enchantment_types = registeredTypes,
                loaded_mod_assemblies = loadedMods,
                harmony_conflicts = harmonyConflicts,
                harmony_conflict_count = harmonyConflicts.Count,
                unregistered_enchantment_mods = unregisteredMods,
                api_compatibility_results = apiCompatibilityResults,
            };

            // Session row is now slim — only scalars and hash references.
            var sessionData = new
            {
                id = _sessionId,
                installation_id = TelemetryConfig.InstallationId,
                mod_version = TelemetryConfig.ModVersion,
                game_version = TelemetryConfig.GameVersion,
                api_version = MultiEnchantmentApiVersion.Current,
                os_platform = osPlatform,
                catalog_hash = catalogHash,
                environment_hash = environmentHash,
            };

            TelemetryHashCache cache = ReadHashCache();
            bool uploadEnvironment = !string.Equals(cache.EnvironmentHash, environmentHash, StringComparison.Ordinal);
            bool uploadCatalog = !string.Equals(cache.CatalogHash, catalogHash, StringComparison.Ordinal);
            string locale = SafeGetLocale();

            List<object>? refCards = null, refRelics = null, refPowers = null;
            string? refCardsHash = null, refRelicsHash = null, refPowersHash = null;
            try
            {
                refCards = CollectCardCatalog();
                refCardsHash = ComputeReferenceCatalogHash("cards", TelemetryConfig.GameVersion, locale, refCards);
            }
            catch (Exception ex) { DiagLog($"CollectCardCatalog FAILED: {ex}"); }
            try
            {
                refRelics = CollectRelicCatalog();
                refRelicsHash = ComputeReferenceCatalogHash("relics", TelemetryConfig.GameVersion, locale, refRelics);
            }
            catch (Exception ex) { DiagLog($"CollectRelicCatalog FAILED: {ex}"); }
            try
            {
                refPowers = CollectPowerCatalog();
                refPowersHash = ComputeReferenceCatalogHash("powers", TelemetryConfig.GameVersion, locale, refPowers);
            }
            catch (Exception ex) { DiagLog($"CollectPowerCatalog FAILED: {ex}"); }
            DiagLog($"Ref counts: cards={refCards?.Count}, relics={refRelics?.Count}, powers={refPowers?.Count}");

            bool uploadRefCards = refCardsHash != null &&
                !string.Equals(cache.ReferenceCardsHash, refCardsHash, StringComparison.Ordinal);
            bool uploadRefRelics = refRelicsHash != null &&
                !string.Equals(cache.ReferenceRelicsHash, refRelicsHash, StringComparison.Ordinal);
            bool uploadRefPowers = refPowersHash != null &&
                !string.Equals(cache.ReferencePowersHash, refPowersHash, StringComparison.Ordinal);

            return SendStartupDataAndUpdateCacheAsync(
                uploadEnvironment ? environmentData : null,
                sessionData,
                uploadCatalog ? catalogData : null,
                uploadRefCards ? refCards : null,
                uploadRefRelics ? refRelics : null,
                uploadRefPowers ? refPowers : null,
                cache,
                environmentHash,
                catalogHash,
                refCardsHash,
                refRelicsHash,
                refPowersHash);
        }
        catch (Exception ex)
        {
            _sessionSendStarted = false;
            DiagLog($"SendSessionDataOnce FAILED before queueing startup data: {ex}");
            return Task.CompletedTask;
        }
    }

    private static async Task SendStartupDataAndUpdateCacheAsync(
        object? environmentData,
        object sessionData,
        object? catalogData,
        List<object>? refCards,
        List<object>? refRelics,
        List<object>? refPowers,
        TelemetryHashCache cache,
        string environmentHash,
        string catalogHash,
        string? refCardsHash,
        string? refRelicsHash,
        string? refPowersHash)
    {
        TelemetryReporter.StartupUploadResult result =
            await TelemetryReporter.SendStartupDataAsync(
                environmentData, sessionData, catalogData, refCards, refRelics, refPowers);

        if (result.SessionUploaded)
        {
            _sessionSent = true;
        }
        else
        {
            _sessionSendStarted = false;
        }

        bool changed = false;
        if (environmentData == null || result.EnvironmentUploaded)
        {
            cache.EnvironmentHash = environmentHash;
            changed = true;
        }

        if (catalogData == null || result.ModCatalogUploaded)
        {
            cache.CatalogHash = catalogHash;
            changed = true;
        }

        if (refCardsHash != null && (refCards == null || result.RefCardsUploaded))
        {
            cache.ReferenceCardsHash = refCardsHash;
            changed = true;
        }

        if (refRelicsHash != null && (refRelics == null || result.RefRelicsUploaded))
        {
            cache.ReferenceRelicsHash = refRelicsHash;
            changed = true;
        }

        if (refPowersHash != null && (refPowers == null || result.RefPowersUploaded))
        {
            cache.ReferencePowersHash = refPowersHash;
            changed = true;
        }

        if (changed)
        {
            WriteHashCache(cache);
        }
    }

    // ── Combat lifecycle ─────────────────────────────────────────────────

    internal static void ResetForCombat()
    {
        _totalApplications = 0;
        _totalRemovals = 0;
        _maxEnchantmentsOnCard = 0;
        _eventBusPublishCount = 0;
        _deserializationFailureCount = 0;
        _enchantedCardPlays = 0;
        EnchantmentTypeCounts.Clear();
        ThirdPartyEnchantmentCounts.Clear();
        ApplicationSourceCounts.Clear();
        EnchantedCardPlayCounts.Clear();
        EnchantApplications.Clear();
        DeserializationFailures.Clear();
        EnchantmentTitleCache.Clear();
    }

    internal static void NoteCombatStarting(IRunState? runState)
    {
        if (!TelemetryConfig.IsEnabled || runState == null) return;

        try
        {
            EnsureRun(runState);
        }
        catch { /* telemetry must never crash the game */ }
    }

    internal static void SendCombatData(bool combatWon, IRunState? runState, ICombatState? combatState)
    {
        if (!TelemetryConfig.IsEnabled || _sessionId == null) return;

        try
        {
            EnsureRun(runState);
            var safeInvokerFailures = SafeInvoker.GetFailureSnapshot();
            var allEnchantApplicationsSnapshot = EnchantApplications.ToList();
            var enchantmentApplicationsSnapshot = allEnchantApplicationsSnapshot.Count <= 100
                ? allEnchantApplicationsSnapshot
                : allEnchantApplicationsSnapshot.Take(100).ToList();
            var enchantmentTypeCountsSnapshot = new Dictionary<string, int>(EnchantmentTypeCounts);
            var comboCountsSnapshot = ComputeComboCounts(allEnchantApplicationsSnapshot);
            var thirdPartyEnchantmentCountsSnapshot = new Dictionary<string, int>(ThirdPartyEnchantmentCounts);
            var applicationSourceCountsSnapshot = new Dictionary<string, int>(ApplicationSourceCounts);
            var enchantedCardPlayCountsSnapshot = new Dictionary<string, int>(EnchantedCardPlayCounts);
            var deserializationFailuresSnapshot = DeserializationFailures.ToList();

            // Combat context from runState / combatState
            string? characterName = null;
            string? encounterName = null;
            string? roomType = null;
            int? floor = null;
            int? ascension = null;
            int roundCount = combatState?.RoundNumber ?? 0;

            try
            {
                if (runState != null)
                {
                    floor = runState.TotalFloor;
                    ascension = runState.AscensionLevel;
                    roomType = runState.CurrentRoom?.RoomType.ToString();

                    // Get first player's character name
                    var players = combatState?.Players;
                    if (players != null)
                    {
                        foreach (var player in players)
                        {
                            characterName = player.Character?.Id.ToString();
                            if (!string.IsNullOrWhiteSpace(characterName))
                            {
                                _runCharacterName ??= characterName;
                            }
                            break;
                        }
                    }
                }

                if (combatState?.Encounter != null)
                {
                    encounterName = combatState.Encounter.Id.ToString();
                }
            }
            catch { /* context is best-effort */ }

            List<string>? deckCardIds = null;
            List<string>? relicIds = null;
            string? roomName = null;
            try { deckCardIds = CollectDeckCardIds(combatState); } catch { }
            try { relicIds = CollectRelicIds(combatState); } catch { }
            try { roomName = runState?.CurrentRoom?.ToString(); } catch { }
            var multiplayer = MergeMultiplayerContext(GetMultiplayerContext(runState, combatState));

            TelemetryReporter.SendCombat(new
            {
                session_id = _sessionId,
                installation_id = TelemetryConfig.InstallationId,
                run_id = _runId,
                run_instance_id = _runInstanceId,
                run_index = _runIndex > 0 ? _runIndex : (int?)null,
                run_seed = _runSeed,
                run_key = _runIdentityKey,
                combat_won = combatWon,
                combat_round_count = roundCount,

                // Combat context
                character = characterName,
                encounter = encounterName,
                room_type = roomType,
                room_name = roomName,
                floor,
                ascension,
                is_multiplayer = multiplayer.IsMultiplayer,
                player_count = multiplayer.PlayerCount,

                // Game state snapshot (null when no enchantment activity → omitted by serializer)
                deck_card_ids = deckCardIds,
                relic_ids = relicIds,

                // Enchantment usage
                total_enchant_applications = _totalApplications,
                total_enchant_removals = _totalRemovals,
                max_enchantments_on_single_card = _maxEnchantmentsOnCard,
                enchantment_applications = enchantmentApplicationsSnapshot,
                // Slim dict format: {type: count}. Titles are in the catalog.
                enchantment_type_counts = enchantmentTypeCountsSnapshot,
                enchantment_combo_counts = comboCountsSnapshot,
                third_party_enchantment_counts = thirdPartyEnchantmentCountsSnapshot,
                application_source_counts = applicationSourceCountsSnapshot,

                // Enchanted card play tracking
                enchanted_card_plays = _enchantedCardPlays,
                enchanted_card_play_counts = enchantedCardPlayCountsSnapshot,

                // Error tracking
                safe_invoker_failure_count = safeInvokerFailures.Sum(static kv => kv.Value),
                safe_invoker_failures = safeInvokerFailures.Select(static kv => new
                {
                    type = kv.Key.Type.FullName ?? kv.Key.Type.Name,
                    hook = kv.Key.Hook,
                    count = kv.Value,
                    assembly = kv.Key.Type.Assembly.GetName().Name,
                }).ToList(),
                deserialization_failure_count = _deserializationFailureCount,
                deserialization_failures = deserializationFailuresSnapshot,
                event_bus_publish_count = _eventBusPublishCount,
            });

            if (_runId != null)
            {
                _runCombatCount++;
            }

            if (!combatWon)
            {
                _pendingLossCombatForRunSummary = false;
                _pendingLossRunIdentityKey = null;
            }
        }
        catch { /* telemetry must never crash the game */ }
    }

    internal static void SendRunData(IRunState? runState, bool isVictory, bool isAbandoned)
    {
        if (!TelemetryConfig.IsEnabled || _sessionId == null) return;

        try
        {
            IRunState? summaryRunState = runState ?? _currentRunState;
            EnsureRun(summaryRunState);
            if (_runId == null || _runEndedSent) return;

            string outcome = isVictory
                ? "victory"
                : isAbandoned ? "abandoned" : "death";
            SendRunSummary(summaryRunState, outcome);
        }
        catch { /* telemetry must never crash the game */ }
    }

    internal static void NoteCombatLossProcessing(IRunState? runState)
    {
        if (!TelemetryConfig.IsEnabled || runState == null) return;

        try
        {
            EnsureRun(runState);
            if (_runId == null) return;

            _pendingLossCombatForRunSummary = true;
            _pendingLossRunIdentityKey = _runIdentityKey;
        }
        catch { /* telemetry must never crash the game */ }
    }

    // ── Note methods (called from framework code) ────────────────────────

    private static void EnsureRun(IRunState? runState)
    {
        if (runState == null) return;

        string identityKey = BuildRunIdentityKey(runState);
        if (_runId != null &&
            (ReferenceEquals(_currentRunState, runState) ||
             string.Equals(_runIdentityKey, identityKey, StringComparison.Ordinal)))
        {
            _currentRunState = runState;
            MergeMultiplayerContext(GetMultiplayerContext(runState, null));
            return;
        }

        if (_currentRunState != null && _runId != null && !_runEndedSent)
        {
            SendRunSummary(_currentRunState, "superseded_by_new_run_state");
        }

        StartRun(runState, identityKey);
    }

    private static void StartRun(IRunState runState, string identityKey)
    {
        _currentRunState = runState;
        _runIdentityKey = identityKey;
        _runId = Guid.NewGuid().ToString("D");
        _runInstanceId = _runId;
        _runSeed = GetRunSeed(runState);
        _runCharacterName = TryGetCharacterNameFromRunState(runState);
        _runIndex++;
        _runCombatCount = 0;
        _runStartedInCurrentSession = IsEarlyRunFloor(runState);
        _runEndedSent = false;
        _pendingLossCombatForRunSummary = false;
        _pendingLossRunIdentityKey = null;
        _runIsMultiplayer = _pendingRunIsMultiplayer;
        _runPlayerCount = null;
        _pendingRunIsMultiplayer = null;
        MergeMultiplayerContext(GetMultiplayerContext(runState, null));
    }

    private static void SendRunSummary(IRunState? runState, string outcome)
    {
        if (_sessionId == null || _runId == null) return;

        int? finalFloor = null;
        int? ascension = null;
        try { finalFloor = runState?.TotalFloor; } catch { }
        try { ascension = runState?.AscensionLevel; } catch { }

        int? combatCount = _runStartedInCurrentSession ? _runCombatCount : null;
        if (_runStartedInCurrentSession &&
            string.Equals(outcome, "death", StringComparison.OrdinalIgnoreCase) &&
            _pendingLossCombatForRunSummary &&
            string.Equals(_pendingLossRunIdentityKey, _runIdentityKey, StringComparison.Ordinal))
        {
            combatCount = (combatCount ?? 0) + 1;
        }

        var multiplayer = MergeMultiplayerContext(GetMultiplayerContext(runState, null));

        TelemetryReporter.SendRun(new
        {
            session_id = _sessionId,
            installation_id = TelemetryConfig.InstallationId,
            run_id = _runId,
            run_instance_id = _runInstanceId,
            run_index = _runIndex > 0 ? _runIndex : (int?)null,
            run_seed = _runSeed,
            run_key = _runIdentityKey,
            outcome,
            final_floor = finalFloor,
            character = GetCharacterName(runState),
            ascension,
            combat_count = combatCount,
            is_multiplayer = multiplayer.IsMultiplayer,
            player_count = multiplayer.PlayerCount,
            game_version = TelemetryConfig.GameVersion,
            mod_version = TelemetryConfig.ModVersion,
            api_version = MultiEnchantmentApiVersion.Current,
        });

        _runEndedSent = true;
    }

    internal static void NoteEnchantApplied(CardModel card, EnchantmentModel enchantment, int amount)
    {
        if (!TelemetryConfig.IsEnabled) return;

        try
        {
            _totalApplications++;

            // Consume the source hint set by the API entry point.
            string source = ApplicationSourceHint.Value ?? "unknown";
            ApplicationSourceHint.Value = null;
            ApplicationSourceCounts[source] = ApplicationSourceCounts.GetValueOrDefault(source) + 1;

            string typeName = enchantment.GetType().Name;
            string assemblyName = enchantment.GetType().Assembly.GetName().Name ?? "unknown";
            string enchantTitle = SafeGetEnchantTitle(enchantment);
            EnchantmentTypeCounts[typeName] = EnchantmentTypeCounts.GetValueOrDefault(typeName) + 1;

            // Cache the localized title for this type so counts can carry titles later.
            EnchantmentTitleCache.TryAdd(typeName, enchantTitle);

            // Track third-party (non-game) enchantments separately.
            string? ns = enchantment.GetType().Namespace;
            if (ns != null && !ns.StartsWith("MegaCrit.Sts2", StringComparison.Ordinal))
            {
                string key = $"{assemblyName}.{typeName}";
                ThirdPartyEnchantmentCounts[key] = ThirdPartyEnchantmentCounts.GetValueOrDefault(key) + 1;
                EnchantmentTitleCache.TryAdd(key, enchantTitle);
            }

            // Existing enchantments on the card — type names only (titles are in the catalog).
            List<string> existingTypes;
            try
            {
                existingTypes = MultiEnchantmentApi.GetEnchantments(card)
                    .Where(e => e != enchantment)
                    .Select(static e => e.GetType().Name)
                    .ToList();
            }
            catch { existingTypes = new List<string>(); }

            EnchantApplications.Add(new EnchantApplicationRecord
            {
                Card = new WeakReference<CardModel>(card),
                CardId = card.Id.ToString(),
                EnchantType = typeName,
                Amount = amount,
                Assembly = assemblyName,
                Source = source,
                ExistingTypes = existingTypes,
            });

            // Track max enchantments on a single card.
            int count = MultiEnchantmentApi.GetEnchantmentCount(card);
            if (count > _maxEnchantmentsOnCard)
            {
                _maxEnchantmentsOnCard = count;
            }
        }
        catch { /* telemetry must never crash the game */ }
    }

    /// <summary>
    /// Called from the AfterCardPlayed hook. Records a play event only if the card
    /// has at least one enchantment — avoids noise from unenchanted cards.
    /// </summary>
    internal static void NoteEnchantedCardPlayed(CardModel card)
    {
        if (!TelemetryConfig.IsEnabled) return;

        try
        {
            int enchantCount = MultiEnchantmentApi.GetEnchantmentCount(card);
            if (enchantCount <= 0) return;

            _enchantedCardPlays++;
            string cardId = card.Id.ToString();
            EnchantedCardPlayCounts[cardId] = EnchantedCardPlayCounts.GetValueOrDefault(cardId) + 1;
        }
        catch { /* telemetry must never crash the game */ }
    }

    internal static void NoteCardRewardSelection(
        IRunState? runState,
        object? player,
        object? reward,
        IReadOnlyList<string> offeredCardIds,
        IReadOnlyList<string> selectedCardIds,
        bool skipped,
        bool rerolled = false,
        bool alternativeUsed = false)
    {
        if (!TelemetryConfig.IsEnabled || _sessionId == null || offeredCardIds.Count == 0) return;

        try
        {
            EnsureRun(runState);

            int? floor = null;
            int? ascension = null;
            string? character = null;
            string? roomType = null;
            string? roomName = null;
            try { floor = runState?.TotalFloor; } catch { }
            try { ascension = runState?.AscensionLevel; } catch { }
            try { roomType = runState?.CurrentRoom?.RoomType.ToString(); } catch { }
            try { roomName = runState?.CurrentRoom?.ToString(); } catch { }
            var multiplayer = MergeMultiplayerContext(GetMultiplayerContext(runState, null));
            try
            {
                object? characterObj = GetPropertyOrFieldValue(player, "Character");
                object? id = GetPropertyOrFieldValue(characterObj, "Id");
                character = id?.ToString() ?? characterObj?.ToString();
                if (!string.IsNullOrWhiteSpace(character))
                {
                    _runCharacterName ??= character;
                }
            }
            catch { /* best-effort */ }

            TelemetryReporter.SendCardReward(new
            {
                session_id = _sessionId,
                installation_id = TelemetryConfig.InstallationId,
                run_id = _runId,
                run_instance_id = _runInstanceId,
                run_index = _runIndex > 0 ? _runIndex : (int?)null,
                run_seed = _runSeed,
                run_key = _runIdentityKey,
                game_version = TelemetryConfig.GameVersion,
                mod_version = TelemetryConfig.ModVersion,
                api_version = MultiEnchantmentApiVersion.Current,
                character = string.IsNullOrWhiteSpace(character) ? GetCharacterName(runState) : character,
                floor,
                ascension,
                room_type = roomType,
                room_name = roomName,
                is_multiplayer = multiplayer.IsMultiplayer,
                player_count = multiplayer.PlayerCount,
                reward_type = reward?.GetType().Name,
                offered_card_ids = offeredCardIds.ToList(),
                selected_card_ids = selectedCardIds.ToList(),
                offered_count = offeredCardIds.Count,
                offered_distinct_count = offeredCardIds.Distinct().Count(),
                selected_count = selectedCardIds.Count,
                selected_distinct_count = selectedCardIds.Distinct().Count(),
                skipped,
                rerolled,
                alternative_used = alternativeUsed,
            });
        }
        catch { /* telemetry must never crash the game */ }
    }

    internal static void NoteRunSaveMode(bool isMultiplayer)
    {
        if (!TelemetryConfig.IsEnabled) return;

        try
        {
            if (_runId == null)
            {
                _pendingRunIsMultiplayer = isMultiplayer;
                return;
            }

            MergeMultiplayerContext((isMultiplayer, null));
        }
        catch { /* telemetry must never crash the game */ }
    }

    internal static void NoteRunLoadedFromSave(bool isMultiplayer)
    {
        if (!TelemetryConfig.IsEnabled) return;

        try
        {
            _pendingRunIsMultiplayer = isMultiplayer;
            if (_runId != null)
            {
                MergeMultiplayerContext((isMultiplayer, null));
            }
        }
        catch { /* telemetry must never crash the game */ }
    }

    internal static void NoteEnchantRemoved() => _totalRemovals++;

    internal static void NoteEventBusPublish() => _eventBusPublishCount++;

    internal static void NoteDeserializationFailure(string enchantmentId, string cardId, Exception ex)
    {
        if (!TelemetryConfig.IsEnabled) return;

        _deserializationFailureCount++;
        DeserializationFailures.Add(new DeserializationFailureRecord
        {
            EnchantmentId = enchantmentId,
            CardId = cardId,
            Error = ex.GetType().Name + ": " + Truncate(ex.Message, 200),
        });
    }

    private static TelemetryHashCache ReadHashCache()
    {
        return _runtimeHashCache ??= new TelemetryHashCache();
    }

    private static void WriteHashCache(TelemetryHashCache cache)
    {
        _runtimeHashCache = cache;
    }

    // ── Data collection helpers ──────────────────────────────────────────

    private static List<object> CollectLoadedMods()
    {
        var snapshots = new Dictionary<string, ModSnapshot>(StringComparer.OrdinalIgnoreCase);
        List<ModManifestInfo> manifests = CollectInstalledModManifests();
        var manifestsById = manifests
            .Where(static m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(static m => m.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);
        var manifestsByDirectory = manifests
            .Where(static m => !string.IsNullOrWhiteSpace(m.Directory))
            .GroupBy(static m => m.Directory!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);
        ModSettingsSnapshot settings = CollectModSettingsSnapshot();
        string ourAsmName = typeof(TelemetryCollector).Assembly.GetName().Name ?? "";

        int runtimeCount = AddRuntimeModSnapshots(snapshots, manifestsById);
        AddAssemblyModSnapshots(snapshots, manifestsByDirectory, settings, ourAsmName);
        AddEnabledManifestSnapshots(snapshots, manifests, settings);

        DiagLog($"CollectLoadedMods: runtime={runtimeCount} loaded={snapshots.Values.Count(static m => m.Loaded)} manifests={manifests.Count} settings_found={settings.Found} total={snapshots.Count}");
        return StableSortForHash(snapshots.Values.Cast<object>());
    }

    private static int AddRuntimeModSnapshots(
        Dictionary<string, ModSnapshot> snapshots,
        IReadOnlyDictionary<string, ModManifestInfo> manifestsById)
    {
        int count = 0;
        try
        {
            int order = 0;
            foreach (Mod mod in ModManager.Mods)
            {
                ModManifest? runtimeManifest = mod.manifest;
                string? id = runtimeManifest?.id;
                bool loaded = mod.state == ModLoadState.Loaded;
                bool enabled = mod.state is not ModLoadState.Disabled and not ModLoadState.DisabledDuplicate;

                if (!enabled && !loaded)
                {
                    order++;
                    continue;
                }

                ModManifestInfo? diskManifest = null;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    manifestsById.TryGetValue(id, out diskManifest);
                }

                ModSnapshot snapshot = ModSnapshot.FromRuntimeMod(mod, diskManifest, order);
                string key = snapshot.Key;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    snapshots[key] = snapshot;
                    count++;
                }

                order++;
            }
        }
        catch (Exception ex)
        {
            DiagLog($"AddRuntimeModSnapshots FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        return count;
    }

    private static void AddAssemblyModSnapshots(
        Dictionary<string, ModSnapshot> snapshots,
        IReadOnlyDictionary<string, ModManifestInfo> manifestsByDirectory,
        ModSettingsSnapshot settings,
        string ourAsmName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!TryGetLoadedModSnapshot(asm, ourAsmName, manifestsByDirectory, settings, out ModSnapshot? snapshot) ||
                snapshot == null)
            {
                continue;
            }

            string key = snapshot.Key;
            if (!string.IsNullOrWhiteSpace(key) && !snapshots.ContainsKey(key))
            {
                snapshots[key] = snapshot;
            }
        }
    }

    private static void AddEnabledManifestSnapshots(
        Dictionary<string, ModSnapshot> snapshots,
        IReadOnlyList<ModManifestInfo> manifests,
        ModSettingsSnapshot settings)
    {
        foreach (ModManifestInfo manifest in manifests)
        {
            ModEnableInfo? enableInfo = settings.Find(manifest.Id, manifest.Directory);
            if (settings.Found &&
                (!settings.ModsEnabled || enableInfo?.IsEnabled != true))
            {
                continue;
            }

            if (!settings.Found && !ManifestDependsOnUs(manifest))
            {
                continue;
            }

            string key = manifest.Id ?? manifest.Directory ?? manifest.ManifestFileName;
            if (string.IsNullOrWhiteSpace(key) || snapshots.ContainsKey(key))
            {
                continue;
            }

            snapshots[key] = ModSnapshot.FromManifest(manifest, enableInfo);
        }
    }

    private static List<object> CollectApiCompatibilityResults()
    {
        var result = new List<object>();
        string ourAsmName = typeof(TelemetryCollector).Assembly.GetName().Name ?? "";

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!TryGetRelevantModAssembly(
                    asm,
                    ourAsmName,
                    out _,
                    out _,
                    out _))
            {
                continue;
            }

            try
            {
                AssemblyScanner.ApiCompatibilitySnapshot snapshot =
                    AssemblyScanner.GetApiCompatibilitySnapshot(asm);
                result.Add(new
                {
                    assembly = snapshot.Assembly,
                    declared_min = snapshot.DeclaredMin,
                    declared_max = snapshot.DeclaredMax,
                    runtime_api = snapshot.RuntimeApi,
                    status = snapshot.Status,
                });
            }
            catch { /* compatibility collection is best-effort */ }
        }

        return StableSortForHash(result);
    }

    private static List<object> CollectHarmonyConflicts()
    {
        var result = new List<object>();
        const string modId = "MultiEnchantmentMod";

        try
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                Patches? patchInfo = Harmony.GetPatchInfo(method);
                if (patchInfo == null) continue;

                var ourPatches = new List<object>();
                var otherPatches = new List<object>();

                CollectPatchDetails(patchInfo.Prefixes, "prefix", modId, ourPatches, otherPatches);
                CollectPatchDetails(patchInfo.Postfixes, "postfix", modId, ourPatches, otherPatches);
                CollectPatchDetails(patchInfo.Transpilers, "transpiler", modId, ourPatches, otherPatches);
                CollectPatchDetails(patchInfo.Finalizers, "finalizer", modId, ourPatches, otherPatches);

                if (ourPatches.Count > 0 && otherPatches.Count > 0)
                {
                    result.Add(new
                    {
                        method = $"{method.DeclaringType?.FullName}.{method.Name}",
                        our_patches = ourPatches,
                        other_patches = otherPatches,
                    });
                }
            }
        }
        catch { /* ignore */ }

        return result;
    }

    private static void CollectPatchDetails(
        IEnumerable<Patch> patches, string patchType, string ourModId,
        List<object> ourPatches, List<object> otherPatches)
    {
        foreach (Patch p in patches)
        {
            var detail = new
            {
                type = patchType,
                owner = p.owner ?? "unknown",
                priority = p.priority,
                method = p.PatchMethod?.Name ?? "unknown",
                before = p.before?.ToList() ?? new List<string>(),
                after = p.after?.ToList() ?? new List<string>(),
            };

            if (p.owner == ourModId)
                ourPatches.Add(detail);
            else
                otherPatches.Add(detail);
        }
    }

    private static List<object> CollectUnregisteredEnchantmentMods()
    {
        var result = new List<object>();
        var registeredTypes = EnchantmentRegistry.GetAllRegisteredTypes();

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(static t => t != null).ToArray()!; }
            catch { continue; }

            var unregistered = types
                .Where(t => IsThirdPartyEnchantmentType(t) && !registeredTypes.Contains(t))
                .Select(static t => t.Name)
                .ToList();

            if (unregistered.Count > 0)
            {
                result.Add(new
                {
                    assembly = asm.GetName().Name,
                    enchantment_types = unregistered,
                });
            }
        }

        return result;
    }

    private static List<object> CollectRegisteredEnchantmentTypes()
    {
        var result = new List<object>();
        foreach (Type type in EnchantmentRegistry.GetAllRegisteredTypes())
        {
            if (!IsThirdPartyEnchantmentType(type)) continue;

            string stackBehavior;
            try { stackBehavior = MultiEnchantmentStackSupport.GetBehavior(type).ToString(); }
            catch { stackBehavior = "unknown"; }

            int? maxInstances;
            try { maxInstances = EnchantmentRegistry.GetMaxInstances(type); }
            catch { maxInstances = null; }

            bool isPermanent;
            try { isPermanent = EnchantmentRegistry.IsPermanentScope(type); }
            catch { isPermanent = false; }

            bool hasLifecycle;
            try { hasLifecycle = EnchantmentRegistry.HasLifecycleHandlers(type); }
            catch { hasLifecycle = false; }

            bool hasDynVars;
            try { hasDynVars = EnchantmentRegistry.HasAnyDynamicVarContributions(type); }
            catch { hasDynVars = false; }

            result.Add(new
            {
                type = type.Name,
                @namespace = type.Namespace ?? "unknown",
                assembly = type.Assembly.GetName().Name,
                is_third_party = true,
                stack_behavior = stackBehavior,
                max_instances = maxInstances,
                is_permanent = isPermanent,
                has_lifecycle = hasLifecycle,
                has_dynamic_vars = hasDynVars,
            });
        }
        return result;
    }

    private static Dictionary<string, int> ComputeComboCounts(IEnumerable<EnchantApplicationRecord> applications)
    {
        var combos = new Dictionary<string, int>();
        var cardEnchantments = new List<(WeakReference<CardModel> Card, HashSet<string> Types)>();

        foreach (var app in applications)
        {
            if (app.Card == null || !app.Card.TryGetTarget(out CardModel? card)) continue;

            HashSet<string>? types = null;
            for (int i = 0; i < cardEnchantments.Count; i++)
            {
                if (cardEnchantments[i].Card.TryGetTarget(out CardModel? candidate) && ReferenceEquals(candidate, card))
                {
                    types = cardEnchantments[i].Types;
                    break;
                }
            }

            if (types == null)
            {
                types = new HashSet<string>();
                cardEnchantments.Add((new WeakReference<CardModel>(card), types));
            }
            types.Add(app.EnchantType);
        }

        foreach ((_, HashSet<string> types) in cardEnchantments)
        {
            if (types.Count < 2) continue;
            string combo = string.Join("+", types.OrderBy(static x => x));
            combos[combo] = combos.GetValueOrDefault(combo) + 1;
        }

        return combos;
    }

    // ── Mod catalog ───────────────────────────────────────────────────────

    private static object BuildModCatalogData(List<object> loadedMods, out string catalogHash)
    {
        var sortedMods = StableSortForHash(loadedMods);
        var enchantmentCatalog = StableSortForHash(CollectEnchantmentCatalog());
        catalogHash = ComputeStableHash(new
        {
            mods = sortedMods,
            enchantments = enchantmentCatalog,
        });

        return new
        {
            session_id = _sessionId,
            game_version = TelemetryConfig.GameVersion,
            mod_version = TelemetryConfig.ModVersion,
            locale = SafeGetLocale(),
            catalog_hash = catalogHash,
            mods = sortedMods,
            enchantments = enchantmentCatalog,
        };
    }

    internal static string MinimalCrashCatalogHash => ComputeStableHash(new
    {
        mods = Array.Empty<object>(),
        enchantments = Array.Empty<object>(),
    });

    internal static string MinimalCrashEnvironmentHash => ComputeStableHash(new
    {
        catalog_hash = MinimalCrashCatalogHash,
        harmony_conflicts = Array.Empty<object>(),
        api_compatibility_results = Array.Empty<object>(),
        unregistered_enchantment_mods = Array.Empty<object>(),
    });

    internal static object BuildMinimalCrashEnvironmentData(string environmentHash) => new
    {
        environment_hash = environmentHash,
        registered_enchantment_count = 0,
        registered_enchantment_types = Array.Empty<object>(),
        loaded_mod_assemblies = Array.Empty<object>(),
        harmony_conflicts = Array.Empty<object>(),
        harmony_conflict_count = 0,
        unregistered_enchantment_mods = Array.Empty<object>(),
        api_compatibility_results = Array.Empty<object>(),
    };

    internal static object BuildMinimalCrashCatalogData(string catalogHash) => new
    {
        session_id = _sessionId,
        game_version = TelemetryConfig.GameVersion,
        mod_version = TelemetryConfig.ModVersion,
        locale = "unknown",
        catalog_hash = catalogHash,
        mods = Array.Empty<object>(),
        enchantments = Array.Empty<object>(),
    };

    private static List<object> CollectEnchantmentCatalog()
    {
        var result = new List<object>();

        foreach (Type type in EnchantmentRegistry.GetAllRegisteredTypes())
        {
            try
            {
                if (!IsThirdPartyEnchantmentType(type)) continue;

                EnchantmentModel? canonical = null;
                try
                {
                    canonical = (EnchantmentModel?)typeof(ModelDb)
                        .GetMethod("Enchantment", Type.EmptyTypes)
                        ?.MakeGenericMethod(type)
                        .Invoke(null, null);
                }
                catch { /* some types may not have a canonical instance */ }

                if (canonical == null)
                {
                    try { canonical = (EnchantmentModel?)Activator.CreateInstance(type); }
                    catch { /* not all types support default construction */ }
                }

                string? title = null;
                string? description = null;
                string? extraCardText = null;
                if (canonical != null)
                {
                    title = ReadSafeFormattedOrRawText(canonical, "Title");
                    description = ReadPlainTextMember(canonical, "Description");
                    extraCardText = ReadPlainTextMember(canonical, "DynamicExtraCardText");
                }

                string stackBehavior;
                try { stackBehavior = MultiEnchantmentStackSupport.GetBehavior(type).ToString(); }
                catch { stackBehavior = "unknown"; }

                int? maxInstances;
                try { maxInstances = EnchantmentRegistry.GetMaxInstances(type); }
                catch { maxInstances = null; }

                bool isPermanent;
                try { isPermanent = EnchantmentRegistry.IsPermanentScope(type); }
                catch { isPermanent = false; }

                result.Add(new
                {
                    type_name = type.Name,
                    full_name = type.FullName,
                    assembly = type.Assembly.GetName().Name,
                    assembly_version = GetModVersion(type.Assembly),
                    is_third_party = true,
                    title = Truncate(title, 100),
                    description = Truncate(description, 500),
                    extra_card_text = Truncate(extraCardText, 300),

                    // Stacking behavior
                    stack_behavior = stackBehavior,
                    max_instances = maxInstances,
                    is_permanent = isPermanent,

                    // Framework integration
                    has_lifecycle = EnchantmentRegistry.HasLifecycleHandlers(type),
                    has_dynamic_vars = EnchantmentRegistry.HasAnyDynamicVarContributions(type),
                });
            }
            catch { /* skip this type if anything goes wrong */ }
        }

        return result;
    }

    private static bool IsFrameworkOrGameAssembly(string asmName, string ourAsmName)
    {
        return asmName.StartsWith("System", StringComparison.Ordinal) ||
               asmName.StartsWith("Microsoft", StringComparison.Ordinal) ||
               asmName.StartsWith("Godot", StringComparison.Ordinal) ||
               asmName.StartsWith("GodotPlugins", StringComparison.Ordinal) ||
               asmName.StartsWith("netstandard", StringComparison.Ordinal) ||
               asmName is "0Harmony" or "sts2" or "mscorlib" or "System.Private.CoreLib" ||
               asmName == ourAsmName;
    }

    // ── Game reference tables (wiki-style, one row per entity) ────────

    /// <summary>
    /// Scans all loaded assemblies for concrete <c>CardModel</c> subclasses,
    /// creates a canonical instance via <c>ModelDb.Card&lt;T&gt;()</c>, and reads
    /// localized title/description/type/rarity/cost.
    /// Column names match the <c>ref_cards</c> table for direct PostgREST upsert.
    /// </summary>
    private static ContentSourceInfo GetContentSourceInfo(Type type)
    {
        Assembly asm = type.Assembly;
        string asmName = asm.GetName().Name ?? "unknown";
        string ourAsmName = typeof(TelemetryCollector).Assembly.GetName().Name ?? "";
        bool isGameNamespace = type.Namespace?.StartsWith("MegaCrit.Sts2", StringComparison.Ordinal) ?? false;
        bool isThirdParty = !isGameNamespace && !IsFrameworkOrGameAssembly(asmName, ourAsmName);

        return new ContentSourceInfo
        {
            TypeName = type.Name,
            FullName = type.FullName ?? type.Name,
            Assembly = asmName,
            AssemblyVersion = GetModVersion(asm),
            IsThirdParty = isThirdParty,
        };
    }

    private static List<object> CollectCardCatalog()
    {
        var result = new List<object>();
        var seen = new HashSet<string>();
        string locale = SafeGetLocale();
        string gameVersion = TelemetryConfig.GameVersion;
        DateTimeOffset collectedAt = DateTimeOffset.UtcNow;

        // Try multiple approaches to find ModelDb.Card<T>()
        MethodInfo? modelDbCard = null;
        try
        {
            modelDbCard = typeof(ModelDb).GetMethod("Card", Type.EmptyTypes);
        }
        catch (AmbiguousMatchException)
        {
            // Multiple overloads — find the generic one explicitly
            modelDbCard = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Card" && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 0);
        }
        DiagLog($"CollectCardCatalog: ModelDb.Card method found={modelDbCard != null}");

        int cardTypeCount = 0;
        int failCount = 0;

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(static t => t != null).ToArray()!; }
            catch { continue; }

            foreach (Type type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) continue;
                if (!typeof(CardModel).IsAssignableFrom(type)) continue;

                cardTypeCount++;
                string typeKey = type.FullName ?? type.Name;
                if (!seen.Add(typeKey)) continue;

                try
                {
                    CardModel? card = null;
                    if (modelDbCard != null)
                    {
                        try { card = (CardModel?)modelDbCard.MakeGenericMethod(type).Invoke(null, null); }
                        catch (Exception ex)
                        {
                            if (failCount < 3) DiagLog($"  ModelDb.Card<{type.Name}> failed: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                    if (card == null)
                    {
                        try { card = (CardModel?)Activator.CreateInstance(type); }
                        catch (Exception ex)
                        {
                            if (failCount < 3) DiagLog($"  Activator.CreateInstance({type.Name}) failed: {ex.InnerException?.Message ?? ex.Message}");
                            failCount++;
                            continue;
                        }
                    }
                    if (card == null) { failCount++; continue; }

                    ContentSourceInfo source = GetContentSourceInfo(type);
                    string? title = ReadPlainTextMember(card, "Title");
                    string? description = Truncate(ReadPlainTextMember(card, "Description"), 300);
                    int energyCost = ReadCatalogEnergyCost(card, type);

                    result.Add(new
                    {
                        card_id = card.Id.ToString(),
                        title,
                        description,
                        card_type = card.Type.ToString(),
                        rarity = card.Rarity.ToString(),
                        energy_cost = energyCost,
                        type_name = source.TypeName,
                        full_name = source.FullName,
                        assembly = source.Assembly,
                        assembly_version = source.AssemblyVersion,
                        is_third_party = source.IsThirdParty,
                        game_version = gameVersion,
                        locale,
                        updated_at = collectedAt,
                    });
                }
                catch (Exception ex)
                {
                    if (failCount < 3) DiagLog($"  Card {type.Name} outer catch: {ex.Message}");
                    failCount++;
                }
            }
        }
        List<object> deduped = DeduplicateRowsByKeys(result, "card_id", "locale");
        DiagLog($"CollectCardCatalog done: types={cardTypeCount} success={result.Count} deduped={deduped.Count} fail={failCount}");
        return deduped;
    }

    /// <summary>
    /// Scans for concrete <c>RelicModel</c> subclasses and reads localized metadata.
    /// Column names match the <c>ref_relics</c> table for direct PostgREST upsert.
    /// </summary>
    private static List<object> CollectRelicCatalog()
    {
        var result = new List<object>();
        var seen = new HashSet<string>();
        string locale = SafeGetLocale();
        string gameVersion = TelemetryConfig.GameVersion;
        DateTimeOffset collectedAt = DateTimeOffset.UtcNow;
        // Use compile-time reference — runtime string search fails because
        // sts2.dll throws ReflectionTypeLoadException and loses loadable types.
        Type relicBaseType = typeof(RelicModel);
        DiagLog($"CollectRelicCatalog: relicBaseType={relicBaseType.FullName}");

        MethodInfo? modelDbRelic = null;
        try { modelDbRelic = typeof(ModelDb).GetMethod("Relic", Type.EmptyTypes); }
        catch (AmbiguousMatchException)
        {
            modelDbRelic = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Relic" && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 0);
        }
        DiagLog($"CollectRelicCatalog: ModelDb.Relic method found={modelDbRelic != null}");

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(static t => t != null).ToArray()!; }
            catch { continue; }

            foreach (Type type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) continue;
                if (!relicBaseType.IsAssignableFrom(type)) continue;

                string typeKey = type.FullName ?? type.Name;
                if (!seen.Add(typeKey)) continue;

                try
                {
                    object? relic = null;
                    if (modelDbRelic != null)
                    {
                        try { relic = modelDbRelic.MakeGenericMethod(type).Invoke(null, null); }
                        catch { }
                    }
                    if (relic == null)
                    {
                        try { relic = Activator.CreateInstance(type); }
                        catch { continue; }
                    }
                    if (relic == null) continue;

                    string? id = GetPropertyOrFieldValue(relic, "Id")?.ToString();
                    string? title = ReadSafeFormattedOrRawText(relic, "Title");
                    string? description = Truncate(
                        ReadPlainTextMember(relic, "DynamicDescription", "Description", "EventDescription"),
                        300);
                    string? rarity = GetPropertyOrFieldValue(relic, "Rarity")?.ToString();

                    if (string.IsNullOrEmpty(id)) continue;
                    ContentSourceInfo source = GetContentSourceInfo(type);

                    result.Add(new
                    {
                        relic_id = id,
                        title,
                        description,
                        rarity,
                        type_name = source.TypeName,
                        full_name = source.FullName,
                        assembly = source.Assembly,
                        assembly_version = source.AssemblyVersion,
                        is_third_party = source.IsThirdParty,
                        game_version = gameVersion,
                        locale,
                        updated_at = collectedAt,
                    });
                }
                catch { /* skip */ }
            }
        }
        return DeduplicateRowsByKeys(result, "relic_id", "locale");
    }

    /// <summary>
    /// Scans for concrete power/buff model types. STS2 powers may be
    /// <c>PowerModel</c>, <c>BuffModel</c>, or similar. Best-effort via reflection.
    /// Column names match the <c>ref_powers</c> table for direct PostgREST upsert.
    /// </summary>
    private static List<object> CollectPowerCatalog()
    {
        var result = new List<object>();
        var seen = new HashSet<string>();
        string locale = SafeGetLocale();
        string gameVersion = TelemetryConfig.GameVersion;
        DateTimeOffset collectedAt = DateTimeOffset.UtcNow;

        // Use compile-time reference — PowerModel is in MegaCrit.Sts2.Core.Models
        Type powerBaseType = typeof(PowerModel);
        DiagLog($"CollectPowerCatalog: powerBaseType={powerBaseType.FullName}");

        MethodInfo? modelDbPower = null;
        try { modelDbPower = typeof(ModelDb).GetMethod("Power", Type.EmptyTypes); }
        catch (AmbiguousMatchException)
        {
            modelDbPower = typeof(ModelDb).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Power" && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 0);
        }
        DiagLog($"CollectPowerCatalog: ModelDb.Power method found={modelDbPower != null}");

        int powerTypeCount = 0;
        int failCount = 0;

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(static t => t != null).ToArray()!; }
            catch { continue; }

            foreach (Type type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) continue;
                if (!powerBaseType.IsAssignableFrom(type)) continue;

                powerTypeCount++;
                string typeKey = type.FullName ?? type.Name;
                if (!seen.Add(typeKey)) continue;

                try
                {
                    PowerModel? power = null;
                    if (modelDbPower != null)
                    {
                        try { power = (PowerModel?)modelDbPower.MakeGenericMethod(type).Invoke(null, null); }
                        catch (Exception ex)
                        {
                            if (failCount < 3) DiagLog($"  ModelDb.Power<{type.Name}> failed: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                    if (power == null)
                    {
                        try { power = (PowerModel?)Activator.CreateInstance(type); }
                        catch (Exception ex)
                        {
                            if (failCount < 3) DiagLog($"  Activator.CreateInstance({type.Name}) failed: {ex.InnerException?.Message ?? ex.Message}");
                            failCount++;
                            continue;
                        }
                    }
                    if (power == null) { failCount++; continue; }

                    string? id = power.Id.ToString();
                    string? title = ReadSafeFormattedOrRawText(power, "Title");
                    string? description = Truncate(
                        ReadPowerDescription(power),
                        300);

                    if (string.IsNullOrEmpty(id)) id = type.Name;
                    ContentSourceInfo source = GetContentSourceInfo(type);

                    result.Add(new
                    {
                        power_id = id,
                        title,
                        description,
                        type_name = source.TypeName,
                        full_name = source.FullName,
                        assembly = source.Assembly,
                        assembly_version = source.AssemblyVersion,
                        is_third_party = source.IsThirdParty,
                        power_type = power.Type.ToString(),
                        stack_type = power.StackType.ToString(),
                        instance_type = power.InstanceType.ToString(),
                        game_version = gameVersion,
                        locale,
                        updated_at = collectedAt,
                    });
                }
                catch (Exception ex)
                {
                    if (failCount < 3) DiagLog($"  Power {type.Name} outer catch: {ex.Message}");
                    failCount++;
                }
            }
        }
        List<object> deduped = DeduplicateRowsByKeys(result, "power_id", "locale");
        DiagLog($"CollectPowerCatalog done: types={powerTypeCount} success={result.Count} deduped={deduped.Count} fail={failCount}");
        return deduped;
    }

    private static bool ContainsThirdPartyEnchantmentTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes().Any(IsThirdPartyEnchantmentType);
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t != null).Any(t => IsThirdPartyEnchantmentType(t!));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLoadedModSnapshot(
        Assembly asm,
        string ourAsmName,
        IReadOnlyDictionary<string, ModManifestInfo> manifestsByDirectory,
        ModSettingsSnapshot settings,
        out ModSnapshot? snapshot)
    {
        snapshot = null;
        string asmName = asm.GetName().Name ?? "";
        if (string.IsNullOrWhiteSpace(asmName))
        {
            return false;
        }

        bool isOurAssembly = string.Equals(asmName, ourAsmName, StringComparison.OrdinalIgnoreCase);
        if (!isOurAssembly && IsFrameworkOrGameAssembly(asmName, ourAsmName))
        {
            return false;
        }

        bool referencesUs = false;
        try
        {
            referencesUs = asm.GetReferencedAssemblies()
                .Any(r => string.Equals(r.Name, ourAsmName, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* ignore reflection failures */ }

        bool hasThirdPartyEnchantments = ContainsThirdPartyEnchantmentTypes(asm);
        ModManifestInfo? manifest = TryFindManifestForAssembly(asm, manifestsByDirectory);
        ModEnableInfo? enableInfo = settings.Find(manifest?.Id ?? asmName, manifest?.Directory);

        if (!isOurAssembly &&
            manifest == null &&
            !referencesUs &&
            !hasThirdPartyEnchantments &&
            !IsAssemblyUnderModsRoot(asm))
        {
            return false;
        }

        snapshot = ModSnapshot.FromAssembly(
            asm,
            manifest,
            enableInfo,
            referencesUs,
            hasThirdPartyEnchantments);
        return true;
    }

    private static ModManifestInfo? TryFindManifestForAssembly(
        Assembly asm,
        IReadOnlyDictionary<string, ModManifestInfo> manifestsByDirectory)
    {
        try
        {
            string? location = asm.Location;
            if (string.IsNullOrWhiteSpace(location)) return null;

            string? directory = GetTopLevelModDirectoryName(location);
            if (!string.IsNullOrWhiteSpace(directory) &&
                manifestsByDirectory.TryGetValue(directory, out ModManifestInfo? byDirectory))
            {
                return byDirectory;
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    private static bool IsAssemblyUnderModsRoot(Assembly asm)
    {
        try
        {
            string? location = asm.Location;
            return !string.IsNullOrWhiteSpace(location) &&
                   GetTopLevelModDirectoryName(location) != null;
        }
        catch
        {
            return false;
        }
    }

    private static ModSettingsSnapshot CollectModSettingsSnapshot()
    {
        try
        {
            ModSettingsSnapshot? fromRuntime = TryCollectRuntimeModSettingsSnapshot();
            if (fromRuntime != null)
            {
                return fromRuntime;
            }

            string? settingsPath = FindLatestSettingsSavePath();
            if (!string.IsNullOrWhiteSpace(settingsPath))
            {
                ModSettingsSnapshot? fromFile = TryReadModSettingsSnapshot(settingsPath);
                if (fromFile != null)
                {
                    return fromFile;
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog($"CollectModSettingsSnapshot FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        return ModSettingsSnapshot.NotFound;
    }

    private static ModSettingsSnapshot? TryCollectRuntimeModSettingsSnapshot()
    {
        try
        {
            var mods = ModManager.Mods;
            if (mods == null || mods.Count == 0)
            {
                return null;
            }

            var byKey = new Dictionary<string, ModEnableInfo>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (Mod mod in mods)
            {
                string? id = mod.manifest?.id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    order++;
                    continue;
                }

                bool enabled = mod.state is not ModLoadState.Disabled and not ModLoadState.DisabledDuplicate;
                var info = new ModEnableInfo
                {
                    Id = id,
                    IsEnabled = enabled,
                    Source = mod.modSource.ToString(),
                    LoadOrder = order,
                };
                AddModEnableInfo(byKey, info, mod.path);
                order++;
            }

            return byKey.Count == 0
                ? null
                : new ModSettingsSnapshot
                {
                    Found = true,
                    ModsEnabled = true,
                    ModsByKey = byKey,
                };
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLatestSettingsSavePath()
    {
        try
        {
            string? appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                return null;
            }

            string root = Path.Combine(appData, "SlayTheSpire2");
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.EnumerateFiles(root, "settings.save", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static ModSettingsSnapshot? TryReadModSettingsSnapshot(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("mod_settings", out JsonElement modSettings) ||
                modSettings.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool modsEnabled = ReadJsonBool(modSettings, "mods_enabled") ?? true;
            var byKey = new Dictionary<string, ModEnableInfo>(StringComparer.OrdinalIgnoreCase);
            if (modSettings.TryGetProperty("mod_list", out JsonElement modList) &&
                modList.ValueKind == JsonValueKind.Array)
            {
                int order = 0;
                foreach (JsonElement entry in modList.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        order++;
                        continue;
                    }

                    string? id = ReadJsonString(entry, "id");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        order++;
                        continue;
                    }

                    var info = new ModEnableInfo
                    {
                        Id = id,
                        IsEnabled = modsEnabled && (ReadJsonBool(entry, "is_enabled") ?? true),
                        Source = ReadJsonString(entry, "source"),
                        LoadOrder = order,
                    };
                    AddModEnableInfo(byKey, info, null);
                    order++;
                }
            }

            return new ModSettingsSnapshot
            {
                Found = true,
                ModsEnabled = modsEnabled,
                ModsByKey = byKey,
            };
        }
        catch (Exception ex)
        {
            DiagLog($"TryReadModSettingsSnapshot FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void AddModEnableInfo(
        IDictionary<string, ModEnableInfo> map,
        ModEnableInfo info,
        string? path)
    {
        if (!string.IsNullOrWhiteSpace(info.Id))
        {
            map[info.Id] = info;
        }

        string? directory = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                directory = Path.GetFileName(path.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            }
            catch { /* best-effort */ }
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            map[directory] = info;
        }
    }

    private static List<ModManifestInfo> CollectInstalledModManifests()
    {
        var result = new List<ModManifestInfo>();
        string? modsRoot = FindModsRoot();
        if (string.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
        {
            DiagLog("CollectInstalledModManifests: mods root not found");
            return result;
        }

        try
        {
            foreach (string modDirectory in Directory.EnumerateDirectories(modsRoot))
            {
                ModManifestInfo? manifest = TryReadManifestFromModDirectory(modDirectory);
                if (manifest != null)
                {
                    result.Add(manifest);
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog($"CollectInstalledModManifests FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    private static ModManifestInfo? TryReadManifestFromModDirectory(string modDirectory)
    {
        try
        {
            if (!Directory.Exists(modDirectory)) return null;

            string directoryName = Path.GetFileName(modDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

            IEnumerable<string> candidates = new[]
                {
                    Path.Combine(modDirectory, $"{directoryName}.json"),
                    Path.Combine(modDirectory, "mod_manifest.json"),
                }
                .Concat(Directory.EnumerateFiles(modDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(static p => !Path.GetFileName(p).EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)));

            foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ModManifestInfo? manifest = TryReadModManifestInfo(path, directoryName);
                if (manifest != null)
                {
                    return manifest;
                }
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    private static ModManifestInfo? TryReadModManifestInfo(string path, string directoryName)
    {
        try
        {
            if (!File.Exists(path)) return null;

            string text = File.ReadAllText(path, Encoding.UTF8);
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !LooksLikeModManifest(root))
                {
                    return null;
                }

                string? id = ReadJsonString(root, "id");
                string? name = ReadJsonString(root, "name");
                string? version = ReadJsonString(root, "version");
                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(version))
                {
                    return null;
                }

                return new ModManifestInfo
                {
                    Id = string.IsNullOrWhiteSpace(id) ? Path.GetFileNameWithoutExtension(path) : id,
                    DisplayName = name,
                    Version = version,
                    Directory = directoryName,
                    ManifestFileName = Path.GetFileName(path),
                    HasDll = ReadJsonBool(root, "has_dll") ?? ReadJsonBool(root, "hasDll"),
                    HasPck = ReadJsonBool(root, "has_pck") ?? ReadJsonBool(root, "hasPck"),
                    AffectsGameplay = ReadJsonBool(root, "affects_gameplay") ?? ReadJsonBool(root, "affectsGameplay"),
                    Dependencies = ReadManifestDependencies(root),
                };
            }
            catch (JsonException)
            {
                return TryReadLooseModManifestInfo(text, path, directoryName);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeModManifest(JsonElement root)
    {
        return root.TryGetProperty("id", out _) ||
               root.TryGetProperty("has_dll", out _) ||
               root.TryGetProperty("hasDll", out _) ||
               root.TryGetProperty("has_pck", out _) ||
               root.TryGetProperty("hasPck", out _) ||
               root.TryGetProperty("affects_gameplay", out _) ||
               root.TryGetProperty("affectsGameplay", out _);
    }

    private static ModManifestInfo? TryReadLooseModManifestInfo(string text, string path, string directoryName)
    {
        if (!Regex.IsMatch(text, "\"(id|has_dll|hasDll|has_pck|hasPck|affects_gameplay|affectsGameplay)\"\\s*:",
                RegexOptions.IgnoreCase))
        {
            return null;
        }

        string? id = ReadLooseJsonString(text, "id");
        string? version = ReadLooseJsonString(text, "version");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return new ModManifestInfo
        {
            Id = string.IsNullOrWhiteSpace(id) ? Path.GetFileNameWithoutExtension(path) : id,
            DisplayName = ReadLooseJsonString(text, "name"),
            Version = version,
            Directory = directoryName,
            ManifestFileName = Path.GetFileName(path),
            HasDll = ReadLooseJsonBool(text, "has_dll") ?? ReadLooseJsonBool(text, "hasDll"),
            HasPck = ReadLooseJsonBool(text, "has_pck") ?? ReadLooseJsonBool(text, "hasPck"),
            AffectsGameplay = ReadLooseJsonBool(text, "affects_gameplay") ?? ReadLooseJsonBool(text, "affectsGameplay"),
        };
    }

    private static string? ReadJsonString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    private static bool? ReadJsonBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => null,
        };
    }

    private static List<object>? ReadManifestDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out JsonElement dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<object>();
        foreach (JsonElement dependency in dependencies.EnumerateArray().Take(50))
        {
            switch (dependency.ValueKind)
            {
                case JsonValueKind.String:
                    string? id = dependency.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result.Add(new { id });
                    }
                    break;
                case JsonValueKind.Object:
                    string? objectId = ReadJsonString(dependency, "id");
                    if (!string.IsNullOrWhiteSpace(objectId))
                    {
                        result.Add(new
                        {
                            id = objectId,
                            version = ReadJsonString(dependency, "version"),
                            min_version = ReadJsonString(dependency, "min_version"),
                            max_version = ReadJsonString(dependency, "max_version"),
                        });
                    }
                    break;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool ManifestDependsOnUs(ModManifestInfo manifest)
    {
        string ourAsmName = typeof(TelemetryCollector).Assembly.GetName().Name ?? "";
        if (string.IsNullOrWhiteSpace(ourAsmName) || manifest.Dependencies == null)
        {
            return false;
        }

        foreach (object dependency in manifest.Dependencies)
        {
            string? id = GetPropertyOrFieldValue(dependency, "id")?.ToString();
            if (string.Equals(id, ourAsmName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RuntimeManifestDependsOnUs(ModManifest? manifest)
    {
        string ourAsmName = typeof(TelemetryCollector).Assembly.GetName().Name ?? "";
        if (string.IsNullOrWhiteSpace(ourAsmName) || manifest?.dependencies == null)
        {
            return false;
        }

        foreach (ModDependency dependency in manifest.dependencies)
        {
            if (string.Equals(dependency.id, ourAsmName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<object>? ReadRuntimeManifestDependencies(ModManifest? manifest)
    {
        if (manifest?.dependencies == null || manifest.dependencies.Count == 0)
        {
            return null;
        }

        var result = new List<object>();
        foreach (ModDependency dependency in manifest.dependencies.Take(50))
        {
            if (!string.IsNullOrWhiteSpace(dependency.id))
            {
                result.Add(new
                {
                    id = dependency.id,
                    min_version = dependency.minVersion,
                });
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static string? ReadLooseJsonString(string text, string propertyName)
    {
        Match match = Regex.Match(
            text,
            $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"(?<value>[^\"]*)\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static bool? ReadLooseJsonBool(string text, string propertyName)
    {
        Match match = Regex.Match(
            text,
            $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*(?<value>true|false)",
            RegexOptions.IgnoreCase);
        return match.Success && bool.TryParse(match.Groups["value"].Value, out bool parsed)
            ? parsed
            : null;
    }

    private static string? FindModsRoot()
    {
        string? dir = Path.GetDirectoryName(typeof(TelemetryCollector).Assembly.Location);
        string? current = dir;
        for (int depth = 0; depth <= 8 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (string.Equals(Path.GetFileName(current), "mods", StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            string childMods = Path.Combine(current, "mods");
            if (Directory.Exists(childMods))
            {
                return childMods;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        string? parent = Directory.GetParent(dir ?? "")?.FullName;
        return !string.IsNullOrWhiteSpace(parent) &&
               string.Equals(Path.GetFileName(parent), "mods", StringComparison.OrdinalIgnoreCase)
            ? parent
            : null;
    }

    private static string? GetTopLevelModDirectoryName(string path)
    {
        string? modsRoot = FindModsRoot();
        if (string.IsNullOrWhiteSpace(modsRoot)) return null;

        string fullRoot = Path.GetFullPath(modsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relative = Path.GetRelativePath(fullRoot, fullPath);
        string? directory = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    private static bool IsThirdPartyEnchantmentType(Type type)
    {
        return !type.IsAbstract &&
               !type.IsInterface &&
               typeof(EnchantmentModel).IsAssignableFrom(type) &&
               !(type.Namespace?.StartsWith("MegaCrit.Sts2", StringComparison.Ordinal) ?? false);
    }

    private static string GetModVersion(Assembly asm)
    {
        string asmName = asm.GetName().Name ?? "";
        if (string.Equals(asmName, "sts2", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(TelemetryConfig.GameVersion) &&
            !string.Equals(TelemetryConfig.GameVersion, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return TelemetryConfig.GameVersion;
        }

        string? manifestVersion = TryGetManifestVersion(asm);
        if (!string.IsNullOrWhiteSpace(manifestVersion))
        {
            return manifestVersion;
        }

        string? infoVersion = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        string? fileVersion = asm
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        return asm.GetName().Version?.ToString() ?? "unknown";
    }

    private static string? TryGetManifestVersion(Assembly asm)
    {
        try
        {
            string? asmPath = asm.Location;
            if (string.IsNullOrWhiteSpace(asmPath)) return null;

            string? dir = Path.GetDirectoryName(asmPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            string asmName = asm.GetName().Name ?? "";
            var candidates = new List<string>
            {
                Path.Combine(dir, $"{asmName}.json"),
                Path.Combine(dir, "mod_manifest.json"),
            };

            candidates.AddRange(Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                .Where(static p => !Path.GetFileName(p).EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)));

            foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? version = TryReadManifestVersion(path, asmName);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    private static string? TryReadManifestVersion(string path, string asmName)
    {
        try
        {
            if (!File.Exists(path)) return null;

            string text = File.ReadAllText(path, Encoding.UTF8);
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("version", out JsonElement versionEl)) return null;

                bool looksLikeModManifest =
                    root.TryGetProperty("id", out JsonElement idEl) ||
                    root.TryGetProperty("name", out _) ||
                    root.TryGetProperty("has_dll", out _) ||
                    root.TryGetProperty("has_pck", out _);
                if (!looksLikeModManifest) return null;

                if (idEl.ValueKind == JsonValueKind.String)
                {
                    string? id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id) &&
                        !string.Equals(id, asmName, StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileNameWithoutExtension(path).Equals(asmName, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }

                return versionEl.GetString();
            }
            catch (JsonException)
            {
                return TryReadLooseManifestVersion(text, path, asmName);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadLooseManifestVersion(string text, string path, string asmName)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        bool isLikelyManifest =
            string.Equals(fileName, asmName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "mod_manifest", StringComparison.OrdinalIgnoreCase);
        if (!isLikelyManifest) return null;

        if (!Regex.IsMatch(text, "\"(id|name|has_dll|has_pck)\"\\s*:", RegexOptions.IgnoreCase))
        {
            return null;
        }

        Match match = Regex.Match(text, "\"version\"\\s*:\\s*\"(?<version>[^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static string SafeGetLocale()
    {
        try
        {
            // Godot overrides .NET culture, so CultureInfo often returns empty.
            // Try Godot's TranslationServer first, then fall back to .NET.
            string? godotLocale = null;
            try
            {
                Type? translationServer = Type.GetType("Godot.TranslationServer, GodotSharp");
                godotLocale = translationServer
                    ?.GetMethod("GetLocale", Type.EmptyTypes)
                    ?.Invoke(null, null)?.ToString();
            }
            catch { /* Godot API not available */ }

            if (!string.IsNullOrEmpty(godotLocale)) return godotLocale;

            string uiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (!string.IsNullOrEmpty(uiCulture)) return uiCulture;

            string culture = System.Globalization.CultureInfo.CurrentCulture.Name;
            if (!string.IsNullOrEmpty(culture)) return culture;

            return "unknown";
        }
        catch { return "unknown"; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string SafeGetTitle(CardModel card)
    {
        try { return card.Title ?? card.Id.ToString(); }
        catch { return card.Id.ToString(); }
    }

    private static string SafeGetEnchantTitle(EnchantmentModel enchantment)
    {
        return ReadSafeFormattedOrRawText(enchantment, "Title") ?? enchantment.GetType().Name;
    }

    private static bool TryGetRelevantModAssembly(
        Assembly asm,
        string ourAsmName,
        out string asmName,
        out bool referencesUs,
        out bool hasThirdPartyEnchantments)
    {
        asmName = asm.GetName().Name ?? "";
        referencesUs = false;
        hasThirdPartyEnchantments = false;

        if (string.IsNullOrWhiteSpace(asmName) ||
            IsFrameworkOrGameAssembly(asmName, ourAsmName))
        {
            return false;
        }

        try
        {
            referencesUs = asm.GetReferencedAssemblies()
                .Any(r => string.Equals(r.Name, ourAsmName, StringComparison.OrdinalIgnoreCase));
        }
        catch { /* ignore reflection failures */ }

        hasThirdPartyEnchantments = ContainsThirdPartyEnchantmentTypes(asm);
        return referencesUs || hasThirdPartyEnchantments;
    }

    private static string? GetRunSeed(IRunState runState)
    {
        object? rng = GetPropertyOrFieldValue(runState, "Rng");
        object? value = GetPropertyOrFieldValue(rng, "StringSeed")
            ?? GetPropertyOrFieldValue(rng, "Seed");
        return value?.ToString();
    }

    private static bool IsEarlyRunFloor(IRunState runState)
    {
        try
        {
            return runState.TotalFloor <= 1;
        }
        catch
        {
            return false;
        }
    }

    private static (bool? IsMultiplayer, int? PlayerCount) GetMultiplayerContext(
        IRunState? runState,
        ICombatState? combatState)
    {
        int? playerCount = TryGetPlayerCount(combatState?.Players)
                           ?? TryGetPlayerCount(GetPropertyOrFieldValue(runState, "Players"));
        bool? isMultiplayer = TryGetMultiplayerFlag(combatState)
                              ?? TryGetMultiplayerFlag(runState);

        if (playerCount > 1)
        {
            isMultiplayer = true;
        }

        return (isMultiplayer, playerCount);
    }

    private static (bool? IsMultiplayer, int? PlayerCount) MergeMultiplayerContext(
        (bool? IsMultiplayer, int? PlayerCount) context)
    {
        if (context.IsMultiplayer == true)
        {
            _runIsMultiplayer = true;
        }
        else if (!_runIsMultiplayer.HasValue && context.IsMultiplayer == false)
        {
            _runIsMultiplayer = false;
        }

        if (context.PlayerCount.HasValue &&
            (!_runPlayerCount.HasValue || context.PlayerCount.Value > _runPlayerCount.Value))
        {
            _runPlayerCount = context.PlayerCount.Value;
        }

        if (_runPlayerCount > 1)
        {
            _runIsMultiplayer = true;
        }

        return (_runIsMultiplayer, _runPlayerCount);
    }

    private static bool? TryGetMultiplayerFlag(object? source)
    {
        foreach (string memberName in new[]
                 {
                     "IsMultiplayer",
                     "IsMultiplayerRun",
                     "IsOnline",
                     "IsOnlineRun",
                     "Multiplayer",
                 })
        {
            object? value = GetPropertyOrFieldValue(source, memberName);
            if (value is bool b)
            {
                return b;
            }
        }

        return null;
    }

    private static int? TryGetPlayerCount(object? players)
    {
        if (players == null)
        {
            return null;
        }

        try
        {
            object? countValue = GetPropertyOrFieldValue(players, "Count")
                                 ?? GetPropertyOrFieldValue(players, "Length");
            if (countValue is int reflectedCount && reflectedCount >= 0)
            {
                return reflectedCount;
            }

            if (players is ICollection collection)
            {
                return collection.Count;
            }

            if (players is IEnumerable enumerable)
            {
                int count = 0;
                foreach (object _ in enumerable)
                {
                    count++;
                }

                return count;
            }
        }
        catch { /* best-effort */ }

        return null;
    }

    private static string BuildRunIdentityKey(IRunState runState)
    {
        string seed = GetRunSeed(runState) ?? "unknown-seed";
        string character = TryGetCharacterNameFromRunState(runState) ?? "unknown-character";
        int? ascension = null;
        try { ascension = runState.AscensionLevel; } catch { }

        object? manager = null;
        try { manager = RunManager.Instance; } catch { }
        object? startTime = GetPropertyOrFieldValue(manager, "_startTime");
        object? dailyTime = GetPropertyOrFieldValue(manager, "DailyTime");

        string rawKey = string.Join('|', new[]
        {
            TelemetryConfig.InstallationId,
            seed,
            character,
            ascension?.ToString() ?? "unknown-ascension",
            startTime?.ToString() ?? dailyTime?.ToString() ?? "unknown-start",
        });

        return ComputeStableHash(rawKey);
    }

    private static string? GetCharacterName(IRunState? runState) =>
        _runCharacterName ?? TryGetCharacterNameFromRunState(runState);

    private static string? TryGetCharacterNameFromRunState(IRunState? runState)
    {
        object? players = GetPropertyOrFieldValue(runState, "Players");
        if (players is IEnumerable enumerable)
        {
            foreach (object player in enumerable)
            {
                object? character = GetPropertyOrFieldValue(player, "Character");
                object? id = GetPropertyOrFieldValue(character, "Id");
                string? name = id?.ToString() ?? character?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        object? localPlayer = GetPropertyOrFieldValue(runState, "Player")
            ?? GetPropertyOrFieldValue(runState, "LocalPlayer");
        object? localCharacter = GetPropertyOrFieldValue(localPlayer, "Character");
        object? localId = GetPropertyOrFieldValue(localCharacter, "Id");
        return localId?.ToString() ?? localCharacter?.ToString();
    }

    // ── Game state snapshot helpers ─────────────────────────────────────

    /// <summary>
    /// Collects deck card IDs from the first active player's deck.
    /// Returns card IDs only (e.g. ["CARD.STRIKE", "CARD.DEFEND", ...]).
    /// </summary>
    private static List<string>? CollectDeckCardIds(ICombatState? combatState)
    {
        if (combatState == null) return null;

        foreach (var player in combatState.Players)
        {
            // player.Deck.Cards gives the persistent deck (not combat copies)
            object? deck = GetPropertyOrFieldValue(player, "Deck");
            object? cards = GetPropertyOrFieldValue(deck, "Cards");
            if (cards is IEnumerable<CardModel> cardList)
            {
                return cardList
                    .Where(static c => c != null)
                    .Select(static c => c.Id.ToString())
                    .ToList();
            }

            // Fallback: combat cards
            object? pcs = GetPropertyOrFieldValue(player, "PlayerCombatState");
            object? allCards = GetPropertyOrFieldValue(pcs, "AllCards");
            if (allCards is IEnumerable<CardModel> combatCards)
            {
                return combatCards
                    .Where(static c => c != null && !c.HasBeenRemovedFromState)
                    .Select(static c => c.Id.ToString())
                    .Distinct()
                    .ToList();
            }
            break; // first player only
        }
        return null;
    }

    /// <summary>
    /// Collects relic IDs from the first active player via reflection.
    /// STS2 Player may expose Relics, OwnedRelics, or a similar property.
    /// </summary>
    private static List<string>? CollectRelicIds(ICombatState? combatState)
    {
        if (combatState == null) return null;

        foreach (var player in combatState.Players)
        {
            // Try common relic property names
            foreach (string propName in new[] { "Relics", "OwnedRelics", "RelicModels" })
            {
                object? relics = GetPropertyOrFieldValue(player, propName);
                if (relics is System.Collections.IEnumerable enumerable)
                {
                    var ids = new List<string>();
                    foreach (object relic in enumerable)
                    {
                        object? id = GetPropertyOrFieldValue(relic, "Id");
                        string? idStr = id?.ToString();
                        if (!string.IsNullOrEmpty(idStr))
                        {
                            ids.Add(idStr);
                        }
                    }
                    if (ids.Count > 0) return ids;
                }
            }

            // Fallback: try RunState.Relics
            object? runState = GetPropertyOrFieldValue(player, "RunState");
            foreach (string propName in new[] { "Relics", "OwnedRelics", "RelicModels" })
            {
                object? relics = GetPropertyOrFieldValue(runState, propName);
                if (relics is System.Collections.IEnumerable enumerable)
                {
                    var ids = new List<string>();
                    foreach (object relic in enumerable)
                    {
                        object? id = GetPropertyOrFieldValue(relic, "Id");
                        string? idStr = id?.ToString();
                        if (!string.IsNullOrEmpty(idStr))
                        {
                            ids.Add(idStr);
                        }
                    }
                    if (ids.Count > 0) return ids;
                }
            }
            break; // first player only
        }
        return null;
    }

    private static object? GetPropertyOrFieldValue(object? target, string name)
    {
        if (target == null) return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();
        try
        {
            PropertyInfo? prop = type.GetProperty(name, flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                return prop.GetValue(target);
            }
        }
        catch { /* best-effort reflection */ }

        try
        {
            FieldInfo? field = type.GetField(name, flags);
            return field?.GetValue(target);
        }
        catch { return null; }
    }

    private static List<object> DeduplicateRowsByKeys(IEnumerable<object> rows, params string[] keyNames)
    {
        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (object row in rows)
        {
            string? key = BuildRowKey(row, keyNames);
            if (key == null)
            {
                continue;
            }

            if (!merged.TryGetValue(key, out object? existing) ||
                CountPopulatedMembers(row) >= CountPopulatedMembers(existing))
            {
                merged[key] = row;
            }
        }

        return merged.Values.ToList();
    }

    private static string? BuildRowKey(object row, IReadOnlyList<string> keyNames)
    {
        var parts = new string[keyNames.Count];
        for (int i = 0; i < keyNames.Count; i++)
        {
            object? value = GetPropertyOrFieldValue(row, keyNames[i]);
            if (value == null)
            {
                return null;
            }

            parts[i] = value.ToString() ?? "";
        }

        return string.Join('\u001f', parts);
    }

    private static int CountPopulatedMembers(object row)
    {
        int count = 0;
        foreach (PropertyInfo property in row.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.GetIndexParameters().Length != 0) continue;

            object? value;
            try { value = property.GetValue(row); }
            catch { continue; }

            if (value is null) continue;
            if (value is string s && string.IsNullOrWhiteSpace(s)) continue;
            count++;
        }

        return count;
    }

    private static int? TryReadIntMember(object? target, string name)
    {
        object? value = GetPropertyOrFieldValue(target, name);
        if (value is int i) return i;
        if (value != null && int.TryParse(value.ToString(), out int parsed)) return parsed;
        return null;
    }

    private static string? ReadFormattedText(object? target, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            object? value = GetPropertyOrFieldValue(target, memberName);
            if (value == null) continue;

            try
            {
                string? formatted = value.GetType()
                    .GetMethod("GetFormattedText", Type.EmptyTypes)
                    ?.Invoke(value, null)?.ToString();
                if (!string.IsNullOrEmpty(formatted)) return formatted;
            }
            catch { /* best-effort reflection */ }

            try
            {
                string? text = value.ToString();
                if (!string.IsNullOrEmpty(text) && text != value.GetType().FullName)
                {
                    return text;
                }
            }
            catch { /* best-effort reflection */ }
        }

        return null;
    }

    private static string? ReadSafeFormattedOrRawText(object? target, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            object? value = GetPropertyOrFieldValue(target, memberName);
            string? plain = ExtractPlainText(value);
            if (!string.IsNullOrWhiteSpace(plain))
            {
                return plain;
            }

            string? formatted = TryFormatTextWithoutThrowing(value);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted;
            }
        }

        return null;
    }

    private static string? ReadPlainTextMember(object? target, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            string? text = ExtractPlainText(GetPropertyOrFieldValue(target, memberName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadPowerDescription(PowerModel power)
    {
        return ReadPlainTextMember(
            power,
            "SmartDescription",
            "RemoteDescription",
            "Description",
            "EventDescription");
    }

    private static string? ExtractPlainText(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        string? rawText = TryInvokeStringMethod(value, "GetRawText");
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
            object? memberValue = GetPropertyOrFieldValue(value, memberName);
            if (memberValue is string memberText && !string.IsNullOrWhiteSpace(memberText))
            {
                return memberText;
            }
        }

        return null;
    }

    private static string? TryInvokeStringMethod(object? value, string methodName)
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

    private static string? TryFormatTextWithoutThrowing(object? value)
    {
        if (value == null)
        {
            return null;
        }

        try
        {
            string? formatted = value.GetType()
                .GetMethod("GetFormattedText", Type.EmptyTypes)
                ?.Invoke(value, null)?.ToString();
            return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
        }
        catch
        {
            return null;
        }
    }

    private static int ReadCatalogEnergyCost(CardModel card, Type type)
    {
        try
        {
            if (card.Keywords.Contains(CardKeyword.Unplayable))
            {
                return -2;
            }
        }
        catch { /* best-effort */ }

        try
        {
            CardEnergyCost cost = card.EnergyCost;
            return cost.CostsX ? -1 : cost.Canonical;
        }
        catch (Exception ex)
        {
            DiagLog($"  EnergyCost read failed for {type.Name}: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            bool costsX = ReadBoolMember(card, "HasEnergyCostX") ?? false;
            if (costsX)
            {
                return -1;
            }

            int? canonical = TryReadIntMember(card, "CanonicalEnergyCost");
            if (canonical.HasValue)
            {
                return canonical.Value;
            }
        }
        catch { /* best-effort reflection fallback */ }

        return 0;
    }

    private static bool? ReadBoolMember(object? target, string name)
    {
        object? value = GetPropertyOrFieldValue(target, name);
        if (value is bool b) return b;
        if (value != null && bool.TryParse(value.ToString(), out bool parsed)) return parsed;
        return null;
    }

    private static List<object> StableSortForHash(IEnumerable<object> values) =>
        values
            .OrderBy(static value => JsonSerializer.Serialize(value, HashJsonOptions), StringComparer.Ordinal)
            .ToList();

    private static string ComputeReferenceCatalogHash(
        string catalogName,
        string gameVersion,
        string locale,
        IEnumerable<object>? rows)
    {
        List<IReadOnlyDictionary<string, object?>> stableRows = (rows ?? Enumerable.Empty<object>())
            .Select(static row => RemoveVolatileReferenceFields(row))
            .OrderBy(static row => JsonSerializer.Serialize(row, HashJsonOptions), StringComparer.Ordinal)
            .ToList();

        return ComputeStableHash(new
        {
            catalog = catalogName,
            game_version = gameVersion,
            locale,
            rows = stableRows,
        });
    }

    private static IReadOnlyDictionary<string, object?> RemoveVolatileReferenceFields(object row)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (PropertyInfo property in row.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.GetIndexParameters().Length != 0) continue;

            string name = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            if (string.Equals(name, "updated_at", StringComparison.Ordinal))
            {
                continue;
            }

            try { values[name] = property.GetValue(row); }
            catch { /* best-effort hash input */ }
        }

        return values;
    }

    private static string ComputeStableHash(object value)
    {
        string json = JsonSerializer.Serialize(value, HashJsonOptions);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static string Truncate(string? s, int max) =>
        s == null ? "" : s.Length <= max ? s : s[..max];

    // ── Diagnostic logging (temporary) ──────────────────────────────────

    private static void DiagLog(string msg) => TelemetryDiagnostics.Append(msg);

    // ── Records ──────────────────────────────────────────────────────────

    /// <summary>
    /// Slim application record — titles, descriptions, and card metadata are omitted
    /// because they can be looked up via card_id / enchant_type from the catalog.
    /// This reduces per-record size from ~478 bytes to ~120 bytes.
    /// </summary>
    private sealed class EnchantApplicationRecord
    {
        [JsonIgnore]
        public WeakReference<CardModel>? Card { get; init; }
        public string CardId { get; init; } = "";
        public string EnchantType { get; init; } = "";
        public int Amount { get; init; }
        public string Assembly { get; init; } = "";
        public string Source { get; init; } = "unknown";
        public List<string> ExistingTypes { get; init; } = new();
    }

    private sealed class ApplicationSourceScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public ApplicationSourceScope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ApplicationSourceHint.Value = _previous;
        }
    }

    private sealed class DeserializationFailureRecord
    {
        public string EnchantmentId { get; init; } = "";
        public string CardId { get; init; } = "";
        public string Error { get; init; } = "";
    }

    private sealed class ContentSourceInfo
    {
        public string TypeName { get; init; } = "";
        public string FullName { get; init; } = "";
        public string Assembly { get; init; } = "";
        public string AssemblyVersion { get; init; } = "";
        public bool IsThirdParty { get; init; }
    }

    private sealed class ModSnapshot
    {
        [JsonIgnore]
        public string Key => Id ?? Directory ?? Assembly ?? Name;

        public string Name { get; init; } = "";
        public string? Id { get; init; }
        public string? Assembly { get; init; }
        public string? Version { get; init; }
        public string? Directory { get; init; }
        public string? Manifest { get; init; }
        public bool Loaded { get; init; }
        public bool Enabled { get; init; }
        public bool Installed { get; init; }
        public string? LoadState { get; init; }
        public string? Source { get; init; }
        public int? LoadOrder { get; init; }
        public bool ReferencesUs { get; init; }
        public bool HasEnchantments { get; init; }
        public bool? HasDll { get; init; }
        public bool? HasPck { get; init; }
        public bool? AffectsGameplay { get; init; }
        public List<object>? Dependencies { get; init; }

        public static ModSnapshot FromAssembly(
            Assembly assembly,
            ModManifestInfo? manifest,
            ModEnableInfo? enableInfo,
            bool referencesUs,
            bool hasThirdPartyEnchantments)
        {
            string asmName = assembly.GetName().Name ?? "unknown";
            return new ModSnapshot
            {
                Id = manifest?.Id,
                Name = manifest?.DisplayName ?? manifest?.Id ?? asmName,
                Assembly = asmName,
                Version = manifest?.Version ?? GetModVersion(assembly),
                Directory = manifest?.Directory ?? TryGetTopLevelModDirectoryName(assembly),
                Manifest = manifest?.ManifestFileName,
                Loaded = true,
                Enabled = enableInfo?.IsEnabled ?? true,
                Installed = manifest != null,
                LoadState = "Loaded",
                Source = enableInfo?.Source,
                LoadOrder = enableInfo?.LoadOrder,
                ReferencesUs = referencesUs,
                HasEnchantments = hasThirdPartyEnchantments,
                HasDll = manifest?.HasDll,
                HasPck = manifest?.HasPck,
                AffectsGameplay = manifest?.AffectsGameplay,
                Dependencies = manifest?.Dependencies,
            };
        }

        public static ModSnapshot FromRuntimeMod(Mod mod, ModManifestInfo? diskManifest, int order)
        {
            ModManifest? manifest = mod.manifest;
            string? id = manifest?.id ?? diskManifest?.Id;
            string? assemblyName = mod.assembly?.GetName().Name;
            bool loaded = mod.state == ModLoadState.Loaded;
            bool enabled = mod.state is not ModLoadState.Disabled and not ModLoadState.DisabledDuplicate;
            return new ModSnapshot
            {
                Id = id,
                Name = manifest?.name ?? diskManifest?.DisplayName ?? id ?? assemblyName ?? "unknown",
                Assembly = assemblyName,
                Version = manifest?.version ?? diskManifest?.Version ?? (mod.assembly == null ? null : GetModVersion(mod.assembly)),
                Directory = TryGetDirectoryName(mod.path) ?? diskManifest?.Directory,
                Manifest = diskManifest?.ManifestFileName ?? (string.IsNullOrWhiteSpace(id) ? null : id + ".json"),
                Loaded = loaded,
                Enabled = enabled,
                Installed = true,
                LoadState = mod.state.ToString(),
                Source = mod.modSource.ToString(),
                LoadOrder = order,
                ReferencesUs = RuntimeManifestDependsOnUs(manifest) || (diskManifest != null && ManifestDependsOnUs(diskManifest)),
                HasEnchantments = mod.assembly != null && ContainsThirdPartyEnchantmentTypes(mod.assembly),
                HasDll = manifest?.hasDll ?? diskManifest?.HasDll,
                HasPck = manifest?.hasPck ?? diskManifest?.HasPck,
                AffectsGameplay = manifest?.affectsGameplay ?? diskManifest?.AffectsGameplay,
                Dependencies = ReadRuntimeManifestDependencies(manifest) ?? diskManifest?.Dependencies,
            };
        }

        public static ModSnapshot FromManifest(ModManifestInfo manifest, ModEnableInfo? enableInfo)
        {
            return new ModSnapshot
            {
                Id = manifest.Id,
                Name = manifest.DisplayName ?? manifest.Id ?? manifest.Directory ?? "unknown",
                Assembly = null,
                Version = manifest.Version,
                Directory = manifest.Directory,
                Manifest = manifest.ManifestFileName,
                Loaded = false,
                Enabled = enableInfo?.IsEnabled ?? true,
                Installed = true,
                LoadState = null,
                Source = enableInfo?.Source,
                LoadOrder = enableInfo?.LoadOrder,
                ReferencesUs = ManifestDependsOnUs(manifest),
                HasEnchantments = false,
                HasDll = manifest.HasDll,
                HasPck = manifest.HasPck,
                AffectsGameplay = manifest.AffectsGameplay,
                Dependencies = manifest.Dependencies,
            };
        }

        private static string? TryGetTopLevelModDirectoryName(Assembly assembly)
        {
            try
            {
                string? location = assembly.Location;
                return string.IsNullOrWhiteSpace(location) ? null : TelemetryCollector.GetTopLevelModDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetDirectoryName(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                return Path.GetFileName(path.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed class ModManifestInfo
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? Version { get; init; }
        public string? Directory { get; init; }
        public string ManifestFileName { get; init; } = "";
        public bool? HasDll { get; init; }
        public bool? HasPck { get; init; }
        public bool? AffectsGameplay { get; init; }
        public List<object>? Dependencies { get; init; }
    }

    private sealed class ModSettingsSnapshot
    {
        public static readonly ModSettingsSnapshot NotFound = new();

        public bool Found { get; init; }
        public bool ModsEnabled { get; init; } = true;
        public IReadOnlyDictionary<string, ModEnableInfo> ModsByKey { get; init; } =
            new Dictionary<string, ModEnableInfo>(StringComparer.OrdinalIgnoreCase);

        public ModEnableInfo? Find(string? id, string? directory)
        {
            if (!string.IsNullOrWhiteSpace(id) && ModsByKey.TryGetValue(id, out ModEnableInfo? byId))
            {
                return byId;
            }

            if (!string.IsNullOrWhiteSpace(directory) &&
                ModsByKey.TryGetValue(directory, out ModEnableInfo? byDirectory))
            {
                return byDirectory;
            }

            return null;
        }
    }

    private sealed class ModEnableInfo
    {
        public string Id { get; init; } = "";
        public bool IsEnabled { get; init; }
        public string? Source { get; init; }
        public int? LoadOrder { get; init; }
    }

    private sealed class TelemetryHashCache
    {
        public string? EnvironmentHash { get; set; }
        public string? CatalogHash { get; set; }
        public string? ReferenceCardsHash { get; set; }
        public string? ReferenceRelicsHash { get; set; }
        public string? ReferencePowersHash { get; set; }
    }
}

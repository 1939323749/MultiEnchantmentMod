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
    private static bool _runEndedSent;

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
    private static int _beforeEnchantedCancelCount;
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

            // Reference tables: cards, relics, powers — one row per entity (upsert).
            List<object>? refCards = null, refRelics = null, refPowers = null;
            try { refCards = CollectCardCatalog(); }
            catch (Exception ex) { DiagLog($"CollectCardCatalog FAILED: {ex}"); }
            try { refRelics = CollectRelicCatalog(); }
            catch (Exception ex) { DiagLog($"CollectRelicCatalog FAILED: {ex}"); }
            try { refPowers = CollectPowerCatalog(); }
            catch (Exception ex) { DiagLog($"CollectPowerCatalog FAILED: {ex}"); }
            DiagLog($"Ref counts: cards={refCards?.Count}, relics={refRelics?.Count}, powers={refPowers?.Count}");

            // Send order: environment (dedup) → catalog → session → ref tables.
            _sessionSent = true;
            return TelemetryReporter.SendStartupDataAsync(
                environmentData, sessionData, catalogData, refCards, refRelics, refPowers);
        }
        catch (Exception ex)
        {
            _sessionSendStarted = false;
            DiagLog($"SendSessionDataOnce FAILED before queueing startup data: {ex}");
            return Task.CompletedTask;
        }
    }

    // ── Combat lifecycle ─────────────────────────────────────────────────

    internal static void ResetForCombat()
    {
        _totalApplications = 0;
        _totalRemovals = 0;
        _maxEnchantmentsOnCard = 0;
        _beforeEnchantedCancelCount = 0;
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

            // Game state snapshot — only collected when there's enchantment activity
            // to keep zero-enchant combats lightweight.
            List<string>? deckCardIds = null;
            List<string>? relicIds = null;
            string? roomName = null;
            if (_totalApplications > 0 || _enchantedCardPlays > 0)
            {
                try { deckCardIds = CollectDeckCardIds(combatState); } catch { }
                try { relicIds = CollectRelicIds(combatState); } catch { }
                try { roomName = runState?.CurrentRoom?.ToString(); } catch { }
            }

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
                before_enchanted_cancel_count = _beforeEnchantedCancelCount,
                event_bus_publish_count = _eventBusPublishCount,
            });

            if (_runId != null)
            {
                _runCombatCount++;
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
        _runEndedSent = false;
    }

    private static void SendRunSummary(IRunState? runState, string outcome)
    {
        if (_sessionId == null || _runId == null) return;

        int? finalFloor = null;
        int? ascension = null;
        try { finalFloor = runState?.TotalFloor; } catch { }
        try { ascension = runState?.AscensionLevel; } catch { }

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
            combat_count = _runCombatCount,
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

    internal static void NoteEnchantRemoved() => _totalRemovals++;

    internal static void NoteBeforeEnchantCancelled() => _beforeEnchantedCancelCount++;

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

    // ── Data collection helpers ──────────────────────────────────────────

    private static List<object> CollectLoadedMods()
    {
        var result = new List<object>();
        string ourAsmName = typeof(TelemetryCollector).Assembly.GetName().Name ?? "";

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!TryGetRelevantModAssembly(
                    asm,
                    ourAsmName,
                    out string asmName,
                    out bool referencesUs,
                    out bool hasThirdPartyEnchantments))
            {
                continue;
            }

            result.Add(new
            {
                name = asmName,
                version = GetModVersion(asm),
                references_us = referencesUs,
                has_enchantments = hasThirdPartyEnchantments,
            });
        }

        return StableSortForHash(result);
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
                    try { title = canonical.Title?.GetFormattedText(); } catch { }
                    try { description = canonical.Description?.GetFormattedText(); } catch { }
                    try { extraCardText = canonical.DynamicExtraCardText?.GetFormattedText(); } catch { }
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
                    string? title = null;
                    string? description = null;
                    try { title = card.Title; } catch { }
                    try { description = Truncate(card.Description?.GetFormattedText(), 300); } catch { }
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
                    // Title/Description are LocString objects — must call GetFormattedText()
                    string? title = null;
                    try
                    {
                        object? titleObj = GetPropertyOrFieldValue(relic, "Title");
                        title = titleObj?.GetType().GetMethod("GetFormattedText", Type.EmptyTypes)
                            ?.Invoke(titleObj, null)?.ToString();
                        if (string.IsNullOrEmpty(title))
                            title = titleObj?.ToString();
                    }
                    catch { }
                    string? description = null;
                    try
                    {
                        description = Truncate(
                            ReadHoverTipDescription(relic, "HoverTip") ??
                            ReadFormattedText(relic, "DynamicDescription", "Description", "EventDescription"),
                            300);
                    }
                    catch { }
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
                    string? title = ReadFormattedText(power, "Title");
                    string? description = Truncate(
                        ReadHoverTipDescription(power, "DumbHoverTip") ??
                        ReadFormattedText(power, "Description", "SmartDescription", "RemoteDescription"),
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

            string text = File.ReadAllText(path);
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
        try { return enchantment.Title?.GetFormattedText() ?? enchantment.GetType().Name; }
        catch { return enchantment.GetType().Name; }
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

    private static string? ReadHoverTipDescription(object? target, string memberName)
    {
        object? hoverTip = GetPropertyOrFieldValue(target, memberName);
        string? description = GetPropertyOrFieldValue(hoverTip, "Description")?.ToString();
        return string.IsNullOrEmpty(description) ? null : description;
    }

    private static List<object> StableSortForHash(IEnumerable<object> values) =>
        values
            .OrderBy(static value => JsonSerializer.Serialize(value, HashJsonOptions), StringComparer.Ordinal)
            .ToList();

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
}

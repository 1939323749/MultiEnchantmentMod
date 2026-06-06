using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MultiEnchantmentMod.Telemetry;

internal static class TelemetryReporter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static TelemetryReporter()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush(TimeSpan.FromSeconds(2));
    }
    private static readonly object RealtimeQueueLock = new();
    private static readonly object BackgroundQueueLock = new();
    private static Task _realtimeQueue = Task.CompletedTask;
    private static Task _backgroundQueue = Task.CompletedTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static void SendSession(object data) =>
        EnqueueRealtime(async () => { _ = await SendAsync("telemetry_sessions", data); });
    internal static void SendCombat(object data) =>
        EnqueueRealtime(async () => { _ = await SendAsync("telemetry_combats", data); });
    internal static void SendRun(object data) =>
        EnqueueRealtime(async () => { _ = await SendAsync("telemetry_runs", data); });
    internal static void SendCrash(object data) =>
        EnqueueRealtime(async () => { _ = await SendAsync("telemetry_crashes", data); });
    internal static void SendCardReward(object data) =>
        EnqueueRealtime(async () => { _ = await SendAsync("telemetry_card_rewards", data); });

    internal static void SendStartupData(
        object? environmentData, object? sessionData, object? modCatalogData,
        List<object>? refCards, List<object>? refRelics, List<object>? refPowers) =>
        EnqueueBackground(() => SendStartupDataAsync(
            environmentData, sessionData, modCatalogData, refCards, refRelics, refPowers));

    internal static void EnqueueBackgroundWork(Func<Task> work) => EnqueueBackground(work);

    internal static bool Flush(TimeSpan timeout)
    {
        Task realtime;
        Task background;
        lock (RealtimeQueueLock)
        {
            realtime = _realtimeQueue;
        }
        lock (BackgroundQueueLock)
        {
            background = _backgroundQueue;
        }

        var sw = Stopwatch.StartNew();
        bool realtimeDone = WaitForQueue(realtime, timeout);
        TimeSpan remaining = timeout - sw.Elapsed;
        bool backgroundDone = remaining > TimeSpan.Zero
            ? WaitForQueue(background, remaining)
            : background.IsCompleted;
        return realtimeDone && backgroundDone;
    }

    private static bool WaitForQueue(Task pending, TimeSpan timeout)
    {
        try
        {
            return pending.Wait(timeout);
        }
        catch
        {
            return false;
        }
    }

    internal static async Task<StartupUploadResult> SendStartupDataAsync(
        object? environmentData, object? sessionData, object? modCatalogData,
        List<object>? refCards, List<object>? refRelics, List<object>? refPowers)
    {
        // Keep startup reference uploads off the realtime queue. Reference rows
        // are insert-only from the public client; existing keys are ignored.
        return new StartupUploadResult
        {
            SessionUploaded = sessionData == null ||
                              await SendAsync("telemetry_sessions", sessionData),
            EnvironmentUploaded = environmentData == null ||
                                   await SendAsync("telemetry_environments", environmentData),
            ModCatalogUploaded = modCatalogData == null ||
                                  await SendAsync("telemetry_mod_catalog", modCatalogData),
            RefCardsUploaded = refCards is not { Count: > 0 } ||
                               await SendRowsAsync("ref_cards", refCards, "card_id,locale"),
            RefRelicsUploaded = refRelics is not { Count: > 0 } ||
                                await SendRowsAsync("ref_relics", refRelics, "relic_id,locale"),
            RefPowersUploaded = refPowers is not { Count: > 0 } ||
                                await SendRowsAsync("ref_powers", refPowers, "power_id,locale"),
        };
    }

    internal sealed class StartupUploadResult
    {
        internal bool SessionUploaded { get; init; }
        internal bool EnvironmentUploaded { get; init; }
        internal bool ModCatalogUploaded { get; init; }
        internal bool RefCardsUploaded { get; init; }
        internal bool RefRelicsUploaded { get; init; }
        internal bool RefPowersUploaded { get; init; }
    }

    private static async Task<bool> SendAsync(
        string table,
        object data,
        string? onConflict = null,
        bool mergeDuplicates = false,
        bool duplicateIsSuccess = true)
    {
        if (!TelemetryConfig.IsEnabled) return false;

        try
        {
            object payload = NormalizePayloadForSend(data, onConflict);
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            DiagLog($"SendAsync {table}: json_length={json.Length} onConflict={onConflict} merge={mergeDuplicates}");

            string url = $"{TelemetryConfig.SupabaseUrl}/rest/v1/{table}";
            if (!string.IsNullOrWhiteSpace(onConflict))
            {
                url += $"?on_conflict={Uri.EscapeDataString(onConflict)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("apikey", TelemetryConfig.AnonKey);
            request.Headers.Add("Authorization", $"Bearer {TelemetryConfig.AnonKey}");
            request.Headers.Add("Prefer", string.IsNullOrWhiteSpace(onConflict)
                ? "return=minimal"
                : mergeDuplicates
                    ? "return=minimal,resolution=merge-duplicates"
                    : "return=minimal,resolution=ignore-duplicates");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request);
            int statusCode = (int)response.StatusCode;
            DiagLog($"SendAsync {table}: status={statusCode}");

            if (statusCode < 400)
            {
                return true;
            }

            if (statusCode >= 400)
            {
                string body = await response.Content.ReadAsStringAsync();
                if (duplicateIsSuccess &&
                    statusCode == 409 &&
                    body.Contains("\"23505\"", StringComparison.OrdinalIgnoreCase))
                {
                    DiagLog($"SendAsync {table}: duplicate ignored");
                    return true;
                }

                DiagLog($"SendAsync {table} ERROR: {body[..Math.Min(body.Length, 500)]}");
            }
        }
        catch (Exception ex)
        {
            DiagLog($"SendAsync {table} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static async Task<bool> SendRowsAsync(
        string table,
        IEnumerable<object> rows,
        string? onConflict = null)
    {
        List<object> rowList = rows.ToList();
        if (rowList.Count == 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(onConflict) &&
            await SendAsync(table, rowList, onConflict, duplicateIsSuccess: false))
        {
            return true;
        }

        if (await SendAsync(table, rowList, duplicateIsSuccess: false))
        {
            return true;
        }

        bool allUploaded = true;
        foreach (object row in rowList)
        {
            allUploaded &= await SendAsync(table, row);
        }

        return allUploaded;
    }

    private static void EnqueueRealtime(Func<Task> work) =>
        EnqueueSend(work, RealtimeQueueLock, ref _realtimeQueue);

    private static void EnqueueBackground(Func<Task> work) =>
        EnqueueSend(work, BackgroundQueueLock, ref _backgroundQueue);

    private static void EnqueueSend(Func<Task> work, object queueLock, ref Task queue)
    {
        Task queued;
        lock (queueLock)
        {
            queue = queue
                .ContinueWith(
                    static async (previous, state) =>
                    {
                        _ = previous.Exception;
                        var nextWork = (Func<Task>)state!;
                        await nextWork();
                    },
                    work,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
            queued = queue;
        }

        queued.ContinueWith(static t => { _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private static object NormalizePayloadForSend(object data, string? onConflict)
    {
        if (string.IsNullOrWhiteSpace(onConflict) || data is not IEnumerable<object> rows)
        {
            return data;
        }

        string[] keys = onConflict
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0)
        {
            return data;
        }

        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        int originalCount = 0;
        foreach (object row in rows)
        {
            originalCount++;
            string? key = BuildConflictKey(row, keys);
            if (key == null)
            {
                continue;
            }

            if (!merged.TryGetValue(key, out object? existing) ||
                CountPopulatedFields(row) >= CountPopulatedFields(existing))
            {
                merged[key] = row;
            }
        }

        if (merged.Count == 0)
        {
            return data;
        }

        if (merged.Count != originalCount)
        {
            DiagLog($"Deduped payload on ({onConflict}): {originalCount} -> {merged.Count}");
        }

        return merged.Values.ToList();
    }

    private static string? BuildConflictKey(object row, IReadOnlyList<string> keys)
    {
        var parts = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            object? value = ReadMember(row, keys[i]);
            if (value == null)
            {
                return null;
            }

            parts[i] = value.ToString() ?? "";
        }

        return string.Join('\u001f', parts);
    }

    private static int CountPopulatedFields(object row)
    {
        int count = 0;
        foreach (var property in row.GetType().GetProperties())
        {
            object? value;
            try { value = property.GetValue(row); }
            catch { continue; }

            if (value is null) continue;
            if (value is string s && string.IsNullOrWhiteSpace(s)) continue;
            count++;
        }

        return count;
    }

    private static object? ReadMember(object row, string name)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.IgnoreCase;

        try
        {
            var property = row.GetType().GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(row);
            }
        }
        catch { }

        try
        {
            return row.GetType().GetField(name, flags)?.GetValue(row);
        }
        catch
        {
            return null;
        }
    }

    private static void DiagLog(string msg) => TelemetryDiagnostics.Append(msg);
}

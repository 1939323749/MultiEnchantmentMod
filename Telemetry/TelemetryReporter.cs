using System;
using System.Collections.Generic;
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
    private static readonly object QueueLock = new();
    private static Task _sendQueue = Task.CompletedTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static void SendSession(object data) =>
        EnqueueSend(() => SendAsync("telemetry_sessions", data));
    internal static void SendCombat(object data) => EnqueueSend(() => SendAsync("telemetry_combats", data));
    internal static void SendRun(object data) => EnqueueSend(() => SendAsync("telemetry_runs", data));
    internal static void SendCrash(object data) => EnqueueSend(() => SendAsync("telemetry_crashes", data));
    internal static void SendCardReward(object data) => EnqueueSend(() => SendAsync("telemetry_card_rewards", data));

    internal static void SendStartupData(
        object environmentData, object sessionData, object modCatalogData,
        List<object>? refCards, List<object>? refRelics, List<object>? refPowers) =>
        EnqueueSend(() => SendStartupDataAsync(
            environmentData, sessionData, modCatalogData, refCards, refRelics, refPowers));

    internal static void EnqueueBackgroundWork(Func<Task> work) => EnqueueSend(work);

    internal static bool Flush(TimeSpan timeout)
    {
        Task pending;
        lock (QueueLock)
        {
            pending = _sendQueue;
        }

        try
        {
            return pending.Wait(timeout);
        }
        catch
        {
            return false;
        }
    }

    internal static async Task SendStartupDataAsync(
        object environmentData, object sessionData, object modCatalogData,
        List<object>? refCards, List<object>? refRelics, List<object>? refPowers)
    {
        // Create the session first so later combat/run/reward rows can satisfy their FK.
        // Anonymous upserts require SELECT permission/policies in PostgREST, so sessions
        // use plain INSERT because each startup already has a fresh session id.
        await SendAsync("telemetry_sessions", sessionData);
        await SendAsync("telemetry_environments", environmentData, onConflict: "environment_hash");
        await SendAsync("telemetry_mod_catalog", modCatalogData, onConflict: "catalog_hash");

        // Reference tables are discovered by the anonymous client. Insert new keys only;
        // trusted maintenance jobs can update canonical metadata without client pollution.
        if (refCards is { Count: > 0 })
            await SendAsync("ref_cards", refCards, onConflict: "card_id,locale", mergeDuplicates: false);
        if (refRelics is { Count: > 0 })
            await SendAsync("ref_relics", refRelics, onConflict: "relic_id,locale", mergeDuplicates: false);
        if (refPowers is { Count: > 0 })
            await SendAsync("ref_powers", refPowers, onConflict: "power_id,locale", mergeDuplicates: false);
    }

    private static async Task SendAsync(string table, object data, string? onConflict = null, bool mergeDuplicates = false)
    {
        if (!TelemetryConfig.IsEnabled) return;

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

            if (statusCode >= 400)
            {
                string body = await response.Content.ReadAsStringAsync();
                if (statusCode == 409 &&
                    body.Contains("\"23505\"", StringComparison.OrdinalIgnoreCase))
                {
                    DiagLog($"SendAsync {table}: duplicate ignored");
                    return;
                }

                DiagLog($"SendAsync {table} ERROR: {body[..Math.Min(body.Length, 500)]}");
                if (ShouldRetryWithoutConflict(statusCode, body, onConflict))
                {
                    await SendAsync(table, data);
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog($"SendAsync {table} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ShouldRetryWithoutConflict(int statusCode, string body, string? onConflict)
    {
        if (string.IsNullOrWhiteSpace(onConflict))
        {
            return false;
        }

        return statusCode is 401 or 403 &&
               body.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnqueueSend(Func<Task> work)
    {
        Task queued;
        lock (QueueLock)
        {
            _sendQueue = _sendQueue
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
            queued = _sendQueue;
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

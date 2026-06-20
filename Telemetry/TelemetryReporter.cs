using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // Each telemetry stream maps to one PostHog event name.
    internal static void SendSession(object data) =>
        EnqueueRealtime(() => CaptureAsync("session_started", data));
    internal static void SendCombat(object data) =>
        EnqueueRealtime(() => CaptureAsync("combat_completed", data));
    internal static void SendRun(object data) =>
        EnqueueRealtime(() => CaptureAsync("run_ended", data));
    internal static void SendCrash(object data) =>
        EnqueueRealtime(() => CaptureAsync("mod_crash", data));
    internal static void SendCardReward(object data) =>
        EnqueueRealtime(() => CaptureAsync("card_reward", data));

    internal static void SendStartupData(
        object? environmentData, object? sessionData, object? modCatalogData) =>
        EnqueueBackground(() => SendStartupDataAsync(environmentData, sessionData, modCatalogData));

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
        object? environmentData, object? sessionData, object? modCatalogData)
    {
        // These heavy events are hash-gated by the collector (sent only when their
        // content changes), so this just forwards whatever was handed in. A null
        // argument means "unchanged, skip" and counts as already-uploaded.
        return new StartupUploadResult
        {
            SessionUploaded = sessionData == null ||
                              await CaptureAsync("session_started", sessionData),
            EnvironmentUploaded = environmentData == null ||
                                  await CaptureAsync("mod_environment", environmentData),
            ModCatalogUploaded = modCatalogData == null ||
                                 await CaptureAsync("mod_catalog", modCatalogData),
        };
    }

    internal sealed class StartupUploadResult
    {
        internal bool SessionUploaded { get; init; }
        internal bool EnvironmentUploaded { get; init; }
        internal bool ModCatalogUploaded { get; init; }
    }

    /// <summary>
    /// Posts one event to PostHog's batch ingestion endpoint. Fire-and-forget: a
    /// failure is dropped (telemetry is non-critical and must never affect the game),
    /// never retried into an amplifying storm. PostHog ingestion is built for high
    /// concurrency, so there is no per-write connection bottleneck.
    /// </summary>
    private static async Task<bool> CaptureAsync(string eventName, object data)
    {
        if (!TelemetryConfig.IsEnabled) return false;

        try
        {
            // The event's anonymous payload object becomes the PostHog "properties"
            // bag; we only add the actor id and the anonymous-events flag.
            JsonObject properties =
                JsonSerializer.SerializeToNode(data, JsonOptions)?.AsObject() ?? new JsonObject();
            properties["distinct_id"] = TelemetryConfig.InstallationId;
            properties["$process_person_profiles"] = false; // anonymous events: no person profiles

            var payload = new
            {
                api_key = TelemetryConfig.PostHogProjectKey,
                batch = new[]
                {
                    new
                    {
                        @event = eventName,
                        timestamp = DateTimeOffset.UtcNow,
                        properties,
                    },
                },
            };

            string json = JsonSerializer.Serialize(payload, JsonOptions);
            DiagLog($"Capture {eventName}: json_length={json.Length}");

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{TelemetryConfig.PostHogHost}/batch/");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request);
            int statusCode = (int)response.StatusCode;
            DiagLog($"Capture {eventName}: status={statusCode}");

            if (statusCode < 400)
            {
                return true;
            }

            string body = await response.Content.ReadAsStringAsync();
            DiagLog($"Capture {eventName} ERROR: {body[..Math.Min(body.Length, 500)]}");
        }
        catch (Exception ex)
        {
            DiagLog($"Capture {eventName} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
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

    private static void DiagLog(string msg) => TelemetryDiagnostics.Append(msg);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;

namespace MultiEnchantmentMod;

/// <summary>
/// A dedicated node added once to the SceneTree root so its <see cref="_Process"/> fires on EVERY
/// rendered frame, independent of any vanilla node's process state. The previous frame hook
/// (a postfix on NTargetManager._Process) was unreliable because that node stops processing when no
/// targeting is active, so its delta spiked into fake "130ms frames" that were really just sampling
/// gaps. This node gives trustworthy per-frame timing for the managed-vs-render attribution.
/// </summary>
public partial class FrameProfilerNode : Node
{
    public override void _Process(double delta)
    {
        Perf.RecordFrame(delta);
        Perf.MaybeDump("frame");
    }
}

/// <summary>
/// Opt-in, allocation-light profiler for the combat/UI hot paths. Active only while
/// <see cref="MultiEnchantmentMod.VerboseLog"/> is true (manifest <c>"verboseLog": true</c>).
/// When inactive, <see cref="Measure"/> captures no timestamp and <see cref="Scope.Dispose"/>
/// is a single predicted branch, so wrapping a hot method is effectively free in the shipped
/// default. A per-method "calls / total ms / avg ms" table is dumped once per player turn
/// (see SetupPlayerTurnPrefix) so the heaviest method is obvious without an external profiler —
/// this is the measurement that replaced the old always-on log spam.
/// </summary>
internal static class Perf
{
    internal static bool Enabled;

    private sealed class Stat
    {
        public long Calls;
        public long Ticks;
        public long Bytes;
    }

    private static readonly Dictionary<string, Stat> Stats = new(StringComparer.Ordinal);
    private static readonly object Sync = new();
    private static long _lastSampleTick;

    // Per-frame stats, fed from a Harmony postfix on a singleton vanilla _Process (NTargetManager).
    // This is the ONLY way to see time spent in vanilla rendering BETWEEN the mod's own methods —
    // the per-method table alone can't distinguish "mod is slow" from "rendering this card is slow".
    private static int _frames;
    private static double _frameMsSum;
    private static double _frameMsMax;
    private static int _slowFrames;
    // Managed ticks accumulated within the CURRENT frame (reset every RecordFrame). Lets a single
    // slow frame be split into mod-managed vs render/other and name the worst mod method in it.
    private static long _frameManagedTicks;
    private static readonly Dictionary<string, long> _frameTopName = new(StringComparer.Ordinal);
    private static long _drawCallsMax;
    private static long _drawCallsLast;
    // GC monitoring: a Gen1/Gen2 collection that lands in a frame can stall it 50-150ms with ZERO
    // managed code attributable (the pause is in the runtime, not the mod) — exactly the signature of
    // the "modManaged=0 render/other=130ms worst=-" hitches. Allocation churn (.ToList() everywhere)
    // is the suspected driver.
    private static int _lastG0, _lastG1, _lastG2;
    private static long _lastAllocBytes;
    private static int _winG0, _winG1, _winG2;
    private static long _winAllocBytes;

    /// <summary>Record one rendered frame's duration (seconds). Drives the FPS / slow-frame line and,
    /// for any frame over ~50ms, immediately logs that single hitch split into managed vs render.</summary>
    internal static void RecordFrame(double deltaSec)
    {
        if (!Enabled)
        {
            return;
        }

        double ms = deltaSec * 1000.0;
        long drawCalls = 0;
        try { drawCalls = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame); }
        catch { /* not available */ }

        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long allocBytes = GC.GetTotalAllocatedBytes(false);

        string? line = null;
        lock (Sync)
        {
            int dg0 = g0 - _lastG0, dg1 = g1 - _lastG1, dg2 = g2 - _lastG2;
            long dAlloc = allocBytes - _lastAllocBytes;
            if (_lastAllocBytes == 0) { dg0 = dg1 = dg2 = 0; dAlloc = 0; }
            _lastG0 = g0; _lastG1 = g1; _lastG2 = g2; _lastAllocBytes = allocBytes;
            _winG0 += dg0; _winG1 += dg1; _winG2 += dg2; _winAllocBytes += dAlloc;

            _frames++;
            _frameMsSum += ms;
            if (ms > _frameMsMax) _frameMsMax = ms;
            if (ms > 33.0) _slowFrames++;
            if (drawCalls > _drawCallsMax) _drawCallsMax = drawCalls;
            _drawCallsLast = drawCalls;

            double tickMs = 1000.0 / Stopwatch.Frequency;
            double managed = _frameManagedTicks * tickMs;
            if (ms > 50.0)
            {
                string worst = "-";
                long worstTicks = 0;
                foreach (KeyValuePair<string, long> kv in _frameTopName)
                {
                    if (kv.Value > worstTicks) { worstTicks = kv.Value; worst = kv.Key; }
                }

                line = "[MultiEnchantment][Perf] SLOW FRAME " + ms.ToString("F1", CultureInfo.InvariantCulture)
                    + "ms drawCalls=" + drawCalls.ToString(CultureInfo.InvariantCulture)
                    + " GC(g0/g1/g2)=" + dg0.ToString(CultureInfo.InvariantCulture) + "/" + dg1.ToString(CultureInfo.InvariantCulture) + "/" + dg2.ToString(CultureInfo.InvariantCulture)
                    + " allocKB=" + (dAlloc / 1024).ToString(CultureInfo.InvariantCulture)
                    + " — modManaged=" + managed.ToString("F1", CultureInfo.InvariantCulture)
                    + "ms render/other=" + Math.Max(0, ms - managed).ToString("F1", CultureInfo.InvariantCulture)
                    + "ms; worst mod method this frame=" + worst + " (" + (worstTicks * tickMs).ToString("F1", CultureInfo.InvariantCulture) + "ms)";
            }

            _frameManagedTicks = 0;
            _frameTopName.Clear();
        }

        if (line != null)
        {
            MultiEnchantmentMod.Logger.Info(line);
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;
        private readonly long _startBytes;

        internal Scope(string? name)
        {
            _name = name;
            _start = name == null ? 0L : Stopwatch.GetTimestamp();
            _startBytes = name == null ? 0L : GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            if (_name == null)
            {
                return;
            }

            long elapsed = Stopwatch.GetTimestamp() - _start;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - _startBytes;
            lock (Sync)
            {
                if (!Stats.TryGetValue(_name, out Stat? s))
                {
                    s = new Stat();
                    Stats[_name] = s;
                }

                s.Calls++;
                s.Ticks += elapsed;
                s.Bytes += bytes;
                _frameManagedTicks += elapsed;
                if (!_frameTopName.TryGetValue(_name, out long acc))
                {
                    acc = 0;
                }
                _frameTopName[_name] = acc + elapsed;
            }
        }
    }

    /// <summary>Wrap a hot path with <c>using var _ = Perf.Measure("Name");</c>.</summary>
    internal static Scope Measure(string name) => new(Enabled ? name : null);

    /// <summary>Increment a pure call-frequency counter (no timing).</summary>
    internal static void Count(string name)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Sync)
        {
            if (!Stats.TryGetValue(name, out Stat? s))
            {
                s = new Stat();
                Stats[name] = s;
            }

            s.Calls++;
        }
    }

    /// <summary>
    /// Time-gated self-sampling dump: call from hot paths so that whenever the game is actually
    /// busy (e.g. a stutter while hovering a heavily-enchanted card) the accumulated table is
    /// flushed at most once per <paramref name="intervalMs"/>. This captures the INTERACTIVE
    /// window — the per-turn dump only captures the gap before a turn starts (deck restore / setup).
    /// </summary>
    internal static void MaybeDump(string reason, long intervalMs = 2000)
    {
        if (!Enabled)
        {
            return;
        }

        // _lastSampleTick is read/written outside Sync. Safe because every Perf.* caller runs on the
        // main thread (STS2 combat + render are single-threaded lockstep); revisit if ever called off-thread.
        long now = System.Environment.TickCount64;
        if (now - _lastSampleTick < intervalMs)
        {
            return;
        }

        _lastSampleTick = now;
        Dump(reason);
    }

    /// <summary>Emits the accumulated table (sorted by total time) and resets it. No-op when disabled.</summary>
    internal static void Dump(string reason)
    {
        if (!Enabled)
        {
            return;
        }

        List<KeyValuePair<string, Stat>> snapshot;
        int frames;
        double frameMsSum, frameMsMax;
        int slowFrames;
        long drawCallsMax, drawCallsLast;
        int winG1, winG2;
        double winAllocMb;
        lock (Sync)
        {
            if (Stats.Count == 0 && _frames == 0)
            {
                return;
            }

            snapshot = Stats.OrderByDescending(static kv => kv.Value.Ticks).ToList();
            Stats.Clear();
            frames = _frames;
            frameMsSum = _frameMsSum;
            frameMsMax = _frameMsMax;
            slowFrames = _slowFrames;
            drawCallsMax = _drawCallsMax;
            drawCallsLast = _drawCallsLast;
            winG1 = _winG1;
            winG2 = _winG2;
            winAllocMb = _winAllocBytes / (1024.0 * 1024.0);
            _frames = 0;
            _frameMsSum = 0;
            _frameMsMax = 0;
            _slowFrames = 0;
            _drawCallsMax = 0;
            _winG0 = 0;
            _winG1 = 0;
            _winG2 = 0;
            _winAllocBytes = 0;
        }

        double tickMs = 1000.0 / Stopwatch.Frequency;
        double managedMs = snapshot.Sum(kv => kv.Value.Ticks) * tickMs;
        double fps = 0;
        try { fps = Engine.GetFramesPerSecond(); } catch { /* not on main thread / not ready */ }
        double avgFrame = frames > 0 ? frameMsSum / frames : 0;
        double renderMs = Math.Max(0, frameMsSum - managedMs);

        StringBuilder sb = new();
        sb.Append("[MultiEnchantment][Perf] ").Append(reason)
            .Append(" — FPS≈").Append(fps.ToString("F0", CultureInfo.InvariantCulture))
            .Append(" frames=").Append(frames.ToString(CultureInfo.InvariantCulture))
            .Append(" avgFrame=").Append(avgFrame.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
            .Append(" maxFrame=").Append(frameMsMax.ToString("F1", CultureInfo.InvariantCulture)).Append("ms")
            .Append(" slow(>33ms)=").Append(slowFrames.ToString(CultureInfo.InvariantCulture))
            .Append(" drawCalls(max/last)=").Append(drawCallsMax.ToString(CultureInfo.InvariantCulture)).Append('/').Append(drawCallsLast.ToString(CultureInfo.InvariantCulture))
            .Append(" GC(g1/g2)=").Append(winG1.ToString(CultureInfo.InvariantCulture)).Append('/').Append(winG2.ToString(CultureInfo.InvariantCulture))
            .Append(" alloc=").Append(winAllocMb.ToString("F1", CultureInfo.InvariantCulture)).Append("MB")
            .Append("  |  window: modManaged=").Append(managedMs.ToString("F0", CultureInfo.InvariantCulture)).Append("ms")
            .Append(" nonMod(render/other/GC)≈").Append(renderMs.ToString("F0", CultureInfo.InvariantCulture)).Append("ms")
            .Append("\n  >>> If nonMod >> modManaged while FPS is low, the lag is NOT in this mod's logic (vanilla render / other mod / GC).")
            .Append("\n  per-method cost since last dump:");
        foreach (KeyValuePair<string, Stat> kv in snapshot)
        {
            double ms = kv.Value.Ticks * tickMs;
            double avg = kv.Value.Calls > 0 ? ms / kv.Value.Calls : 0;
            double allocMb = kv.Value.Bytes / (1024.0 * 1024.0);
            sb.Append('\n')
                .Append("  ").Append(kv.Key.PadRight(36))
                .Append(" calls=").Append(kv.Value.Calls.ToString(CultureInfo.InvariantCulture).PadLeft(8))
                .Append("  total=").Append(ms.ToString("F1", CultureInfo.InvariantCulture).PadLeft(9)).Append("ms")
                .Append("  avg=").Append(avg.ToString("F3", CultureInfo.InvariantCulture)).Append("ms")
                .Append("  alloc=").Append(allocMb.ToString("F1", CultureInfo.InvariantCulture)).Append("MB");
        }

        MultiEnchantmentMod.Logger.Info(sb.ToString());
    }
}

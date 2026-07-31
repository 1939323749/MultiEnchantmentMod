using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;
// Godot also defines a Label (the UI node), so the IL one needs disambiguating.
using EmitLabel = System.Reflection.Emit.Label;

namespace MultiEnchantmentMod;

/// <summary>
/// Experimental (v2.5 branch): makes third-party <c>card.Enchantment is XXX</c> checks
/// multi-enchantment aware without the third-party mod referencing this mod.
///
/// <para>C# compiles <c>card.Enchantment is XXX</c> / <c>as XXX</c> / <c>is XXX x</c> to the IL
/// pair <c>callvirt CardModel.get_Enchantment(); isinst XXX</c> inside the caller's own method
/// body. That pair only ever sees the vanilla primary slot, so a card carrying XXX in one of this
/// mod's additional slots reads as "not enchanted with XXX" to any mod that hasn't migrated to
/// <c>MultiEnchantmentApi.HasEnchantment</c>. After all mods finish loading, this rewriter scans
/// every loaded third-party mod assembly for that exact instruction pair and Harmony-transpiles
/// each containing method, folding the pair into <see cref="FindFirstAssignable"/> — which checks
/// the primary slot first (preserving original behavior whenever it matched before) and then the
/// additional slots.</para>
///
/// <para>Three more shapes are handled by replacing the <c>isinst</c> alone (see
/// <see cref="FindFirstAssignableFromInstance"/>, which recovers the owning card from the
/// enchantment instance): a value parked in a local first (<c>var e = card.Enchantment;</c> then a
/// later <c>e is X</c>, or a <c>switch</c> over it), and the null-conditional
/// <c>card?.Enchantment is X</c> whose <c>isinst</c> is a branch target. A fourth pattern targets
/// value modifiers rather than type tests: a mod that copies the base game's damage/block math into
/// its own method body calls <c>card.Enchantment.EnchantDamageAdditive(...)</c> directly, applying
/// only the primary slot's contribution; those calls are retargeted at the aggregating shims in
/// <c>MultiEnchantmentSupport</c>.</para>
///
/// <para>Anything it cannot rewrite (generic methods, a value arriving from a source whose
/// provenance cannot be proven, or a Harmony patch failure) is left untouched and logged as a
/// warning telling the player that mod's enchantment detection may miss additional slots, with a
/// pointer to the wiki's no-dependency compatibility bridge. Every outcome is also tallied per
/// (mod, pattern) for telemetry — the near-miss counts are the measure of what this pass still
/// leaves behind.</para>
///
/// <para>Note the division of labour with <see cref="MultiEnchantmentEnchantInternalGuard"/>: IL
/// rewriting is for logic a mod has COPIED into its own body, where there is no entry point left to
/// hook. When a mod still calls a vanilla method, patching that method is strictly better — it
/// covers every call site at once, regardless of the caller's IL shape.</para>
///
/// <para>Opt-outs: a mod can declare an assembly-level attribute whose type is <b>named</b>
/// <c>MultiEnchantmentNoRewriteAttribute</c> (declared in its own assembly — no reference to this
/// mod needed; matched by name), or the player can set <c>"rewriteIsChecks": false</c> in
/// <c>MultiEnchantmentMod.json</c>.</para>
/// </summary>
internal static class MultiEnchantmentIsCheckRewriter
{
    private const string OptOutAttributeName = "MultiEnchantmentNoRewriteAttribute";

    private const string CompatAdvice =
        "That check only sees the card's PRIMARY enchantment, so this mod may fail to detect " +
        "enchantments in additional slots (enchantment-dependent behavior may silently not " +
        "trigger). Mod author fix: replace 'card.Enchantment is X' with the no-dependency " +
        "compatibility bridge — https://github.com/1939323749/MultiEnchantmentMod/wiki/" +
        "Integrating-MultiEnchantmentMod (\"Optional: No Hard Dependency\" section).";

    private static readonly MethodInfo GetEnchantmentGetter =
        AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Enchantment));

    private static readonly MethodInfo ShimMethod =
        AccessTools.Method(typeof(MultiEnchantmentIsCheckRewriter), nameof(FindFirstAssignable));

    private static readonly MethodInfo InstanceShimMethod =
        AccessTools.Method(typeof(MultiEnchantmentIsCheckRewriter), nameof(FindFirstAssignableFromInstance));

    private static readonly HashSet<MethodBase> LoggedMethods = new();

    private static Harmony? _harmony;
    private static bool _hasRun;
    private static int _deferAttempts;
    private static int _rewrittenMethods;
    private static int _rewrittenSites;
    private static int _unrewrittenSites;

    // ── Per-pattern / per-outcome statistics ────────────────────────────────────────────────────
    // The aggregate counters above drive the single human-readable summary line. These break the
    // same events down by (mod, pattern, outcome) so telemetry can answer "how much would another
    // rewrite pattern actually buy" instead of us guessing. Kept in memory only; the environment
    // snapshot uploads them once per unique mod set (see TelemetryCollector).

    /// <summary>Stable identifier for the IL shape a site matched (or failed to match).</summary>
    internal const string PatternIsCheck = "is_check";

    /// <summary>
    /// Sites where a mod re-implements vanilla's damage/block math over <c>card.Enchantment</c>
    /// instead of going through the game's hook, so only the primary slot contributes.
    /// </summary>
    internal const string PatternValueModifier = "value_modifier";

    /// <summary>
    /// Vanilla value-modifier virtuals → the aggregating shim that replaces them. Each shim takes
    /// the owning <c>CardModel</c> where the virtual took its <c>EnchantmentModel</c> receiver and
    /// keeps the remaining parameters identical, so the rewrite is a straight opcode swap with no
    /// stack reshaping: drop the getter, retarget the call.
    /// </summary>
    private static readonly Dictionary<MethodInfo, MethodInfo> ValueModifierShims = BuildValueModifierShims();

    private static Dictionary<MethodInfo, MethodInfo> BuildValueModifierShims()
    {
        Dictionary<MethodInfo, MethodInfo> map = new();

        void Add(string vanillaName, Type[] parameters, string shimName)
        {
            MethodInfo? vanilla = AccessTools.Method(typeof(EnchantmentModel), vanillaName, parameters);
            MethodInfo? shim = AccessTools.Method(typeof(MultiEnchantmentSupport), shimName);
            if (vanilla == null || shim == null)
            {
                // A game update renamed or reshaped the virtual. Losing one pattern is survivable
                // (those sites simply stay primary-slot-only); a null in the table is not.
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment] value-modifier rewrite: could not bind " +
                    $"EnchantmentModel.{vanillaName} → MultiEnchantmentSupport.{shimName}; " +
                    "third-party copies of the base-game damage/block math will only see the primary slot.");
                return;
            }

            map[vanilla] = shim;
        }

        Add(nameof(EnchantmentModel.EnchantDamageAdditive), new[] { typeof(decimal), typeof(ValueProp) },
            nameof(MultiEnchantmentSupport.AggregateDamageAdditiveDelta));
        Add(nameof(EnchantmentModel.EnchantDamageMultiplicative), new[] { typeof(decimal), typeof(ValueProp) },
            nameof(MultiEnchantmentSupport.AggregateDamageMultiplicativeFactor));
        Add(nameof(EnchantmentModel.EnchantBlockAdditive), new[] { typeof(decimal) },
            nameof(MultiEnchantmentSupport.AggregateBlockAdditiveDelta));
        Add(nameof(EnchantmentModel.EnchantBlockMultiplicative), new[] { typeof(decimal) },
            nameof(MultiEnchantmentSupport.AggregateBlockMultiplicativeFactor));

        return map;
    }

    internal enum RewriteOutcome
    {
        /// <summary>The site was folded into a multi-slot-aware shim.</summary>
        Rewritten,

        /// <summary>
        /// The getter and the type test are separated by instructions we cannot prove safe to fold
        /// (switch-on-type, a value that flowed through an untracked local, ...).
        /// </summary>
        NearMiss,

        /// <summary>
        /// The type test is a branch target (<c>card?.Enchantment</c>), so some path reaches it with
        /// a stack shape that bypasses the shim.
        /// </summary>
        BranchTarget,

        /// <summary>Harmony refused the patch.</summary>
        PatchFailed,

        /// <summary>The method tripped the byte pre-filter but its IL could not be parsed.</summary>
        IlUnreadable,

        /// <summary>
        /// The method was skipped wholesale because it (or its declaring type) is generic —
        /// <c>PatchProcessor.ReadMethodBody</c> cannot open those and Harmony cannot patch them.
        /// Counted so the blind spot is visible rather than silent.
        /// </summary>
        GenericSkipped,
    }

    internal readonly record struct RewriteStatKey(string ModId, string Pattern, RewriteOutcome Outcome);

    private static readonly Dictionary<RewriteStatKey, int> Stats = new();

    /// <summary>
    /// Maps a scanned method back to the mod that owns it. The Harmony transpiler runs detached
    /// from the scan loop (and re-runs whenever another mod patches the same method), so it cannot
    /// otherwise attribute its outcomes to a mod.
    /// </summary>
    private static readonly Dictionary<MethodBase, string> MethodOwners = new();

    private static void RecordStat(string modId, string pattern, RewriteOutcome outcome, int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        RewriteStatKey key = new(modId, pattern, outcome);
        lock (Stats)
        {
            Stats[key] = Stats.GetValueOrDefault(key) + count;
        }
    }

    private static string OwnerOf(MethodBase method)
    {
        lock (Stats)
        {
            return MethodOwners.GetValueOrDefault(method, "<unknown>");
        }
    }

    /// <summary>
    /// Snapshot of every (mod, pattern, outcome) tally recorded so far, for telemetry. Safe to call
    /// at any point; returns an empty map when the rewrite pass is disabled or has not run.
    /// </summary>
    internal static IReadOnlyList<(RewriteStatKey Key, int Count)> GetStatsSnapshot()
    {
        lock (Stats)
        {
            return Stats.Select(static kv => (kv.Key, kv.Value)).ToList();
        }
    }

    /// <summary>True once the deferred scan pass has actually executed.</summary>
    internal static bool HasRun => _hasRun;

    /// <summary>
    /// Queues the scan onto the next idle frame. Called from <c>Initialize()</c>, which runs
    /// inside ModManager's synchronous mod-loading loop — by the next frame every mod assembly
    /// is guaranteed to be loaded, so the scan sees them all regardless of load order.
    /// </summary>
    public static void ScheduleAfterAllModsLoaded()
    {
        try
        {
            Callable.From((Action)RunDeferred).CallDeferred();
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                "[MultiEnchantment] Could not schedule the is-check rewrite pass; third-party " +
                $"'card.Enchantment is X' checks will only see the primary slot. {CompatAdvice} Error: {ex.Message}");
        }
    }

    private static void RunDeferred()
    {
        if (_hasRun)
        {
            return;
        }

        if (ModManager.State != ModManagerState.Initialized)
        {
            // Mod loading spanned a frame boundary (e.g. Steam workshop reads). Re-queue a few
            // times rather than scanning a half-loaded mod list.
            if (++_deferAttempts < 300)
            {
                Callable.From((Action)RunDeferred).CallDeferred();
            }
            else
            {
                MultiEnchantmentMod.Logger.Warn(
                    "[MultiEnchantment] ModManager never reached Initialized; giving up on the " +
                    $"is-check rewrite pass. {CompatAdvice}");
            }
            return;
        }

        _hasRun = true;
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] is-check rewrite pass failed: {ex}. {CompatAdvice}");
        }
    }

    private static void Run()
    {
        _harmony = new Harmony("MultiEnchantmentMod.IsCheckRewrite");
        Assembly self = typeof(MultiEnchantmentIsCheckRewriter).Assembly;
        int scannedAssemblies = 0;

        foreach (Mod mod in ModManager.Mods)
        {
            if (mod.state != ModLoadState.Loaded)
            {
                continue;
            }

            // v0.108.0 replaced Mod.assembly (single Assembly?) with Mod.assemblies
            // (List<Assembly>) — a mod can now ship more than one managed assembly. Scan each.
            foreach (Assembly modAssembly in mod.assemblies)
            {
                if (modAssembly == null || modAssembly == self)
                {
                    continue;
                }

                if (HasOptOutAttribute(modAssembly))
                {
                    MultiEnchantmentMod.Logger.Info(
                        $"[MultiEnchantment] is-check rewrite: skipping {mod.manifest?.id ?? modAssembly.GetName().Name} " +
                        $"({OptOutAttributeName} present).");
                    continue;
                }

                scannedAssemblies++;
                ScanAssembly(mod.manifest?.id ?? modAssembly.GetName().Name ?? "<unknown>", modAssembly);
            }
        }

        if (scannedAssemblies > 0 || _rewrittenSites > 0 || _unrewrittenSites > 0)
        {
            MultiEnchantmentMod.Logger.Info(
                $"[MultiEnchantment] load-time rewrite: scanned {scannedAssemblies} mod assembly(ies); " +
                $"rewrote {_rewrittenSites} card.Enchantment site(s) in {_rewrittenMethods} method(s) " +
                $"to be multi-enchantment aware; {_unrewrittenSites} site(s) left primary-slot-only (see warnings above).");
        }
    }

    private static bool HasOptOutAttribute(Assembly assembly)
    {
        try
        {
            return assembly.GetCustomAttributesData()
                .Any(attr => attr.AttributeType.Name == OptOutAttributeName);
        }
        catch
        {
            return false;
        }
    }

    private static void ScanAssembly(string modId, Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
        catch (Exception ex)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] is-check rewrite: could not enumerate types in {modId}: {ex.Message}. {CompatAdvice}");
            return;
        }

        foreach (Type type in types)
        {
            foreach (MethodBase method in EnumerateMethods(type))
            {
                ScanMethod(modId, method);
            }
        }
    }

    private static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
                                 BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.DeclaredOnly;
        // GetTypes() already includes nested (compiler-generated) types, so async/iterator
        // state-machine MoveNext bodies — where most third-party OnPlay/CanEnchant logic
        // actually lives — are covered by this per-type enumeration.
        foreach (ConstructorInfo ctor in type.GetConstructors(All))
        {
            yield return ctor;
        }

        foreach (MethodInfo methodInfo in type.GetMethods(All))
        {
            yield return methodInfo;
        }
    }

    private static void ScanMethod(string modId, MethodBase method)
    {
        // Abstract → no body to scan.
        if (method.IsAbstract)
        {
            return;
        }

        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return;
        }

        // Cheap byte pre-filter, taken as the UNION of what the patterns need. Every pattern starts
        // at a `call`/`callvirt` (0x28/0x6F) of the get_Enchantment getter, so that byte is the only
        // universally required one — the is-check patterns additionally need an isinst (0x75), but
        // the value-modifier pattern has no distinctive byte of its own. (This used to demand the
        // isinst unconditionally, which would have silently filtered out every value-modifier site.)
        // These bytes can also occur inside operands, so this only narrows candidates — the
        // ReadMethodBody parse below is the real arbiter and never rewrites a non-matching site.
        if (il == null ||
            (Array.IndexOf(il, (byte)0x6F) < 0 && Array.IndexOf(il, (byte)0x28) < 0))
        {
            return;
        }

        // Generic method / generic declaring type → cannot be Harmony-patched AND
        // PatchProcessor.ReadMethodBody throws "Specified method is not supported" on open
        // generics. This screen sits AFTER the byte pre-filter on purpose: running it first (as
        // this code used to) discarded generic methods before we knew whether they even read
        // card.Enchantment, so the blind spot was invisible. Now only generic methods that
        // plausibly contain an is-check are counted, which makes the tally meaningful.
        if (method.ContainsGenericParameters ||
            (method.DeclaringType?.ContainsGenericParameters ?? false))
        {
            RecordStat(modId, PatternIsCheck, RewriteOutcome.GenericSkipped);
            if (MultiEnchantmentMod.VerboseLog)
            {
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] is-check rewrite: {Describe(method)} ({modId}) is generic and cannot be " +
                    "patched; any 'card.Enchantment is X' it contains stays primary-slot-only.");
            }
            return;
        }

        int adjacentSites = 0;
        int viaLocalSites = 0;
        int valueModifierSites = 0;
        int nearMissSites = 0;
        try
        {
            List<Insn> codes = PatchProcessor.ReadMethodBody(method)
                .Select(static kv => new Insn(kv.Key, kv.Value))
                .ToList();

            // Locals that provably only ever hold a card's primary enchantment. Any `isinst` fed by
            // one of these can be rewritten even though the getter is nowhere near it.
            HashSet<int> enchantmentLocals = ComputeEnchantmentLocals(codes);

            for (int i = 0; i < codes.Count; i++)
            {
                // `ldloc V; isinst T` where V only ever receives card.Enchantment. Covers the
                // store-to-local, switch-on-enchantment and "assign then test later" shapes that
                // the adjacent-pair fold cannot reach.
                if (i + 1 < codes.Count &&
                    IsEnchantmentIsInst(codes[i + 1]) &&
                    enchantmentLocals.Contains(GetLoadLocalIndex(codes[i])))
                {
                    viaLocalSites++;
                    continue;
                }

                if (!IsGetEnchantmentCall(codes[i]))
                {
                    continue;
                }

                if (i + 1 < codes.Count && IsEnchantmentIsInst(codes[i + 1]))
                {
                    adjacentSites++;
                    continue;
                }

                // `card.Enchantment.EnchantDamageAdditive(...)` and friends — a copy of the
                // base-game damage/block math that only ever sees the primary slot.
                if (TryFindValueModifierCall(codes, i, out _, out _))
                {
                    valueModifierSites++;
                    continue;
                }

                // Still-unreachable shapes: the enchantment flows into the type test through
                // something we cannot prove (a field, an array slot, a tainted local, a call
                // result). Report so the author knows that site stays primary-slot-only.
                for (int j = i + 2; j < codes.Count && j <= i + 4; j++)
                {
                    if (IsEnchantmentIsInst(codes[j]))
                    {
                        nearMissSites++;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // The method only tripped the byte pre-filter — there is no evidence yet that it even
            // reads card.Enchantment — so a player-facing compat warning here would be a false
            // alarm. Keep it as a verbose-only diagnostic.
            RecordStat(modId, PatternIsCheck, RewriteOutcome.IlUnreadable);
            if (MultiEnchantmentMod.VerboseLog)
            {
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] is-check rewrite: skipped {Describe(method)} ({modId}); " +
                    $"could not read IL: {ex.Message}.");
            }
            return;
        }

        if (nearMissSites > 0)
        {
            _unrewrittenSites += nearMissSites;
            RecordStat(modId, PatternIsCheck, RewriteOutcome.NearMiss, nearMissSites);
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] {Describe(method)} ({modId}) reads card.Enchantment into a complex pattern " +
                $"at {nearMissSites} site(s) that cannot be rewritten automatically. {CompatAdvice}");
        }

        if (adjacentSites == 0 && viaLocalSites == 0 && valueModifierSites == 0)
        {
            return;
        }

        // Generic methods are already screened out at the top of this method (ReadMethodBody cannot
        // parse open generics), so any method reaching here is non-generic and Harmony-patchable.
        // Register the owner before patching: Harmony may invoke the transpiler synchronously from
        // inside Patch(), and the transpiler needs the mod id to attribute its outcomes.
        lock (Stats)
        {
            MethodOwners[method] = modId;
        }

        try
        {
            _harmony!.Patch(method, transpiler: new HarmonyMethod(
                typeof(MultiEnchantmentIsCheckRewriter), nameof(Transpiler)));
        }
        catch (Exception ex)
        {
            int failedSites = adjacentSites + viaLocalSites + valueModifierSites;
            _unrewrittenSites += failedSites;
            RecordStat(modId, PatternIsCheck, RewriteOutcome.PatchFailed, adjacentSites + viaLocalSites);
            RecordStat(modId, PatternValueModifier, RewriteOutcome.PatchFailed, valueModifierSites);
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Failed to rewrite {failedSites} 'card.Enchantment is X' site(s) in " +
                $"{Describe(method)} ({modId}): {ex.Message}. {CompatAdvice}");
        }
    }

    /// <summary>
    /// Opcode + operand, normalized so the same matching logic serves both the read-only scan
    /// (<c>PatchProcessor.ReadMethodBody</c> yields <c>KeyValuePair&lt;OpCode, object&gt;</c>) and the
    /// transpiler (which yields <c>CodeInstruction</c>). Labels and exception blocks exist only on
    /// the latter, so guards that need them stay transpiler-side.
    /// </summary>
    private readonly record struct Insn(OpCode Op, object? Operand);

    private static bool IsGetEnchantmentCall(in Insn code)
    {
        return (code.Op == OpCodes.Callvirt || code.Op == OpCodes.Call) &&
               code.Operand is MethodInfo methodInfo &&
               methodInfo.Equals(GetEnchantmentGetter);
    }

    private static bool IsEnchantmentIsInst(in Insn code)
    {
        return code.Op == OpCodes.Isinst &&
               code.Operand is Type type &&
               typeof(EnchantmentModel).IsAssignableFrom(type);
    }

    /// <summary>
    /// Local slot index a load instruction reads, or -1 when the instruction is not a local load.
    /// The short forms encode the index in the opcode itself; the long forms carry it as an
    /// operand whose CLR type varies by producer (raw index when parsed from bytes,
    /// <c>LocalVariableInfo</c> / <c>LocalBuilder</c> when produced by Harmony).
    /// </summary>
    private static int GetLoadLocalIndex(in Insn code)
    {
        if (code.Op == OpCodes.Ldloc_0) return 0;
        if (code.Op == OpCodes.Ldloc_1) return 1;
        if (code.Op == OpCodes.Ldloc_2) return 2;
        if (code.Op == OpCodes.Ldloc_3) return 3;
        if (code.Op == OpCodes.Ldloc || code.Op == OpCodes.Ldloc_S) return ResolveLocalIndex(code.Operand);
        return -1;
    }

    private static int GetStoreLocalIndex(in Insn code)
    {
        if (code.Op == OpCodes.Stloc_0) return 0;
        if (code.Op == OpCodes.Stloc_1) return 1;
        if (code.Op == OpCodes.Stloc_2) return 2;
        if (code.Op == OpCodes.Stloc_3) return 3;
        if (code.Op == OpCodes.Stloc || code.Op == OpCodes.Stloc_S) return ResolveLocalIndex(code.Operand);
        return -1;
    }

    /// <summary>
    /// Starting from the <c>get_Enchantment</c> call at <paramref name="getterIndex"/>, finds the
    /// value-modifier call that consumes its result as the <i>receiver</i> — i.e. the shape
    /// <c>card.Enchantment.EnchantDamageAdditive(damage, props)</c>, where the getter and the call
    /// are separated by however many instructions it takes to evaluate the arguments.
    /// </summary>
    /// <remarks>
    /// Walks forward tracking how many stack slots sit above the enchantment. The enchantment is
    /// the receiver exactly when that count equals the call's argument count. Anything whose stack
    /// effect is not modelled below — a branch, a store, <c>dup</c> (which would leave a second
    /// reference we would then mistype), an object allocation — aborts the search rather than
    /// guessing, so an unrecognised shape is left alone instead of miscompiled.
    /// </remarks>
    private static bool TryFindValueModifierCall(
        IReadOnlyList<Insn> codes,
        int getterIndex,
        out int callIndex,
        out MethodInfo shim)
    {
        callIndex = -1;
        shim = null!;

        int slotsAboveEnchantment = 0;
        for (int i = getterIndex + 1; i < codes.Count; i++)
        {
            Insn insn = codes[i];

            if ((insn.Op == OpCodes.Callvirt || insn.Op == OpCodes.Call) &&
                insn.Operand is MethodInfo callee &&
                ValueModifierShims.TryGetValue(callee, out MethodInfo? candidate) &&
                slotsAboveEnchantment == callee.GetParameters().Length)
            {
                callIndex = i;
                shim = candidate;
                return true;
            }

            if (!TryGetStackDelta(insn, out int delta))
            {
                return false;
            }

            slotsAboveEnchantment += delta;
            if (slotsAboveEnchantment < 0)
            {
                // Something below our enchantment got consumed, so it is no longer the receiver of
                // anything we could rewrite.
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Net stack effect of the instructions that can legitimately appear while evaluating a value
    /// modifier's arguments. Returns <c>false</c> for anything else, which callers treat as "stop".
    /// </summary>
    private static bool TryGetStackDelta(in Insn insn, out int delta)
    {
        delta = 0;
        OpCode op = insn.Op;
        string name = op.Name ?? string.Empty;

        if (op == OpCodes.Nop)
        {
            return true;
        }

        // Pushes one value, consumes nothing.
        if (op == OpCodes.Ldnull || op == OpCodes.Ldstr ||
            op == OpCodes.Ldsfld || op == OpCodes.Ldsflda ||
            op == OpCodes.Ldloca || op == OpCodes.Ldloca_S ||
            op == OpCodes.Ldarga || op == OpCodes.Ldarga_S ||
            op == OpCodes.Ldtoken ||
            GetLoadLocalIndex(insn) >= 0 ||
            name.StartsWith("ldarg", StringComparison.Ordinal) ||
            name.StartsWith("ldc.", StringComparison.Ordinal))
        {
            delta = 1;
            return true;
        }

        // Consumes one, produces one.
        if (op == OpCodes.Ldfld || op == OpCodes.Ldflda ||
            op == OpCodes.Castclass || op == OpCodes.Isinst ||
            op == OpCodes.Box || op == OpCodes.Unbox_Any ||
            op == OpCodes.Neg || op == OpCodes.Not ||
            name.StartsWith("conv.", StringComparison.Ordinal))
        {
            return true;
        }

        if (op == OpCodes.Pop)
        {
            delta = -1;
            return true;
        }

        // Binary arithmetic / comparison: consumes two, produces one.
        if (op == OpCodes.Add || op == OpCodes.Sub || op == OpCodes.Mul || op == OpCodes.Div ||
            op == OpCodes.Rem || op == OpCodes.And || op == OpCodes.Or || op == OpCodes.Xor ||
            op == OpCodes.Shl || op == OpCodes.Shr || op == OpCodes.Shr_Un ||
            op == OpCodes.Ceq || op == OpCodes.Cgt || op == OpCodes.Cgt_Un ||
            op == OpCodes.Clt || op == OpCodes.Clt_Un)
        {
            delta = -1;
            return true;
        }

        if ((op == OpCodes.Call || op == OpCodes.Callvirt) && insn.Operand is MethodInfo method)
        {
            int popped = method.GetParameters().Length + (method.IsStatic ? 0 : 1);
            int pushed = method.ReturnType == typeof(void) ? 0 : 1;
            delta = pushed - popped;
            return true;
        }

        return false;
    }

    /// <summary>Local slot whose ADDRESS is taken (so it can be written through), or -1.</summary>
    private static int GetAddressOfLocalIndex(in Insn code)
    {
        return code.Op == OpCodes.Ldloca || code.Op == OpCodes.Ldloca_S
            ? ResolveLocalIndex(code.Operand)
            : -1;
    }

    private static int ResolveLocalIndex(object? operand) => operand switch
    {
        // LocalBuilder derives from LocalVariableInfo, so this one case covers both.
        LocalVariableInfo local => local.LocalIndex,
        int index => index,
        short index => index,
        ushort index => index,
        byte index => index,
        sbyte index => index,
        _ => -1,
    };

    /// <summary>
    /// Locals that provably hold nothing but a card's primary enchantment: every store into them is
    /// fed directly by <c>CardModel.get_Enchantment</c>, and their address is never taken (which
    /// would allow an indirect write we cannot see). For such a local, any value loaded out of it is
    /// some card's primary enchantment — so the shim can recover that card from
    /// <see cref="EnchantmentModel.Card"/> and search its additional slots. Loop bodies that store a
    /// different card's enchantment on each iteration stay correct, because the card is recovered
    /// from the instance at runtime rather than assumed at rewrite time.
    /// </summary>
    private static HashSet<int> ComputeEnchantmentLocals(IReadOnlyList<Insn> codes)
    {
        HashSet<int> fedByGetter = new();
        HashSet<int> tainted = new();

        for (int i = 0; i < codes.Count; i++)
        {
            int addressOf = GetAddressOfLocalIndex(codes[i]);
            if (addressOf >= 0)
            {
                tainted.Add(addressOf);
                continue;
            }

            int stored = GetStoreLocalIndex(codes[i]);
            if (stored < 0)
            {
                continue;
            }

            if (i > 0 && IsGetEnchantmentCall(codes[i - 1]))
            {
                fedByGetter.Add(stored);
            }
            else
            {
                tainted.Add(stored);
            }
        }

        fedByGetter.ExceptWith(tainted);
        return fedByGetter;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        List<CodeInstruction> codes = instructions.ToList();

        // Value-modifier sites are rewritten first, in place: the pass swaps two existing
        // instructions without changing the list length, so the is-check pass below can build its
        // own view afterwards without index bookkeeping. The two patterns cannot overlap — a getter
        // feeding a value-modifier call is never immediately followed by an isinst.
        int valueModifierRewrites = RewriteValueModifierSites(codes);

        List<Insn> view = codes.Select(static c => new Insn(c.opcode, c.operand)).ToList();
        HashSet<int> enchantmentLocals = ComputeEnchantmentLocals(view);
        HashSet<int> nullConditionalSites = FindNullConditionalIsInstSites(codes, view);

        List<CodeInstruction> output = new(codes.Count + 8);
        int rewritten = 0;
        int skipped = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            // ── Preferred shape: adjacent `get_Enchantment; isinst T` folds into ONE shim call,
            // which also removes the virtual getter call from the site entirely.
            //
            // [card] callvirt get_Enchantment [ench] isinst T [ench-or-null]
            //   becomes
            // [card] ldtoken T [card, handle] call FindFirstAssignable [ench-or-null]
            if (i + 1 < codes.Count &&
                IsGetEnchantmentCall(view[i]) &&
                IsEnchantmentIsInst(view[i + 1]) &&
                codes[i + 1].labels.Count == 0 &&
                codes[i + 1].blocks.Count == 0)
            {
                CodeInstruction ldToken = new(OpCodes.Ldtoken, (Type)view[i + 1].Operand!);
                ldToken.labels.AddRange(codes[i].labels);
                ldToken.blocks.AddRange(codes[i].blocks);
                output.Add(ldToken);
                output.Add(new CodeInstruction(OpCodes.Call, ShimMethod));
                rewritten++;
                i++;
                continue;
            }

            // ── Fallback shape: rewrite the type test ALONE, leaving whatever produced the value
            // untouched. One instruction becomes two, the stack shape is unchanged, and branch
            // targets survive because the incoming labels move onto the ldtoken. This reaches the
            // sites the fold cannot: a value parked in a local (`var e = card.Enchantment;` then a
            // later `e is X`, switch-on-enchantment) and the null-conditional
            // `card?.Enchantment is X` where some path jumps straight at the isinst.
            //
            // [ench-or-null] isinst T
            //   becomes
            // [ench-or-null] ldtoken T [ench-or-null, handle] call FindFirstAssignableFromInstance
            if (IsEnchantmentIsInst(view[i]) &&
                codes[i].blocks.Count == 0 &&
                (nullConditionalSites.Contains(i) ||
                 (i > 0 && enchantmentLocals.Contains(GetLoadLocalIndex(view[i - 1])))))
            {
                CodeInstruction ldToken = new(OpCodes.Ldtoken, (Type)view[i].Operand!);
                ldToken.labels.AddRange(codes[i].labels);
                output.Add(ldToken);
                output.Add(new CodeInstruction(OpCodes.Call, InstanceShimMethod));
                rewritten++;
                continue;
            }

            // A type test the getter feeds directly that neither shape could claim (it sits inside
            // an exception block, or a path we cannot prove reaches it with an enchantment).
            if (IsEnchantmentIsInst(view[i]) && i > 0 && IsGetEnchantmentCall(view[i - 1]))
            {
                skipped++;
            }

            output.Add(codes[i]);
        }

        codes = output;

        // Transpilers re-run whenever another mod patches the same method later; only count and
        // log the first application.
        if (LoggedMethods.Add(original))
        {
            string modId = OwnerOf(original);
            RecordStat(modId, PatternIsCheck, RewriteOutcome.Rewritten, rewritten);
            RecordStat(modId, PatternIsCheck, RewriteOutcome.BranchTarget, skipped);
            RecordStat(modId, PatternValueModifier, RewriteOutcome.Rewritten, valueModifierRewrites);

            if (valueModifierRewrites > 0)
            {
                _rewrittenSites += valueModifierRewrites;
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] Rewrote {valueModifierRewrites} copied base-game damage/block " +
                    $"calculation site(s) in {Describe(original)} to sum every enchantment slot " +
                    "instead of only the primary one.");
            }

            if (rewritten > 0)
            {
                _rewrittenMethods++;
                _rewrittenSites += rewritten;
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] Rewrote {rewritten} 'card.Enchantment is X' site(s) in {Describe(original)} " +
                    "to also see additional enchantment slots.");
            }

            if (skipped > 0)
            {
                _unrewrittenSites += skipped;
                MultiEnchantmentMod.Logger.Warn(
                    $"[MultiEnchantment] {skipped} 'card.Enchantment is X' site(s) in {Describe(original)} sit in a " +
                    $"shape this pass cannot prove safe to rewrite and were left primary-slot-only. {CompatAdvice}");
            }
        }

        return codes;
    }

    /// <summary>
    /// Retargets <c>card.Enchantment.EnchantDamageAdditive(...)</c>-style calls at the aggregating
    /// shims, so a mod that copied vanilla's damage/block math applies every slot rather than only
    /// the primary one. Mutates <paramref name="codes"/> in place and returns the site count.
    /// </summary>
    /// <remarks>
    /// The rewrite is a pure opcode swap with no stack reshaping: the getter becomes a <c>nop</c>
    /// (leaving the <c>CardModel</c> where the <c>EnchantmentModel</c> would have been) and the
    /// virtual call becomes a static call whose first parameter is that <c>CardModel</c>. Any label
    /// between the two would let a branch arrive mid-span with a different stack, so such spans are
    /// skipped.
    /// </remarks>
    private static int RewriteValueModifierSites(List<CodeInstruction> codes)
    {
        if (ValueModifierShims.Count == 0)
        {
            return 0;
        }

        List<Insn> view = codes.Select(static c => new Insn(c.opcode, c.operand)).ToList();
        int rewritten = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (!IsGetEnchantmentCall(view[i]) ||
                !TryFindValueModifierCall(view, i, out int callIndex, out MethodInfo shim))
            {
                continue;
            }

            if (codes[i].blocks.Count > 0 || codes[callIndex].blocks.Count > 0 ||
                SpanIsBranchedInto(codes, i + 1, callIndex))
            {
                continue;
            }

            CodeInstruction nop = new(OpCodes.Nop);
            nop.labels.AddRange(codes[i].labels);
            nop.blocks.AddRange(codes[i].blocks);
            codes[i] = nop;
            codes[callIndex] = new CodeInstruction(OpCodes.Call, shim);

            view[i] = new Insn(OpCodes.Nop, null);
            view[callIndex] = new Insn(OpCodes.Call, shim);
            rewritten++;
        }

        return rewritten;
    }

    /// <summary>True when any instruction in <c>[start, end]</c> carries an incoming branch label.</summary>
    private static bool SpanIsBranchedInto(List<CodeInstruction> codes, int start, int end)
    {
        for (int i = start; i <= end && i < codes.Count; i++)
        {
            if (codes[i].labels.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Type tests that the null-conditional shape <c>card?.Enchantment is X</c> produces: the
    /// <c>isinst</c> is a branch target, and the paths that jump at it push <c>null</c> rather than
    /// an enchantment. Folding the pair would corrupt those paths' stacks, but rewriting the
    /// <c>isinst</c> alone is safe — the shim maps <c>null</c> to "no match", exactly as the
    /// original <c>isinst</c> did.
    /// </summary>
    /// <remarks>
    /// A label with no branch pointing at it is vestigial (only the fall-through path reaches the
    /// site), so it does not disqualify the rewrite. Anything else reaching the label — a
    /// conditional branch, or an unconditional one not preceded by <c>ldnull</c> — means some path
    /// could arrive with a value we have not proven, so the site is left alone.
    /// </remarks>
    private static HashSet<int> FindNullConditionalIsInstSites(
        List<CodeInstruction> codes,
        List<Insn> view)
    {
        HashSet<int> result = new();

        for (int i = 1; i < codes.Count; i++)
        {
            if (!IsEnchantmentIsInst(view[i]) ||
                codes[i].labels.Count == 0 ||
                codes[i].blocks.Count > 0 ||
                !IsGetEnchantmentCall(view[i - 1]))
            {
                continue;
            }

            bool everyIncomingPathPushesNull = true;
            foreach (EmitLabel label in codes[i].labels)
            {
                for (int j = 0; j < codes.Count; j++)
                {
                    if (!BranchesTo(codes[j], label))
                    {
                        continue;
                    }

                    if ((codes[j].opcode != OpCodes.Br && codes[j].opcode != OpCodes.Br_S) ||
                        j == 0 ||
                        view[j - 1].Op != OpCodes.Ldnull)
                    {
                        everyIncomingPathPushesNull = false;
                        break;
                    }
                }

                if (!everyIncomingPathPushesNull)
                {
                    break;
                }
            }

            if (everyIncomingPathPushesNull)
            {
                result.Add(i);
            }
        }

        return result;
    }

    private static bool BranchesTo(CodeInstruction code, EmitLabel label)
    {
        return code.operand switch
        {
            EmitLabel single => single == label,
            EmitLabel[] many => Array.IndexOf(many, label) >= 0,
            _ => false,
        };
    }

    /// <summary>
    /// Drop-in replacement for a bare <c>isinst T</c> whose operand provably came from
    /// <c>CardModel.Enchantment</c>. Recovers the owning card from the instance and then mirrors
    /// <see cref="FindFirstAssignable"/>: the primary slot first (so any site that matched before
    /// this mod existed still resolves to the same instance), then the additional slots.
    /// </summary>
    /// <remarks>
    /// <para>A <c>null</c> input maps to "no match", which is what the original <c>isinst</c> did —
    /// this is the <c>card?.Enchantment</c> path, and also an unenchanted card.</para>
    /// <para><c>HasCard</c> is a plain field read; the <c>Card</c> property itself asserts
    /// mutability and throws <c>CanonicalModelException</c> on a canonical ModelDb instance, so it
    /// must never be touched unguarded — third-party code does test canonical instances
    /// (<c>ModelDb.Enchantment&lt;X&gt;() is Y</c>), and those legitimately have no card.</para>
    /// </remarks>
    internal static EnchantmentModel? FindFirstAssignableFromInstance(
        EnchantmentModel? primary,
        RuntimeTypeHandle typeHandle)
    {
        Type? type = Type.GetTypeFromHandle(typeHandle);
        if (type == null)
        {
            // Unreachable for handles baked in via ldtoken; mirror `isinst <invalid>` as no-match.
            return null;
        }

        if (primary == null)
        {
            return null;
        }

        if (type.IsInstanceOfType(primary))
        {
            return primary;
        }

        if (!primary.HasCard || !primary.IsMutable)
        {
            return null;
        }

        CardModel card = primary.Card;
        bool includeMarkers = typeof(MarkerEnchantmentModel).IsAssignableFrom(type);
        IReadOnlyList<EnchantmentModel> extras = MultiEnchantmentSupport.GetAdditionalEnchantments(card);
        for (int i = 0; i < extras.Count; i++)
        {
            EnchantmentModel extra = extras[i];
            if (!includeMarkers && extra is MarkerEnchantmentModel)
            {
                continue;
            }

            if (type.IsInstanceOfType(extra))
            {
                return extra;
            }
        }

        return null;
    }

    /// <summary>
    /// Drop-in replacement for the <c>callvirt get_Enchantment; isinst T</c> pair. Checks the
    /// primary slot first — any site that matched before this mod existed still resolves to the
    /// same instance — then the additional slots in application order. Marker enchantments are
    /// only visible to marker-typed checks, mirroring <c>MultiEnchantmentApi.HasEnchantment</c>.
    /// Kept allocation-free: this runs wherever third-party code put is-checks, including
    /// per-card hot paths.
    /// </summary>
    internal static EnchantmentModel? FindFirstAssignable(CardModel card, RuntimeTypeHandle typeHandle)
    {
        // Null card faithfully NREs here, exactly like the original callvirt would have.
        EnchantmentModel? primary = card.Enchantment;
        Type? type = Type.GetTypeFromHandle(typeHandle);
        if (type == null)
        {
            // Unreachable for handles baked in via ldtoken; mirror `isinst <invalid>` as no-match.
            return null;
        }

        if (primary != null && type.IsInstanceOfType(primary))
        {
            return primary;
        }

        bool includeMarkers = typeof(MarkerEnchantmentModel).IsAssignableFrom(type);
        IReadOnlyList<EnchantmentModel> extras = MultiEnchantmentSupport.GetAdditionalEnchantments(card);
        for (int i = 0; i < extras.Count; i++)
        {
            EnchantmentModel extra = extras[i];
            if (!includeMarkers && extra is MarkerEnchantmentModel)
            {
                continue;
            }

            if (type.IsInstanceOfType(extra))
            {
                return extra;
            }
        }

        return null;
    }

    private static string Describe(MethodBase method)
    {
        return $"{method.DeclaringType?.FullName ?? "<global>"}.{method.Name}";
    }
}

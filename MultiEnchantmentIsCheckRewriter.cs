using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

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
/// <para>Anything it cannot rewrite (generic methods, non-adjacent patterns such as multi-arm
/// <c>switch</c>-on-enchantment or <c>card?.Enchantment</c>, or a Harmony patch failure) is left
/// untouched, and MOST such shapes are logged as a warning telling the player that mod's
/// enchantment detection may miss additional slots, with a pointer to the wiki's no-dependency
/// compatibility bridge. The near-miss detection is a heuristic window, not dataflow analysis —
/// a getter and isinst separated by many instructions (e.g. stored to a local, null-checked,
/// then tested much later) can stay silent.</para>
///
/// <para>Opt-out: a mod author declares an assembly-level attribute whose type is <b>named</b>
/// <c>MultiEnchantmentNoRewriteAttribute</c> (declared in its own assembly — no reference to this
/// mod needed; matched by name). There is deliberately NO player-facing toggle: a per-player
/// switch would let two multiplayer clients diverge in third-party enchantment logic and desync
/// the lockstep sim.</para>
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

    private static readonly HashSet<MethodBase> LoggedMethods = new();

    private static Harmony? _harmony;
    private static bool _hasRun;
    private static int _deferAttempts;
    private static int _rewrittenMethods;
    private static int _rewrittenSites;
    private static int _unrewrittenSites;

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
            if (mod.assembly == null || mod.assembly == self)
            {
                continue;
            }

            string modId = mod.manifest?.id ?? mod.assembly.GetName().Name ?? "<unknown>";

            if (mod.state != ModLoadState.Loaded)
            {
                // A mod can end up Failed AFTER its assembly loaded and its Harmony patches were
                // applied (ModManager marks Loaded even when a [ModInitializer] throws, but a
                // later initializer TYPE failing to resolve, or an OnModDetected subscriber
                // throwing, downgrades it) — that patch code still runs at runtime. We don't scan
                // non-Loaded mods (half-initialized types are the loader's problem, not ours to
                // poke), but say so instead of silently skipping.
                if (mod.state == ModLoadState.Failed)
                {
                    MultiEnchantmentMod.Logger.Warn(
                        $"[MultiEnchantment] is-check rewrite: {modId} failed to load but its assembly is " +
                        "live (any Harmony patches it applied before failing still run); its " +
                        $"'card.Enchantment is X' checks stay primary-slot-only. {CompatAdvice}");
                }
                continue;
            }

            if (HasOptOutAttribute(modId, mod.assembly))
            {
                continue;
            }

            scannedAssemblies++;
            ScanAssembly(modId, mod.assembly);
        }

        if (scannedAssemblies > 0 || _rewrittenSites > 0 || _unrewrittenSites > 0)
        {
            MultiEnchantmentMod.Logger.Info(
                $"[MultiEnchantment] is-check rewrite: scanned {scannedAssemblies} mod assembly(ies); " +
                $"rewrote {_rewrittenSites} 'card.Enchantment is X' site(s) in {_rewrittenMethods} method(s) " +
                $"to be multi-enchantment aware; {_unrewrittenSites} site(s) left primary-slot-only (see warnings above).");
        }
    }

    private static bool HasOptOutAttribute(string modId, Assembly assembly)
    {
        try
        {
            if (assembly.GetCustomAttributesData()
                .Any(attr => attr.AttributeType.Name == OptOutAttributeName))
            {
                MultiEnchantmentMod.Logger.Info(
                    $"[MultiEnchantment] is-check rewrite: skipping {modId} ({OptOutAttributeName} present).");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // Unreadable attribute table ⇒ we cannot rule out an author's explicit opt-out.
            // Skip the assembly: a missed rewrite merely keeps the pre-rewrite status quo
            // (primary-slot-only checks), while rewriting against a declared opt-out would
            // break the documented contract.
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] is-check rewrite: could not read assembly attributes of {modId} " +
                $"({ex.GetType().Name}: {ex.Message}); skipping it as if {OptOutAttributeName} were present. {CompatAdvice}");
            return true;
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
        byte[]? il;
        try
        {
            if (method.IsAbstract)
            {
                return;
            }

            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            return;
        }

        // Cheap pre-filter: no isinst opcode byte anywhere → no is/as check of any kind.
        // (0x75 can appear inside operands too; false positives just cost a ReadMethodBody.)
        if (il == null || Array.IndexOf(il, (byte)0x75) < 0)
        {
            return;
        }

        int adjacentSites = 0;
        int nearMissSites = 0;
        try
        {
            List<KeyValuePair<OpCode, object>> codes = PatchProcessor.ReadMethodBody(method).ToList();
            for (int i = 0; i < codes.Count; i++)
            {
                if (!IsGetEnchantmentCall(codes[i]))
                {
                    continue;
                }

                if (i + 1 < codes.Count && IsEnchantmentIsInst(codes[i + 1]))
                {
                    adjacentSites++;
                    continue;
                }

                // switch-on-type / pattern-with-stloc / store-to-local-then-test shapes put a few
                // instructions between the getter call and the isinst (measured on Release IL:
                // multi-arm switch ≈ +3; `var e = card.Enchantment; if (e == null) …; if (e is X)`
                // ≈ +6). We don't rewrite those (no local dataflow analysis), but we do tell the
                // player/author which method still misses additional slots. The window is a
                // heuristic: wide enough for the shapes above, and a false hit (an unrelated
                // enchantment isinst within 8 instructions of a getter call) only costs a
                // spurious advisory warning.
                for (int j = i + 2; j < codes.Count && j <= i + 8; j++)
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
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] is-check rewrite: could not read IL of {Describe(method)} ({modId}): {ex.Message}. {CompatAdvice}");
            return;
        }

        if (nearMissSites > 0)
        {
            _unrewrittenSites += nearMissSites;
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] {Describe(method)} ({modId}) reads card.Enchantment into a complex pattern " +
                $"(switch / null-conditional) at {nearMissSites} site(s) that cannot be rewritten automatically. {CompatAdvice}");
        }

        if (adjacentSites == 0)
        {
            return;
        }

        if (method.ContainsGenericParameters || (method.DeclaringType?.ContainsGenericParameters ?? false))
        {
            _unrewrittenSites += adjacentSites;
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] {Describe(method)} ({modId}) contains {adjacentSites} 'card.Enchantment is X' " +
                $"site(s) but is generic and cannot be Harmony-patched. {CompatAdvice}");
            return;
        }

        try
        {
            _harmony!.Patch(method, transpiler: new HarmonyMethod(
                typeof(MultiEnchantmentIsCheckRewriter), nameof(Transpiler)));
        }
        catch (Exception ex)
        {
            _unrewrittenSites += adjacentSites;
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] Failed to rewrite {adjacentSites} 'card.Enchantment is X' site(s) in " +
                $"{Describe(method)} ({modId}): {ex.Message}. {CompatAdvice}");
        }
    }

    private static bool IsGetEnchantmentCall(KeyValuePair<OpCode, object> code)
    {
        return (code.Key == OpCodes.Callvirt || code.Key == OpCodes.Call) &&
               code.Value is MethodInfo methodInfo &&
               methodInfo.Equals(GetEnchantmentGetter);
    }

    private static bool IsEnchantmentIsInst(KeyValuePair<OpCode, object> code)
    {
        return code.Key == OpCodes.Isinst &&
               code.Value is Type type &&
               typeof(EnchantmentModel).IsAssignableFrom(type);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        List<CodeInstruction> codes = instructions.ToList();
        int rewritten = 0;
        int skipped = 0;

        for (int i = 0; i < codes.Count - 1; i++)
        {
            CodeInstruction getterCall = codes[i];
            CodeInstruction isInst = codes[i + 1];
            if ((getterCall.opcode != OpCodes.Callvirt && getterCall.opcode != OpCodes.Call) ||
                getterCall.operand is not MethodInfo methodInfo ||
                !methodInfo.Equals(GetEnchantmentGetter) ||
                isInst.opcode != OpCodes.Isinst ||
                isInst.operand is not Type targetType ||
                !typeof(EnchantmentModel).IsAssignableFrom(targetType))
            {
                continue;
            }

            if (isInst.labels.Count > 0 || isInst.blocks.Count > 0)
            {
                // Some path branches directly to the isinst (card?.Enchantment shape) — that
                // path's stack bypasses our shim, so folding the pair would corrupt it. Leave
                // the site alone and let the player know.
                skipped++;
                continue;
            }

            // [card] callvirt get_Enchantment [ench] isinst T [ench-or-null]
            //   becomes
            // [card] ldtoken T [card, handle] call FindFirstAssignable [ench-or-null]
            CodeInstruction ldToken = new(OpCodes.Ldtoken, targetType);
            ldToken.labels.AddRange(getterCall.labels);
            ldToken.blocks.AddRange(getterCall.blocks);
            codes[i] = ldToken;
            codes[i + 1] = new CodeInstruction(OpCodes.Call, ShimMethod);
            rewritten++;
            i++;
        }

        // Transpilers re-run whenever another mod patches the same method later; only count and
        // log the first application.
        if (LoggedMethods.Add(original))
        {
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
                    $"[MultiEnchantment] {skipped} 'card.Enchantment is X' site(s) in {Describe(original)} are branch " +
                    $"targets (null-conditional access) and were left primary-slot-only. {CompatAdvice}");
            }
        }

        return codes;
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

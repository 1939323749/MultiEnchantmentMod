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
/// <para>Anything it cannot rewrite (generic methods, non-adjacent patterns such as
/// <c>switch</c>-on-enchantment or <c>card?.Enchantment</c>, or a Harmony patch failure) is left
/// untouched and logged as a warning telling the player that mod's enchantment detection may miss
/// additional slots, with a pointer to the wiki's no-dependency compatibility bridge.</para>
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
                $"[MultiEnchantment] is-check rewrite: scanned {scannedAssemblies} mod assembly(ies); " +
                $"rewrote {_rewrittenSites} 'card.Enchantment is X' site(s) in {_rewrittenMethods} method(s) " +
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
        // ── Pre-screen (cheap, before the accurate-but-costlier PatchProcessor.ReadMethodBody) ──
        // Abstract → no body to scan. Generic method / generic declaring type → cannot be
        // Harmony-patched AND PatchProcessor.ReadMethodBody throws "Specified method is not
        // supported" on open generics. Parsing them only manufactures false "could not read IL"
        // warnings for helpers that merely contain an unrelated `is SomeType` check (e.g. a mod's
        // FindChildOfType<T> / FindFirst<T> node walkers), so skip them outright.
        if (method.IsAbstract ||
            method.ContainsGenericParameters ||
            (method.DeclaringType?.ContainsGenericParameters ?? false))
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

        // Cheap byte pre-filter: `card.Enchantment is X` needs BOTH an isinst (0x75) and the
        // call/callvirt (0x28/0x6F) that invokes the get_Enchantment getter. Requiring both trims
        // methods carrying an isinst for unrelated reasons before the costlier parse. These bytes
        // can also occur inside operands, so this only narrows candidates — the ReadMethodBody
        // parse below is the real arbiter and never rewrites a non-matching site.
        if (il == null ||
            Array.IndexOf(il, (byte)0x75) < 0 ||
            (Array.IndexOf(il, (byte)0x6F) < 0 && Array.IndexOf(il, (byte)0x28) < 0))
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

                // switch-on-type / pattern-with-stloc shapes put a few instructions between the
                // getter call and the isinst. We don't rewrite those (no local dataflow analysis),
                // but we do tell the player/author which method still misses additional slots.
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
            MultiEnchantmentMod.Logger.Warn(
                $"[MultiEnchantment] {Describe(method)} ({modId}) reads card.Enchantment into a complex pattern " +
                $"(switch / null-conditional) at {nearMissSites} site(s) that cannot be rewritten automatically. {CompatAdvice}");
        }

        if (adjacentSites == 0)
        {
            return;
        }

        // Generic methods are already screened out at the top of this method (ReadMethodBody cannot
        // parse open generics), so any method reaching here is non-generic and Harmony-patchable.
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

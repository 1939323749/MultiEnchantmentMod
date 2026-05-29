using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Boot-time integrity check for the small set of vanilla MegaCrit methods this mod replaces
/// with hand-edited copies (search the codebase for the comment <c>"Base-game source:"</c>).
/// On startup we hash the live IL of each tracked method and compare against a frozen baseline.
/// Mismatches are logged once per build so a STS2 patch that quietly rewrites one of these
/// methods surfaces immediately instead of producing subtle drift in production.
/// </summary>
/// <remarks>
/// <para>
/// Scope (deliberately small for this first iteration — 6 highest-impact copies):
/// </para>
/// <list type="bullet">
///   <item><c>CardModel.OnPlayWrapper</c> — fully duplicated in
///   <see cref="MultiEnchantmentSupport"/>.<c>OnPlayWrapperWithMultiEnchantments</c>.</item>
///   <item><c>CardCmd.Enchant</c> / <c>CardCmd.ClearEnchantment</c> — wrapped/prefixed.</item>
///   <item><c>CombatManager.SetupPlayerTurn</c> — postfix relies on exact code structure.</item>
///   <item><c>EnchantmentModel.CanEnchant</c> — patched in
///   <c>MultiEnchantmentTransformPatches</c>.</item>
///   <item><c>Hook.ModifyBlock</c> — one representative of the broad Hook-modifier surface.</item>
/// </list>
/// <para>
/// The <c>Tracked</c> list below is the authoritative scope; this guard includes both copied
/// methods and narrow bridges around vanilla single-slot assumptions.
/// </para>
/// <para>
/// Expected hashes are intentionally empty in this build. On first run, the guard logs the
/// computed SHA1 for each tracked method; freeze those values into <see cref="ExpectedHashes"/>
/// in a follow-up commit. This bootstrap shape keeps the guard active (failure-to-resolve =
/// warning) without producing false positives before the baseline exists.
/// </para>
/// </remarks>
internal static class VanillaCopyGuard
{
    private static readonly object Sync = new();
    private static bool _hasRun;

    private static readonly IReadOnlyList<TrackedMethod> Tracked = new[]
    {
        new TrackedMethod("MegaCrit.Sts2.Core.Models.CardModel", "OnPlayWrapper"),
        new TrackedMethod("MegaCrit.Sts2.Core.Commands.CardCmd", "Enchant",
            "MegaCrit.Sts2.Core.Models.EnchantmentModel",
            "MegaCrit.Sts2.Core.Models.CardModel",
            "System.Decimal"),
        new TrackedMethod("MegaCrit.Sts2.Core.Commands.CardCmd", "ClearEnchantment",
            "MegaCrit.Sts2.Core.Models.CardModel"),
        new TrackedMethod("MegaCrit.Sts2.Core.Combat.CombatManager", "SetupPlayerTurn"),
        new TrackedMethod("MegaCrit.Sts2.Core.Models.EnchantmentModel", "CanEnchant",
            "MegaCrit.Sts2.Core.Models.CardModel"),
        new TrackedMethod("MegaCrit.Sts2.Core.Hooks.Hook", "ModifyBlock"),
        new TrackedMethod("MegaCrit.Sts2.Core.Hooks.Hook", "ModifyDamage"),
        new TrackedMethod("MegaCrit.Sts2.Core.Entities.Cards.CardTransformation", "GetReplacement",
            "MegaCrit.Sts2.Core.Random.Rng"),
        new TrackedMethod("MegaCrit.Sts2.Core.Nodes.Cards.NEnchantPreview", "Init",
            "MegaCrit.Sts2.Core.Models.CardModel",
            "MegaCrit.Sts2.Core.Models.EnchantmentModel",
            "System.Int32"),
        new TrackedMethod("MegaCrit.Sts2.Core.Nodes.Vfx.NCardEnchantVfx", "_Ready"),
        new TrackedMethod("MegaCrit.Sts2.Core.Nodes.Vfx.NCardEnchantVfx", "Create",
            "MegaCrit.Sts2.Core.Models.CardModel"),
        new TrackedMethod("MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen.NDeckHistoryEntry", "Reload"),
        new TrackedMethod("MegaCrit.Sts2.Core.Models.Relics.MysticLighter", "ModifyDamageAdditive",
            "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
            "System.Decimal",
            "MegaCrit.Sts2.Core.ValueProps.ValueProp",
            "MegaCrit.Sts2.Core.Entities.Creatures.Creature",
            "MegaCrit.Sts2.Core.Models.CardModel"),
    };

    /// <summary>
    /// Frozen SHA1 hashes for each tracked method. Leave a key absent to opt that method into
    /// "log-only" mode (current IL hash is logged but never compared). Populate after observing
    /// the first build's hashes in the log.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedHashes = new(StringComparer.Ordinal)
    {
        // Populate once the first stable build ships, e.g.:
        // ["MegaCrit.Sts2.Core.Models.CardModel.OnPlayWrapper"] = "a1b2c3...",
    };

    public static void RunOnce()
    {
        lock (Sync)
        {
            if (_hasRun) return;
            _hasRun = true;
        }

        foreach (TrackedMethod tracked in Tracked)
        {
            try
            {
                CheckOne(tracked);
            }
            catch (Exception ex)
            {
                global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                    $"[VanillaCopyGuard] Internal failure while checking {tracked.FullName}: " +
                    $"{ex}");
            }
        }
    }

    private static void CheckOne(TrackedMethod tracked)
    {
        MethodInfo? method = ResolveMethod(tracked);
        if (method == null)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[VanillaCopyGuard] Could not resolve vanilla method {tracked.FullName} — the base " +
                $"game may have renamed or removed it. Patches that copy this method's body need review.");
            return;
        }

        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[VanillaCopyGuard] {tracked.FullName} has no method body (abstract / extern). Skipping.");
            return;
        }

        byte[]? il = body.GetILAsByteArray();
        if (il == null || il.Length == 0)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[VanillaCopyGuard] {tracked.FullName} returned empty IL. Skipping.");
            return;
        }

        string actual = ComputeSha1(il);
        if (ExpectedHashes.TryGetValue(tracked.FullName, out string? expected))
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                    $"[VanillaCopyGuard] DRIFT: {tracked.FullName} IL hash {actual} differs from " +
                    $"expected {expected}. The base game changed this method; review any " +
                    $"\"Base-game source:\" copies that target it.");
            }
        }
        else
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Info(
                $"[VanillaCopyGuard] {tracked.FullName} IL sha1 = {actual} (log-only; freeze this " +
                $"value into ExpectedHashes when ready).");
        }
    }

    private static MethodInfo? ResolveMethod(TrackedMethod tracked)
    {
        Type? type = AccessTools.TypeByName(tracked.TypeFullName);
        if (type == null) return null;

        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly;
        if (tracked.ParameterTypeFullNames.Length > 0)
        {
            Type[] parameterTypes = new Type[tracked.ParameterTypeFullNames.Length];
            for (int i = 0; i < tracked.ParameterTypeFullNames.Length; i++)
            {
                Type? parameterType = Type.GetType(tracked.ParameterTypeFullNames[i]) ??
                    AccessTools.TypeByName(tracked.ParameterTypeFullNames[i]);
                if (parameterType == null)
                {
                    global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                        $"[VanillaCopyGuard] Could not resolve parameter type " +
                        $"{tracked.ParameterTypeFullNames[i]} for {tracked.FullName}.");
                    return null;
                }

                parameterTypes[i] = parameterType;
            }

            return AccessTools.Method(type, tracked.MethodName, parameterTypes);
        }

        MethodInfo[] candidates = type.GetMethods(flags);
        MethodInfo? chosen = null;
        foreach (MethodInfo m in candidates)
        {
            if (!string.Equals(m.Name, tracked.MethodName, StringComparison.Ordinal))
            {
                continue;
            }

            if (chosen != null)
            {
                // Overloads — leave this case to a future per-signature anchor. Skipping is safer
                // than picking the wrong overload and producing a spurious drift warning.
                global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Info(
                    $"[VanillaCopyGuard] {tracked.FullName} has multiple overloads; not hashing.");
                return null;
            }

            chosen = m;
        }

        return chosen;
    }

    private static string ComputeSha1(byte[] bytes)
    {
        byte[] hash = SHA1.HashData(bytes);
        StringBuilder sb = new(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private readonly record struct TrackedMethod(
        string TypeFullName,
        string MethodName,
        params string[] ParameterTypeFullNames)
    {
        public string FullName => $"{TypeFullName}.{MethodName}";
    }
}

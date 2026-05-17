using System;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// One contribution by a registered enchantment type to a single named dynamic variable on a card.
/// Returned in registration order from the registry — execution order is "card application order
/// × registration order on the same enchantment type", with no extra priority layer.
/// </summary>
/// <param name="VarKey">
/// The dynamic-variable key as it appears in <c>card.DynamicVars</c> / <c>enchantment.DynamicVars</c>
/// and in the description placeholder (e.g. <c>"damage"</c>, <c>"block"</c>, <c>"Times"</c>,
/// <c>"Combust"</c>). Matched case-insensitively against the runtime <c>DynamicVar.Name</c>, which
/// is PascalCase in vanilla — authors are free to write the lowercase placeholder form here.
/// </param>
/// <param name="Contribution">
/// Takes the current snapshot for the contributing enchantment type plus the running value, returns
/// the new running value. Authors typically write <c>(snap, current) =&gt; current + snap.MergedAmount * 5m</c>
/// or <c>(snap, current) =&gt; current * 2m</c>; arbitrary clamp / floor / piecewise logic is allowed.
/// </param>
public sealed record DynamicVarContribution(
    string VarKey,
    Func<EnchantmentStackSnapshot, decimal, decimal> Contribution);

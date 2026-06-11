using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Invisible enchantment + friendly marker CRUD sample.
//
// ── Invisible enchantments ──────────────────────────────────────────────────────────────────
//
// `[Enchantment(Invisible = true)]` makes a FULL gameplay enchantment that renders no badge
// icon: it never occupies the vanilla primary slot and is skipped by the badge pipeline. The
// card betrays it only through its extra card text and its effects — hooks, dynamic vars,
// counting, save/load, and multiplayer sync all behave exactly like a normal enchantment.
// The enchant shimmer VFX is suppressed too (nothing visual to announce).
//
//   dev console (Strike has base damage 6):
//     enchant SAMPLE_CURSE_MARK 1   → damage shows 8, description gains the purple line,
//                                     but NO badge appears on the card.
//
// Use this for "the card remembers something" designs: hidden blessings/curses applied by
// events, behind-the-scenes counters that explain themselves in text, or effects whose source
// should stay mysterious. Note what invisibility can NOT hide: changed numbers still render
// in the modified color, and hover tips still list the enchantment (the text is public anyway).
[Enchantment(Invisible = true, Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleCurseMark : EnchantmentModel
{
    public override bool HasExtraCardText => true;

    [ModifyDynamicVar("damage")]
    public decimal AddTwo(EnchantmentStackSnapshot snapshot, decimal current)
    {
        _ = snapshot;
        return current + 2m;
    }
}

// ── Stored markers + friendly CRUD ──────────────────────────────────────────────────────────
//
// MarkerEnchantmentModel is a real, persisted, non-gameplay data tag on a card. The friendly CRUD on
// MultiEnchantmentApi removes all boilerplate — no manual instance creation, no manual
// NotifyPropsChanged:
//
//   // create-or-read, then read data
//   var marker = MultiEnchantmentApi.GetOrAddMarker<SampleChargeCounter>(card);
//   int charges = marker?.Amount ?? 0;
//
//   // set to an exact value (creates when missing)
//   MultiEnchantmentApi.SetMarker<SampleChargeCounter>(card, amount: 3);
//
//   // count up/down (creates at delta when missing; not auto-removed at zero)
//   int now = MultiEnchantmentApi.AddMarkerAmount<SampleChargeCounter>(card, +1);
//
//   // arbitrary mutation with automatic refresh
//   MultiEnchantmentApi.ModifyMarker<SampleChargeCounter>(card, m => m.Amount *= 2);
//
//   // remove
//   MultiEnchantmentApi.RemoveMarker<SampleChargeCounter>(card);
//
// A marker can also modify ITSELF when other code holds the instance (e.g. a relic iterating
// GetMarkers): `marker.AddAmount(1)` / `marker.SetAmount(0)` / mutate Props then
// `marker.NotifyChanged()` — each re-derives state and refreshes the icon row in one call.
//
// To give the marker a visible badge, pair it with RegisterMarker — see sample 31. Without a registered icon the marker is a pure invisible
// data tag, which is also a legitimate use.
public sealed class SampleChargeCounter : MarkerEnchantmentModel
{
}

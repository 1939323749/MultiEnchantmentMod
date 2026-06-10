using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — card-identity bridges: OnCardTransformed + OnCardCloned.
//
// Goal: react when the host card stops being "the same card" — transformed into another card,
// or duplicated by a gameplay effect.
//
// Semantics recap:
//   • OnCardTransformed fires on the ORIGINAL card's enchantments with the replacement card.
//     For the covered vanilla transforms the compatible-enchantment copy has already run, so the
//     handler sees the replacement's final enchantment state. Use it to migrate custom runtime
//     state or clean up card-keyed caches — not to re-copy enchantments.
//   • OnCardCloned fires on the ORIGINAL card's enchantments with the clone (combat clones via
//     CardModel.CreateClone — Juggling, Nightmare, Music Box, Dual Wield, ... — and the
//     rest-site Clone option). The clone has already inherited every enchantment, so the handler
//     can adjust the copy. UI preview clones never fire this hook.

public sealed class SampleSoulbindMark : EnchantmentModel
{
    public override bool ShowAmount => false;
    public override bool HasExtraCardText => true;
}

public static class SampleSoulbindMarkRegistration
{
    private static System.IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.Register<SampleSoulbindMark>()
            .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.AnyInstanceCountsAsOne)
            .OnCardTransformed<SampleSoulbindMark>((original, self, replacement) =>
            {
                // The mark survived the transform via compatible-enchantment copying; just log
                // the identity change. A real mod would migrate its card-keyed runtime state
                // (dictionaries keyed by the original CardModel) over to `replacement` here.
                SampleRegistration.Logger.Info(
                    $"[Samples] SoulbindMark: host {original.Id} transformed into {replacement.Id}.");
            })
            .OnCardCloned<SampleSoulbindMark>((original, self, clone) =>
            {
                // "Soulbound" flavor: copies do not keep the mark. The clone inherited every
                // enchantment a moment ago, so strip our own instance from it.
                foreach (SampleSoulbindMark inherited in
                         MultiEnchantmentApi.GetEnchantments<SampleSoulbindMark>(clone))
                {
                    MultiEnchantmentApi.RemoveEnchantment(clone, inherited);
                }

                SampleRegistration.Logger.Info(
                    $"[Samples] SoulbindMark: clone {clone.Id} of {original.Id} lost the mark.");
            })
            .FormatExtraText((EnchantmentStackSnapshot snapshot, string defaultText, out string formatted) =>
            {
                _ = snapshot;
                _ = defaultText;
                formatted = "Soulbound: copies of this card do not keep this mark.";
                return true;
            })
            .Commit();
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;
using EnchantmentVisualSlice = MultiEnchantmentMod.EnchantmentVisualSlice;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Presentation / UI capability surface of <see cref="IEnchantmentRegistration"/>. Keyword
/// tracking, extra-text formatting, visual slice computation, and history-tooltip behavior.
/// All contributions compose across registrations (last-writer-wins for the single-valued
/// formatters; additive for keyword tracking).
/// </summary>
public interface IPresentationRegistration
{
    IEnchantmentRegistration TrackKeyword(CardKeyword keyword, Func<EnchantmentStackSnapshot, int> amountFn);

    IEnchantmentRegistration FormatExtraText(PresentationTextFormatter formatter);

    IEnchantmentRegistration VisualSlices(Func<EnchantmentStackSnapshot, IReadOnlyList<int>?> compute);

    IEnchantmentRegistration VisualSlicesWithStatus(
        Func<EnchantmentStackSnapshot, IReadOnlyList<EnchantmentVisualSlice>?> compute);

    IEnchantmentRegistration HistoryDisplay(HistoryDisplayMode mode);
    IEnchantmentRegistration HistoryDisplay(HistoryDisplayMode mode, string groupHeader);
    IEnchantmentRegistration HistoryText(HistoryTextFormatter formatter);
}

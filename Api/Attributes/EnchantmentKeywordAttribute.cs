using System;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Declares that an enchantment contributes (or removes) a card keyword while it is active.
/// Multiple attributes can be stacked on one class to track multiple keywords. The total
/// contribution for any given keyword is the sum across all matching attributes and registered
/// providers; positive sums add the keyword, zero or negative sums leave it absent.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EnchantmentKeywordAttribute : Attribute
{
    public CardKeyword Keyword { get; }

    /// <summary>
    /// How the contribution amount is computed. Defaults to <see cref="KeywordEvalMode.PerInstance"/>.
    /// </summary>
    public KeywordEvalMode Mode { get; init; } = KeywordEvalMode.PerInstance;

    /// <summary>
    /// Constant contribution value (only consulted when <see cref="Mode"/> is
    /// <see cref="KeywordEvalMode.Constant"/>). Defaults to <c>1</c>; use a negative number to
    /// represent removal.
    /// </summary>
    public int Constant { get; init; } = 1;

    public EnchantmentKeywordAttribute(CardKeyword keyword)
    {
        Keyword = keyword;
    }
}

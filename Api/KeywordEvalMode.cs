namespace MultiEnchantmentMod.Api;

/// <summary>
/// How <see cref="EnchantmentKeywordAttribute"/> computes a keyword's contribution amount
/// when no custom <see cref="EnchantmentDefinition{TEnchantment}.KeywordSourceAmount"/> override
/// is provided.
/// </summary>
public enum KeywordEvalMode
{
    /// <summary>
    /// Contribute <c>1</c> per live instance of the enchantment. Equivalent to "count active stacks".
    /// </summary>
    PerInstance,

    /// <summary>
    /// Contribute the snapshot's <c>ActiveTotalAmount</c> (the sum of all active merged slices).
    /// Only well-defined when <see cref="StackBehavior"/> is <see cref="StackBehavior.MergeAmount"/>.
    /// </summary>
    PerTotalAmount,

    /// <summary>
    /// Contribute a constant value (taken from <see cref="EnchantmentKeywordAttribute.Constant"/>),
    /// regardless of instance count or merged amount.
    /// </summary>
    Constant,

    /// <summary>
    /// Defer to a user-provided <see cref="EnchantmentDefinition{TEnchantment}.KeywordSourceAmount"/>
    /// override. The attribute is treated as a declaration of intent only.
    /// </summary>
    Custom,
}

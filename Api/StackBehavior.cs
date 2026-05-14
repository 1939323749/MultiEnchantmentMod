namespace MultiEnchantmentMod.Api;

/// <summary>
/// How the mod treats a second (or n-th) application of the same enchantment type on a card.
/// Each enchantment type registers exactly one <see cref="StackBehavior"/> (via
/// <see cref="EnchantmentAttribute"/>, an <see cref="EnchantmentDefinition{TEnchantment}"/>
/// override, or the fluent <see cref="MultiEnchantmentApi.Register{TEnchantment}()"/> builder).
/// </summary>
public enum StackBehavior
{
    /// <summary>
    /// Reject duplicates — <c>CanEnchant</c> returns <c>false</c> when an instance of this type
    /// already exists on the card. This is the default for unknown enchantment types.
    /// </summary>
    DisallowDuplicate,

    /// <summary>
    /// Merge additional applications into the first instance's <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel.Amount"/>
    /// and append the delta as a slice in the <c>MergedStackAmounts</c> metadata so the UI can show
    /// per-application badges. <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel.OnEnchant"/> is
    /// only invoked on the very first application; any per-stack mutation should live in
    /// <see cref="EnchantmentDefinition{TEnchantment}.OnMergedDelta"/>.
    /// </summary>
    MergeAmount,

    /// <summary>
    /// Each application creates a separate <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel"/>
    /// instance. Use this when each application carries independent runtime state (e.g. Goopy's
    /// per-play Amount growth).
    /// </summary>
    DuplicateInstance,

    /// <summary>
    /// Each application creates a separate instance, but only the first instance's
    /// <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel.OnEnchant"/> mutates the card. Later
    /// instances are bookkeeping-only and contribute to status aggregation / keyword tracking.
    /// Use for "presence aura" style enchantments (Steady, PerfectFit, TezcatarasEmber).
    /// </summary>
    ExistenceStack,
}

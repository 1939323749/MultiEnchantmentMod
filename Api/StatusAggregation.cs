namespace MultiEnchantmentMod.Api;

/// <summary>
/// How a stacked enchantment's <see cref="MegaCrit.Sts2.Core.Entities.Enchantments.EnchantmentStatus"/>
/// is computed when there are multiple visual slices or live instances.
/// </summary>
public enum StatusAggregation
{
    /// <summary>
    /// Status per-slice is taken directly from the anchor (first live) instance rather than
    /// being aggregated across slices. When no <c>WhenActiveStatus</c> predicate is registered
    /// the anchor's status defaults to <see cref="MegaCrit.Sts2.Core.Entities.Enchantments.EnchantmentStatus.Normal"/>,
    /// so the enchantment always appears active — use this when the enchantment has no
    /// once-per-turn or conditional-disable semantics (no Goopy-like behaviour).
    /// If a <c>WhenActiveStatus</c> predicate <em>is</em> registered, it can still drive the
    /// anchor status to <see cref="MegaCrit.Sts2.Core.Entities.Enchantments.EnchantmentStatus.Disabled"/>,
    /// which this aggregation mode then mirrors faithfully across all slices.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// All slices share the same status, taken from the anchor (first live) instance.
    /// Natural fit for <see cref="StackBehavior.MergeAmount"/> where multiple visual slices are
    /// really one logical enchantment.
    /// </summary>
    SharedAcrossStack,

    /// <summary>
    /// Each live instance owns its own status; visual slices are zipped 1:1 with live instances.
    /// Only meaningful when <see cref="StackBehavior"/> is not <see cref="StackBehavior.MergeAmount"/>.
    /// </summary>
    PerInstanceOwned,

    /// <summary>
    /// Status is the OR of every live instance: as long as any instance is active, the stack is
    /// shown as active; only when all instances are disabled does the stack render as disabled.
    /// Natural fit for <see cref="StackBehavior.ExistenceStack"/>.
    /// </summary>
    AnyInstanceCountsAsOne,
}

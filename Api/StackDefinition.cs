namespace MultiEnchantmentMod.Api;

/// <summary>
/// What happens when <see cref="StackDefinition.MaxInstances"/> would be exceeded by a new
/// stacking application. Controls only the cap-overflow case; counts below the cap are always
/// accepted. Ignored when <see cref="StackDefinition.MaxInstances"/> is <c>null</c>.
/// </summary>
public enum StackOverflowPolicy
{
    /// <summary>Default. Reject the new application; existing instances unchanged.</summary>
    Reject,

    /// <summary>
    /// Remove the oldest live instance (in card application order) before attaching the new
    /// one, so the count stays at the cap. Useful for "FIFO buff queue" patterns.
    /// </summary>
    ReplaceOldest,

    /// <summary>
    /// Remove the most recently added live instance before attaching the new one. Useful for
    /// "LIFO refresh" patterns where the freshest stack should always be the new one.
    /// </summary>
    ReplaceNewest,
}

/// <summary>
/// The complete stacking contract for one enchantment type: what happens on a second application,
/// and how the visual status of multiple slices is aggregated. This is the v2 replacement for the
/// (now-internal) <c>EnchantmentStackDefinition</c> record.
/// </summary>
public sealed record StackDefinition(StackBehavior Behavior, StatusAggregation Status)
{
    /// <summary>
    /// Reasonable default for unknown enchantment types: refuse duplicates, treat status as
    /// presence-only. Matches the v1 fallback behavior.
    /// </summary>
    public static StackDefinition Default { get; } = new(
        StackBehavior.DisallowDuplicate,
        StatusAggregation.AnyInstanceCountsAsOne);

    /// <summary>
    /// Optional cap on the number of live <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel"/>
    /// instances of this type that a single card can carry. Applies only to
    /// <see cref="StackBehavior.DuplicateInstance"/> and <see cref="StackBehavior.ExistenceStack"/>
    /// — the two behaviors where instance count grows with each application. Ignored for
    /// <see cref="StackBehavior.DisallowDuplicate"/> (always at most 1) and
    /// <see cref="StackBehavior.MergeAmount"/> (single instance, slices grow via Amount delta).
    ///
    /// <para>
    /// Defaults to <c>null</c> = unbounded, preserving existing behavior. Use this to defend
    /// against accidental loops that re-apply the same enchantment from a hook (e.g. a relic
    /// that re-fires on every play) so the card doesn't accrue thousands of duplicate slices.
    /// </para>
    /// </summary>
    public int? MaxInstances { get; init; }

    /// <summary>
    /// Behavior when <see cref="MaxInstances"/> would be exceeded. Defaults to
    /// <see cref="StackOverflowPolicy.Reject"/> for backwards compatibility. Ignored when
    /// <see cref="MaxInstances"/> is <c>null</c>.
    /// </summary>
    public StackOverflowPolicy OnOverflow { get; init; } = StackOverflowPolicy.Reject;
}

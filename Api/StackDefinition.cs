namespace MultiEnchantmentMod.Api;

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
}

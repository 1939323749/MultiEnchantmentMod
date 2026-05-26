using LegacyStackBehavior = MultiEnchantmentMod.EnchantmentStackBehavior;
using LegacyStatusAggregation = MultiEnchantmentMod.EnchantmentStatusAggregation;
using LegacyStackDefinition = MultiEnchantmentMod.EnchantmentStackDefinition;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Translates between the v2 public enums in <c>MultiEnchantmentMod.Api</c> and the compatibility
/// enums in <c>MultiEnchantmentMod</c> that older callers still reference.
/// </summary>
internal static class LegacyEnumMappings
{
    public static LegacyStackBehavior ToLegacy(this StackBehavior value) => value switch
    {
        StackBehavior.DisallowDuplicate => LegacyStackBehavior.DisallowDuplicate,
        StackBehavior.MergeAmount => LegacyStackBehavior.MergeAmount,
        StackBehavior.DuplicateInstance => LegacyStackBehavior.DuplicateInstance,
        StackBehavior.ExistenceStack => LegacyStackBehavior.ExistenceStack,
        _ => LegacyStackBehavior.DisallowDuplicate,
    };

    public static LegacyStatusAggregation ToLegacy(this StatusAggregation value) => value switch
    {
        StatusAggregation.NotApplicable => LegacyStatusAggregation.None,
        StatusAggregation.SharedAcrossStack => LegacyStatusAggregation.Shared,
        StatusAggregation.PerInstanceOwned => LegacyStatusAggregation.PerInstance,
        StatusAggregation.AnyInstanceCountsAsOne => LegacyStatusAggregation.PresenceOnly,
        _ => LegacyStatusAggregation.PresenceOnly,
    };

    public static LegacyStackDefinition ToLegacy(this StackDefinition value) =>
        new(value.Behavior.ToLegacy(), value.Status.ToLegacy());
}

namespace MultiEnchantmentMod.Api;

public enum RemovalReason
{
    Manual,
    CardCleared,
    CombatEnded,
    TurnEnded,
    TurnLimitReached,
    ActivationLimitReached,
    Replaced,

    /// <summary>
    /// The enchantment's <see cref="EnchantmentScope.RemoveWhenScope.Predicate"/> evaluated to
    /// <c>true</c> on one of its <see cref="EnchantmentScope.RemoveWhenScope.CheckOn"/>
    /// activation triggers.
    /// </summary>
    ConditionMet,

    /// <summary>
    /// The enchantment was evicted by <see cref="StackOverflowPolicy.ReplaceOldest"/> or
    /// <see cref="StackOverflowPolicy.ReplaceNewest"/> when a new application would have
    /// exceeded <see cref="StackDefinition.MaxInstances"/>.
    /// </summary>
    OverflowEvicted,
}

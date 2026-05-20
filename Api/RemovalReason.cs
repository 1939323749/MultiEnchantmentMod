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
}

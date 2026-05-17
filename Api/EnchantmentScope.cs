using System;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

public abstract record EnchantmentScope
{
    public static EnchantmentScope Permanent { get; } = new PermanentScope();
    public static EnchantmentScope UntilCombatEnds { get; } = new UntilCombatEndsScope();
    public static EnchantmentScope UntilTurnEnds { get; } = new UntilTurnEndsScope();

    public static EnchantmentScope LingerForTurns(int turns) => new LingerForTurnsScope(turns);

    public static EnchantmentScope MaxActivations(int n, ActivationTrigger t = ActivationTrigger.OnPlay) =>
        new MaxActivationsScope(n, t);

    public static EnchantmentScope ConditionalActive(Func<CardModel, EnchantmentModel, bool> predicate) =>
        new ConditionalActiveScope(predicate);

    public sealed record PermanentScope : EnchantmentScope;
    public sealed record UntilCombatEndsScope : EnchantmentScope;
    public sealed record UntilTurnEndsScope : EnchantmentScope;
    public sealed record LingerForTurnsScope(int Turns) : EnchantmentScope;
    public sealed record MaxActivationsScope(int Max, ActivationTrigger Trigger) : EnchantmentScope;
    public sealed record ConditionalActiveScope(Func<CardModel, EnchantmentModel, bool> Predicate) : EnchantmentScope;
}

public enum ActivationTrigger
{
    OnPlay,
    AfterCardPlayed,
    AfterCardDrawn,
    AfterPlayerTurnStart,
}

public enum ScopeKind
{
    Permanent,
    UntilCombatEnds,
    UntilTurnEnds,
    LingerForTurns,
    MaxActivations,
}

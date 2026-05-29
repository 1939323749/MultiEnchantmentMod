using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

public abstract record EnchantmentScope
{
    public static EnchantmentScope Permanent { get; } = new PermanentScope();
    public static EnchantmentScope UntilCombatEnds { get; } = new UntilCombatEndsScope();
    public static EnchantmentScope UntilTurnEnds { get; } = new UntilTurnEndsScope();

    public static EnchantmentScope LingerForTurns(int turns)
    {
        if (turns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(turns), turns, "LingerForTurns requires a positive turn count.");
        }

        return new LingerForTurnsScope(turns);
    }

    public static EnchantmentScope MaxActivations(int n, ActivationTrigger? t = null)
    {
        if (n <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "MaxActivations requires a positive activation count.");
        }

        return new MaxActivationsScope(n, t ?? ActivationTrigger.OnPlay);
    }

    public static EnchantmentScope ConditionalActive(Func<CardModel, EnchantmentModel, bool> predicate) =>
        new ConditionalActiveScope(predicate);

    /// <summary>
    /// Removes the enchantment as soon as <paramref name="predicate"/> evaluates to <c>true</c>.
    /// The predicate is re-checked on every <see cref="ActivationTrigger"/> listed in
    /// <paramref name="checkOn"/>. Pair with a tight trigger set (e.g. only
    /// <see cref="ActivationTrigger.AfterCardPlayed"/>) for predictable timing; an empty
    /// <paramref name="checkOn"/> means the predicate is never evaluated (effectively
    /// <see cref="Permanent"/>).
    /// </summary>
    public static EnchantmentScope RemoveWhen(
        Func<CardModel, EnchantmentModel, bool> predicate,
        IEnumerable<ActivationTrigger> checkOn)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(checkOn);
        return new RemoveWhenScope(predicate, new List<ActivationTrigger>(checkOn));
    }

    public sealed record PermanentScope : EnchantmentScope;
    public sealed record UntilCombatEndsScope : EnchantmentScope;
    public sealed record UntilTurnEndsScope : EnchantmentScope;
    public sealed record LingerForTurnsScope(int Turns) : EnchantmentScope;
    public sealed record MaxActivationsScope(int Max, ActivationTrigger Trigger) : EnchantmentScope;
    public sealed record ConditionalActiveScope(Func<CardModel, EnchantmentModel, bool> Predicate) : EnchantmentScope;
    public sealed record RemoveWhenScope(
        Func<CardModel, EnchantmentModel, bool> Predicate,
        IReadOnlyList<ActivationTrigger> CheckOn) : EnchantmentScope;
}

/// <summary>
/// Identifies an in-combat event that can either count toward
/// <see cref="EnchantmentScope.MaxActivationsScope"/> or re-evaluate a
/// <see cref="EnchantmentScope.RemoveWhenScope"/> predicate. The vanilla 4-value enum was
/// migrated to a sealed record so authors can extend triggers without forking the mod —
/// existing call sites continue working unchanged because the static accessors return the same
/// instance every time and record equality is value-based on <see cref="Name"/>.
/// </summary>
public sealed record ActivationTrigger(string Name)
{
    // === Card-scoped triggers (fire when the event happens to the enchanted card) ===========
    public static ActivationTrigger OnPlay { get; } = new(nameof(OnPlay));
    public static ActivationTrigger AfterCardPlayed { get; } = new(nameof(AfterCardPlayed));
    public static ActivationTrigger AfterCardDrawn { get; } = new(nameof(AfterCardDrawn));
    public static ActivationTrigger AfterCardExhausted { get; } = new(nameof(AfterCardExhausted));
    public static ActivationTrigger AfterCardDiscarded { get; } = new(nameof(AfterCardDiscarded));

    // === Turn-scoped triggers (fire once per turn for every enchantment on player's cards) ==
    public static ActivationTrigger AfterPlayerTurnStart { get; } = new(nameof(AfterPlayerTurnStart));
    public static ActivationTrigger AfterPlayerTurnEnd { get; } = new(nameof(AfterPlayerTurnEnd));

    // === Owner-scoped triggers (fire when the card owner experiences the event) =============
    public static ActivationTrigger AfterDamageReceived { get; } = new(nameof(AfterDamageReceived));

    /// <summary>
    /// Third-party extension point. Use a stable, namespaced identifier such as
    /// <c>"mymod:OnRelicTriggered"</c> and call
    /// <c>MultiEnchantmentScopeSupport.NoteActivation(enchantment, ActivationTrigger.Custom(...))</c>
    /// from your own patch / hook to count it. Results are cached so repeat calls with the same
    /// identifier return the reference-equal instance — important for tight loops that compare
    /// triggers in inner enumerations.
    /// </summary>
    public static ActivationTrigger Custom(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return _customCache.GetOrAdd(identifier, static id => new ActivationTrigger($"Custom:{id}"));
    }

    private static readonly ConcurrentDictionary<string, ActivationTrigger> _customCache = new(StringComparer.Ordinal);
}

public enum ScopeKind
{
    Permanent,
    UntilCombatEnds,
    UntilTurnEnds,
    LingerForTurns,
    MaxActivations,
    RemoveWhen,
}

using System;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod;

/// <summary>
/// Outcome of a single <see cref="MultiEnchantmentTransformApi.TryCopyCompatibleEnchantments{TReplacement}"/>
/// invocation. The legacy <see cref="MultiEnchantmentTransformApi.CopyCompatibleEnchantments{TReplacement}"/>
/// wrapper silently treated three of these cases (<see cref="AlreadyApplied"/>,
/// <see cref="DifferentSource"/>, <see cref="SourceIsTarget"/>) as no-ops with a log line, which
/// made debugging "why did the transform skip my enchantments" hard. The Try-API surfaces them
/// explicitly so callers can branch on the result.
/// </summary>
public enum TransformCopyOutcome
{
    /// <summary>The replacement card was newly populated with the source's enchantments.</summary>
    Copied,

    /// <summary>
    /// The replacement was already copied to in a previous call (from any source) and the copy
    /// state is still in place. No new mutation was performed.
    /// </summary>
    AlreadyApplied,

    /// <summary>
    /// The replacement was previously populated from a <em>different</em> source. The current
    /// call left it untouched to avoid silently mixing two source decks; reuse a fresh
    /// <see cref="CardModel"/> for the new source or call
    /// <see cref="MultiEnchantmentTransformApi.ResetTransformState"/> first.
    /// </summary>
    DifferentSource,

    /// <summary>The caller passed the same instance for source and replacement.</summary>
    SourceIsTarget,
}

public static class MultiEnchantmentTransformApi
{
    private static readonly ConditionalWeakTable<CardModel, TransformCopyState> TransformCopyStates = new();

    /// <summary>
    /// Copies every enchantment from <paramref name="source"/> that can legally exist on
    /// <paramref name="replacement"/>. Call this after finishing replacement-specific setup
    /// (e.g. upgrades) and before showing previews / calling <c>CardCmd.Transform</c>. The
    /// replacement is intentionally processed at most once by the API so callers can invoke it
    /// from both preview and final-transform paths without duplicating stacks.
    /// </summary>
    /// <returns>The same <paramref name="replacement"/> instance (so calls can be chained).</returns>
    public static TReplacement CopyCompatibleEnchantments<TReplacement>(CardModel source, TReplacement replacement)
        where TReplacement : CardModel
    {
        TryCopyCompatibleEnchantments(source, replacement, out TReplacement _);
        return replacement;
    }

    /// <summary>
    /// Explicit-outcome variant of <see cref="CopyCompatibleEnchantments{TReplacement}"/>. Use
    /// this when you need to know whether the copy actually happened (and why not, if it
    /// didn't).
    /// </summary>
    public static TransformCopyOutcome TryCopyCompatibleEnchantments<TReplacement>(
        CardModel source,
        TReplacement replacement,
        out TReplacement result)
        where TReplacement : CardModel
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacement);
        result = replacement;

        if (ReferenceEquals(source, replacement))
        {
            MultiEnchantmentMod.Logger.Warn("[TransformApi] Refusing to copy enchantments from a card onto itself.");
            return TransformCopyOutcome.SourceIsTarget;
        }

        TransformCopyState state = TransformCopyStates.GetOrCreateValue(replacement);
        if (state.HasAppliedCopy && MultiEnchantmentSupport.HasAnyEnchantments(replacement))
        {
            if (ReferenceEquals(state.Source, source))
            {
                return TransformCopyOutcome.AlreadyApplied;
            }

            MultiEnchantmentMod.Logger.Warn(
                $"[TransformApi] Replacement {replacement.Id} already received transform-copied enchantments from {state.Source?.Id}. Reusing the same replacement for a different source is not supported.");
            return TransformCopyOutcome.DifferentSource;
        }

        MultiEnchantmentSupport.CloneCompatibleEnchantments(source, replacement);
        state.Source = source;
        state.HasAppliedCopy = true;
        return TransformCopyOutcome.Copied;
    }

    /// <summary>
    /// Creates a concrete transformation whose replacement already carries every compatible
    /// enchantment from the original card. Keeps transform previews and the final transform
    /// result in sync for mods that use <see cref="CardTransformation"/> directly.
    /// </summary>
    public static CardTransformation CreateCompatibleTransformation(CardModel source, CardModel replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacement);

        return new CardTransformation(source, CopyCompatibleEnchantments(source, replacement));
    }

    /// <summary>
    /// Clears any previously-recorded transform-copy state for <paramref name="replacement"/>
    /// so the next <see cref="CopyCompatibleEnchantments{TReplacement}"/> call repopulates it.
    /// Intended for test harnesses and for runtime tools that want to reset state without
    /// recreating the card model. Does not touch the replacement's actual enchantment list.
    /// </summary>
    public static void ResetTransformState(CardModel replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (TransformCopyStates.TryGetValue(replacement, out TransformCopyState? state))
        {
            state.Source = null;
            state.HasAppliedCopy = false;
        }
    }

    private sealed class TransformCopyState
    {
        public CardModel? Source { get; set; }

        public bool HasAppliedCopy { get; set; }
    }
}

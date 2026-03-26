using System;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod;

public static class MultiEnchantmentTransformApi
{
    private static readonly ConditionalWeakTable<CardModel, TransformCopyState> TransformCopyStates = new();

    /// <summary>
    /// Copies every enchantment from <paramref name="source"/> that can legally exist on
    /// <paramref name="replacement"/>. Call this after finishing replacement-specific setup
    /// such as upgrades, and before showing previews or calling CardCmd.Transform.
    /// Each replacement card is intentionally processed at most once by this API so third-party
    /// mods can safely call it from both preview and final transform code without duplicating stacks.
    /// </summary>
    public static TReplacement CopyCompatibleEnchantments<TReplacement>(CardModel source, TReplacement replacement)
        where TReplacement : CardModel
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacement);

        if (ReferenceEquals(source, replacement))
        {
            MultiEnchantmentMod.Logger.Warn("[TransformApi] Refusing to copy enchantments from a card onto itself.");
            return replacement;
        }

        TransformCopyState state = TransformCopyStates.GetOrCreateValue(replacement);
        if (state.HasAppliedCopy && MultiEnchantmentSupport.HasAnyEnchantments(replacement))
        {
            if (!ReferenceEquals(state.Source, source))
            {
                MultiEnchantmentMod.Logger.Warn(
                    $"[TransformApi] Replacement {replacement.Id} already received transform-copied enchantments from {state.Source?.Id}. Reusing the same replacement for a different source is not supported.");
            }

            return replacement;
        }

        MultiEnchantmentSupport.CloneCompatibleEnchantments(source, replacement);
        state.Source = source;
        state.HasAppliedCopy = true;
        return replacement;
    }

    /// <summary>
    /// Creates a concrete transformation whose replacement already carries every compatible
    /// enchantment from the original card. This keeps transform previews and the final transform
    /// result in sync for mods that use CardTransformation directly.
    /// </summary>
    public static CardTransformation CreateCompatibleTransformation(CardModel source, CardModel replacement)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(replacement);

        return new CardTransformation(source, CopyCompatibleEnchantments(source, replacement));
    }

    private sealed class TransformCopyState
    {
        public CardModel? Source { get; set; }

        public bool HasAppliedCopy { get; set; }
    }
}

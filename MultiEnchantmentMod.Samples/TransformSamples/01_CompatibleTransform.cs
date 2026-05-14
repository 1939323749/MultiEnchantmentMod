using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Samples.TransformSamples;

// Sample for MultiEnchantmentTransformApi.
//
// Goal: show the right way to keep stacked enchantments alive when transforming a card into
// a different card.
//
// CopyCompatibleEnchantments returns the replacement unchanged for chaining; the new
// TryCopyCompatibleEnchantments variant returns the outcome enum so the caller can branch
// when (e.g.) the replacement has already been populated from a previous transform.
public static class SampleCompatibleTransform
{
    public static async Task ReplaceCardKeepingEnchantments(CardModel original, CardModel replacement)
    {
        TransformCopyOutcome outcome =
            MultiEnchantmentTransformApi.TryCopyCompatibleEnchantments(original, replacement, out CardModel _);

        switch (outcome)
        {
            case TransformCopyOutcome.Copied:
            case TransformCopyOutcome.AlreadyApplied:
                await CardCmd.Transform(original, replacement);
                break;

            case TransformCopyOutcome.DifferentSource:
                // Replacement was already populated from another card — reset and retry, or
                // pick a different replacement instance. The sample chooses to bail out and
                // log; production code might construct a fresh replacement here.
                MultiEnchantmentMod.Logger.Warn(
                    "[Samples] Refusing to chain transforms onto a replacement that already carried a different source's enchantments.");
                break;

            case TransformCopyOutcome.SourceIsTarget:
                MultiEnchantmentMod.Logger.Warn(
                    "[Samples] ReplaceCardKeepingEnchantments called with identical source and replacement; skipping.");
                break;
        }
    }

    /// <summary>
    /// Variant for callers that want a preview-friendly CardTransformation. The helper
    /// pre-copies compatible enchantments onto the replacement so the preview matches the
    /// final transform.
    /// </summary>
    public static CardTransformation CreatePreviewableTransformation(CardModel source, CardModel replacement)
    {
        return MultiEnchantmentTransformApi.CreateCompatibleTransformation(source, replacement);
    }
}

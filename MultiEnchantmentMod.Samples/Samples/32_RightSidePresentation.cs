using System;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier B — right-side presentation.
//
// EnchantmentPresentationStyle.RightAligned moves a badge into a right-side column, mirrored across
// the card's vertical centerline so the first right badge sits symmetric to the energy / star-cost
// icon, with the rest stacking downward. Only the badge backing is flipped horizontally; the icon
// and amount label stay upright. The right column ignores the vanilla no-star-cost vertical shift
// (NCard's 45px star-label offset), so it stays put regardless of the card's star cost. Left- and
// right-aligned badges form two independent downward-stacking columns, so a single card can carry
// both at once.

// ── Part 1 — a real (gameplay) enchantment rendered on the right, WITH a mirrored badge backing.
//   Auto-discovered by the assembly scan (Tier A/B), same as every other attribute-tagged sample.

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SampleRightSideBadge : EnchantmentModel
{
    public override bool ShowAmount => true; // draw the amount number inside the badge
}

[EnchantmentPresentation(HasPresentationStyle = true)]
public sealed class SampleRightSideBadgeDefinition : EnchantmentDefinition<SampleRightSideBadge>
{
    public override EnchantmentPresentationStyle PresentationStyle => new()
    {
        RightAligned = true,     // right column; first slot is symmetric to the energy icon
        ShowBadgeBacking = true, // keep the backing → its bottom layer gets mirrored horizontally
        // The icon is never flipped, so any asymmetric art stays readable. Fine-tune with
        // IconScale / IconOffset if the mirrored backing needs the icon nudged.
    };
}

// ── Part 2 — a display-only extra icon pinned to the right. Markers default to no backing
//   (ExtraIconPresentation.Default), so nothing is mirrored — RightAligned only decides which
//   column the icon lands in. Targeting StrikeIronclad (which sample 31 already marks on the left)
//   demonstrates the two independent columns coexisting on one card.
//
//   Display providers can't be expressed as attributes alone, so Install() is called explicitly
//   from SampleRegistration.Initialize.

public sealed class SampleRightSideMarker : ExtraIconEnchantmentModel
{
}

public static class SampleRightSideMarkerRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        // Borrow an existing enchantment's texture from ModelDb (never `new` a model). A real mod
        // would pass its own GD.Load<CompressedTexture2D>("res://...png").
        _registration ??= MultiEnchantmentApi.RegisterExtraIcon<SampleRightSideMarker>(
            appliesTo: card => card is StrikeIronclad,
            presentationStyle: ExtraIconPresentation.Default with { RightAligned = true },
            icon: ModelDb.Enchantment<Sharp>().Icon);
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

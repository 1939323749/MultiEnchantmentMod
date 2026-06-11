using System;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Display-only marker sample.
//
// MarkerEnchantmentModel is for marker-style enchantments that primarily exist as card UI
// icons. It defaults to no amount label, no extra card text, hidden battle-history output, no
// vanilla badge backing, hidden-when-disabled, and high display priority.
//
// The marker type is just a key here — it needs no body. You do NOT (and cannot) override Icon:
// EnchantmentModel.Icon is non-virtual. Supply the image instead via the RegisterMarker `icon`
// parameter (or MarkerDisplay.Icon on the provider path), or ship a texture at the model's icon
// path. A marker with no resolvable icon is skipped — there is no placeholder.
public sealed class SampleLibraryMarker : MarkerEnchantmentModel
{
}

public static class SampleLibraryMarkerRegistration
{
    private static IDisposable? _registration;

    public static void Install()
    {
        // Match by card TYPE, not a string id: each character's Strike is a distinct type
        // (StrikeIronclad / StrikeSilent / StrikeDefect / ...), so this targets the Ironclad's
        // Strike specifically — including in the compendium, which renders cards through
        // NCard.UpdateVisuals like everywhere else. Do not gate on IsCombatCard or it would be
        // hidden outside combat.
        //
        // For the icon we borrow an existing enchantment's texture (fetched from ModelDb — never
        // `new` a model). A real mod would pass its own GD.Load<CompressedTexture2D>("res://...png").
        _registration ??= MultiEnchantmentApi.RegisterMarker<SampleLibraryMarker>(
            appliesTo: card => card is StrikeIronclad,
            presentationStyle: MarkerPresentation.Default with { IconScale = 1.25f },
            icon: ModelDb.Enchantment<Sharp>().Icon);
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}

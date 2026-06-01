using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Context passed to static extra-icon providers when a card UI is being refreshed.
/// </summary>
public sealed record ExtraIconDisplayContext(
    CardModel Card,
    bool HasLiveEnchantment,
    bool IsCombatCard,
    bool IsPreviewCard);

public delegate bool ExtraIconDisplayPredicate(ExtraIconDisplayContext context);

using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Context passed to static marker providers when a card UI is being refreshed.
/// </summary>
public sealed record MarkerDisplayContext(
    CardModel Card,
    bool HasLiveEnchantment,
    bool IsCombatCard,
    bool IsPreviewCard);

public delegate bool MarkerDisplayPredicate(MarkerDisplayContext context);

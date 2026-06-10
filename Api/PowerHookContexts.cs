using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Context for one <c>Hook.ModifyPowerAmountGiven</c> evaluation where the enchanted card is the
/// power's <c>cardSource</c>. Bundled so <see cref="PowerAmountGivenContribution"/> handlers can
/// branch on the power type / target without taking five parameters.
/// </summary>
/// <param name="Power">The power being applied (canonical or mutable instance, as vanilla passes it).</param>
/// <param name="Giver">The creature giving the power (vanilla guarantees non-null on the given path).</param>
/// <param name="Target">The creature about to receive the power. Null for untargeted previews.</param>
/// <param name="Card">The card whose play is applying the power — the card carrying the contributing enchantment.</param>
public sealed record PowerGivenContext(
    PowerModel Power,
    Creature Giver,
    Creature? Target,
    CardModel Card);

/// <summary>
/// Context for the <c>OnCardAppliedPower</c> lifecycle bridge. Fired after
/// <c>Hook.AfterPowerAmountChanged</c> completes for a power application whose
/// <c>cardSource</c> is the enchanted card.
/// </summary>
/// <param name="Power">The power instance that changed. <c>Power.Owner</c> equals <paramref name="Target"/>.</param>
/// <param name="Amount">The delta actually applied (post all modifiers). Never zero — vanilla skips the hook for no-ops.</param>
/// <param name="Applier">The creature that applied the power, when known.</param>
/// <param name="Target">The creature now carrying the power.</param>
public sealed record PowerAppliedContext(
    PowerModel Power,
    decimal Amount,
    Creature? Applier,
    Creature Target);

/// <summary>
/// One contribution to the amount of a power the enchanted card gives. Contributions fold over
/// the running amount after vanilla's additive/multiplicative listener pipeline — return
/// <c>currentAmount + snapshot.MergedAmount</c> for "+1 per stack" semantics, or any clamp /
/// piecewise logic. Multiple registrations compose in registration order.
/// </summary>
public delegate decimal PowerAmountGivenContribution(
    EnchantmentStackSnapshot snapshot,
    PowerGivenContext context,
    decimal currentAmount);

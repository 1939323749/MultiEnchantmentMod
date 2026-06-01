using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Context passed to a stack-aware <c>OnPlay</c> handler. The handler is invoked once per
/// enchantment type and reads <see cref="EnchantmentStackSnapshot.ActiveTotalAmount"/> to decide
/// how strongly the effect should apply.
/// </summary>
public sealed record StackedOnPlayContext(
    EnchantmentStackSnapshot Snapshot,
    PlayerChoiceContext ChoiceContext,
    CardPlay? CardPlay);

public sealed record StackedBeforeCardPlayedContext(
    EnchantmentStackSnapshot Snapshot,
    CardPlay CardPlay);

public sealed record StackedAfterCardPlayedContext(
    EnchantmentStackSnapshot Snapshot,
    PlayerChoiceContext ChoiceContext,
    CardPlay CardPlay);

/// <summary>
/// Context passed to a stack-aware "another enchantment was just attached to this card" hook.
/// <see cref="ChoiceContext"/> is null for synchronous application paths such as
/// <see cref="MultiEnchantmentApi.Enchant(CardModel, EnchantmentModel, decimal, EnchantmentScope?)"/>.
/// Use <see cref="MultiEnchantmentApi.EnchantAsync(PlayerChoiceContext?, CardModel, EnchantmentModel, decimal, EnchantmentScope?)"/>
/// when the downstream handler needs to run commands or auto-play cards immediately.
/// </summary>
/// <remarks>
/// On synchronous enchant paths this hook is dispatched by blocking on the returned task
/// (<c>.GetAwaiter().GetResult()</c>), so handlers <b>must not</b> await real game commands
/// (auto-play, card selection, power application) — doing so can stall or deadlock the game thread.
/// Keep handlers to pure state updates here; route imperative / command-issuing logic through
/// <see cref="MultiEnchantmentApi.AfterCardEnchanted"/> together with
/// <see cref="MultiEnchantmentApi.EnchantAsync"/> instead.
/// </remarks>
public sealed record StackedAfterSiblingAppliedContext(
    EnchantmentStackSnapshot Snapshot,
    PlayerChoiceContext? ChoiceContext,
    CardModel Card,
    EnchantmentModel NewSibling);

public sealed record StackedAfterCardDrawnContext(
    EnchantmentStackSnapshot Snapshot,
    PlayerChoiceContext ChoiceContext,
    CardModel DrawnCard,
    bool FromHandDraw);

/// <summary>
/// Context passed to a stack-aware cleanup hook. <see cref="ChoiceContext"/> is currently null
/// in the vanilla bridge, so use this callback for synchronous cleanup / state reset only.
/// </summary>
public sealed record StackedBeforeFlushContext(
    EnchantmentStackSnapshot Snapshot,
    PlayerChoiceContext? ChoiceContext,
    Player Player);

/// <summary>
/// Context passed to a stack-aware damage-result hook.
/// </summary>
public sealed record StackedAfterDamageGivenContext(
    EnchantmentStackSnapshot Snapshot,
    PlayerChoiceContext ChoiceContext,
    Creature? Dealer,
    DamageResult Result,
    ValueProp Props,
    Creature Target,
    CardModel? CardSource);

using System.Threading.Tasks;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

public delegate Task StackedOnPlayHandler(StackedOnPlayContext context);
public delegate Task StackedBeforeCardPlayedHandler(StackedBeforeCardPlayedContext context);
public delegate Task StackedAfterCardPlayedHandler(StackedAfterCardPlayedContext context);
public delegate Task StackedAfterSiblingAppliedHandler(StackedAfterSiblingAppliedContext context);
public delegate Task StackedAfterCardDrawnHandler(StackedAfterCardDrawnContext context);
public delegate Task StackedAfterAnyCardDrawnHandler(StackedAfterCardDrawnContext context);
public delegate Task StackedBeforeFlushHandler(StackedBeforeFlushContext context);
public delegate Task StackedAfterDamageGivenHandler(StackedAfterDamageGivenContext context);

/// <summary>
/// Fold function for combat energy-cost contributions. Receives the current stack snapshot and
/// the running combat cost, and returns the next running cost.
/// </summary>
public delegate decimal EnergyCostContribution(EnchantmentStackSnapshot snapshot, decimal currentCost);

/// <summary>
/// Fold function for card play-count contributions. The value is the total play count used by
/// <c>Hook.ModifyCardPlayCount</c>, not the extra replay count returned by
/// <c>CardModel.GetEnchantedReplayCount</c>.
/// </summary>
public delegate int CardPlayCountContribution(EnchantmentStackSnapshot snapshot, int currentPlayCount);

/// <summary>
/// Fold function for hand-draw contributions. Receives the current stack snapshot and the running
/// hand-draw count (player-level, called once per turn start across all player combat cards),
/// and returns the next running count.
/// </summary>
public delegate decimal HandDrawContribution(EnchantmentStackSnapshot snapshot, decimal currentHandDraw);

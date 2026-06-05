using System.Threading.Tasks;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Context passed after a card has successfully finished a fresh enchantment application.
/// The applied enchantment is already live on <see cref="Card"/>, so command handlers may safely
/// autoplay the card from async application paths and observe the newly applied enchantment
/// during that play.
/// </summary>
/// <remarks>
/// This notification is only raised by the async application paths
/// (<c>MultiEnchantmentApi.EnchantAsync</c> / <c>CopyEnchantmentAsync</c>). The synchronous
/// <c>Enchant</c> / <c>CopyEnchantment</c> overloads and vanilla enchant paths do not raise it,
/// because handlers are awaited and may issue game commands.
/// </remarks>
/// <param name="CascadeDepth">
/// Nesting depth of this notification. <c>0</c> for a top-level application (the player directly
/// enchanted a card); <c>&gt; 0</c> when the application was triggered from inside another
/// <see cref="AfterCardEnchantedHandler"/>. Cascade-style cards (e.g. "whenever you enchant, also
/// enchant a random hand card") should early-out with <c>if (ctx.CascadeDepth &gt; 0) return;</c>
/// to avoid unbounded recursion.
/// </param>
public sealed record AfterCardEnchantedContext(
    PlayerChoiceContext? ChoiceContext,
    CardModel Card,
    EnchantmentModel AppliedEnchantment,
    EnchantmentModel RequestedEnchantment,
    int AppliedAmount,
    EnchantmentScope? ScopeOverride,
    int CascadeDepth = 0);

public delegate Task AfterCardEnchantedHandler(AfterCardEnchantedContext context);

/// <summary>
/// Context passed before an enchantment is applied to a card. Handlers may cancel the application
/// or modify the amount. Only fired on async paths (<see cref="MultiEnchantmentApi.EnchantAsync"/>
/// / <see cref="MultiEnchantmentApi.CopyEnchantmentAsync"/>), matching the
/// <see cref="AfterCardEnchantedHandler"/> contract.
/// </summary>
public sealed record BeforeCardEnchantedContext(
    PlayerChoiceContext? ChoiceContext,
    CardModel Card,
    EnchantmentModel Enchantment,
    int RequestedAmount,
    EnchantmentScope? ScopeOverride,
    int CascadeDepth = 0)
{
    /// <summary>Set to <c>true</c> to cancel the enchantment application.</summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// Set to a different value to override the applied amount. Initialized to
    /// <see cref="RequestedAmount"/>. Values less than or equal to zero cancel the application.
    /// </summary>
    public int ModifiedAmount { get; set; } = RequestedAmount;
}

public delegate Task BeforeCardEnchantedHandler(BeforeCardEnchantedContext context);

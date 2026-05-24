namespace MultiEnchantmentMod.Api;

/// <summary>
/// Controls how an enchantment application appears in the per-floor battle history tooltip.
/// </summary>
public enum HistoryDisplayMode
{
    /// <summary>
    /// Auto-detect based on scope. Non-permanent scopes (<c>UntilCombatEnds</c>,
    /// <c>UntilTurnEnds</c>, <c>LingerForTurns</c>, <c>MaxActivations</c>) default to
    /// <see cref="Hidden"/>; permanent scopes (<c>Permanent</c>, <c>ConditionalActive</c>,
    /// <c>RemoveWhen</c>) default to <see cref="InRewards"/>.
    /// </summary>
    Auto,

    /// <summary>Show in the rewards section (vanilla behavior).</summary>
    InRewards,

    /// <summary>Don't show in the battle history at all.</summary>
    Hidden,

    /// <summary>Show in the actions/events section instead of rewards.</summary>
    InActions,

    /// <summary>
    /// Show in a custom group section with a custom header. Requires setting a group header
    /// via <see cref="IEnchantmentRegistration.HistoryDisplay(HistoryDisplayMode, string)"/>
    /// or <see cref="EnchantmentDefinition{TEnchantment}.HistoryGroupHeader"/>.
    /// </summary>
    CustomGroup,
}

/// <summary>
/// Delegate for producing custom battle-history text for an enchantment application.
/// Receives the card title and enchantment title; returns the full formatted line to display.
/// Return <c>null</c> to fall back to the default format.
/// </summary>
public delegate string? HistoryTextFormatter(string cardTitle, string enchantmentTitle);

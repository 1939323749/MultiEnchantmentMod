using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;
using HookExecutionMode = MultiEnchantmentMod.HookExecutionMode;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Fluent replacement for the 8-positional-argument
/// <see cref="LegacyExecutionPolicy"/> record constructor. Call
/// <see cref="MultiEnchantmentApi"/>'s <c>Register&lt;T&gt;().Execution(p => ...)</c> entry point
/// to obtain one; the registration applies the result via <see cref="Build"/>.
/// </summary>
public sealed class ExecutionPolicyBuilder
{
    private HookExecutionMode _all = HookExecutionMode.Default;
    private HookExecutionMode _onEnchant = HookExecutionMode.Default;
    private HookExecutionMode _onPlay = HookExecutionMode.Default;
    private HookExecutionMode _afterCardPlayed = HookExecutionMode.Default;
    private HookExecutionMode _afterCardDrawn = HookExecutionMode.Default;
    private HookExecutionMode _afterPlayerTurnStart = HookExecutionMode.Default;
    private HookExecutionMode _beforePlayPhaseStart = HookExecutionMode.Default;
    private HookExecutionMode _beforeFlush = HookExecutionMode.Default;

    /// <summary>
    /// Sets the default execution mode for every hook that is not given a per-hook value.
    /// </summary>
    public ExecutionPolicyBuilder All(HookExecutionMode mode) { _all = mode; return this; }

    public ExecutionPolicyBuilder OnEnchant(HookExecutionMode mode) { _onEnchant = mode; return this; }
    public ExecutionPolicyBuilder OnPlay(HookExecutionMode mode) { _onPlay = mode; return this; }
    public ExecutionPolicyBuilder AfterCardPlayed(HookExecutionMode mode) { _afterCardPlayed = mode; return this; }
    public ExecutionPolicyBuilder AfterCardDrawn(HookExecutionMode mode) { _afterCardDrawn = mode; return this; }
    public ExecutionPolicyBuilder AfterPlayerTurnStart(HookExecutionMode mode) { _afterPlayerTurnStart = mode; return this; }
    public ExecutionPolicyBuilder BeforePlayPhaseStart(HookExecutionMode mode) { _beforePlayPhaseStart = mode; return this; }
    public ExecutionPolicyBuilder BeforeFlush(HookExecutionMode mode) { _beforeFlush = mode; return this; }

    /// <summary>
    /// Materializes the configured modes into the legacy
    /// <see cref="LegacyExecutionPolicy"/> record used internally.
    /// </summary>
    public LegacyExecutionPolicy Build() => new(
        DefaultMode: _all,
        OnEnchant: _onEnchant,
        OnPlay: _onPlay,
        AfterCardPlayed: _afterCardPlayed,
        AfterCardDrawn: _afterCardDrawn,
        AfterPlayerTurnStart: _afterPlayerTurnStart,
        BeforePlayPhaseStart: _beforePlayPhaseStart,
        BeforeFlush: _beforeFlush);
}

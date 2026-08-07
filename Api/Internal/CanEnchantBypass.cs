namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Ambient switch that drops the <c>CanEnchant</c> veto for the duration of a scope. Public entry
/// points: <see cref="MultiEnchantmentApi.IgnoreCanEnchant"/> and
/// <see cref="MultiEnchantmentApi.ForceEnchant"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the <b>veto</b> goes away. Everything downstream of the gate is untouched: stack behavior
/// resolution (<c>MergeAmount</c> vs. per-instance slots), <c>MaxInstances</c> and the overflow
/// policy, scope handling, ordering, deck-version mirroring, and every notification. So a forced
/// application still stacks and merges exactly like an ordinary one — this is not a back door around
/// the stacking model, it is a back door around "this enchantment refuses this card".
/// </para>
/// <para>
/// The motivating case is card <b>type</b>: vanilla <c>EnchantmentModel.CanEnchant</c> rejects
/// Status / Curse / Quest outright, so a mod that wants "enchant a Burn" cannot express it through
/// the normal pipeline at all. Authors' own <c>CanEnchant</c> overrides are bypassed too — the caller
/// is stating that this particular application is deliberate.
/// </para>
/// <para>
/// Evaluated with short-circuiting <b>before</b> the veto is consulted, so neither vanilla's rules nor
/// the author's override actually runs while the scope is active; an override with side effects or one
/// that throws cannot interfere with a forced application.
/// </para>
/// <para>
/// <see cref="System.Threading.AsyncLocal{T}"/> rather than a plain static so the flag survives awaits
/// (the async application path) and unwinds on exceptions. Nesting restores the previous value rather
/// than clearing, so an inner scope cannot re-arm the veto for an outer one.
/// </para>
/// </remarks>
internal static class CanEnchantBypass
{
    private static readonly AsyncLocal<bool> ActiveFlag = new();

    internal static bool IsActive => ActiveFlag.Value;

    internal static IDisposable Push()
    {
        bool previous = ActiveFlag.Value;
        ActiveFlag.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ActiveFlag.Value = previous;
        }
    }
}

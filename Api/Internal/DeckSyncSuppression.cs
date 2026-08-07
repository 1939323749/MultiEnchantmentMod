namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Ambient switch that turns off combat-card → <c>CardModel.DeckVersion</c> mirroring for the
/// duration of a scope. Public entry point: <see cref="MultiEnchantmentApi.SuppressDeckVersionSync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Motivating case — "temporarily swap this card's enchantment for the rest of the combat, then put
/// the original back". Both halves of that operation run against the <b>combat</b> card, which is
/// discarded when the combat ends; the deck version is the pre-combat baseline and should come out
/// of the whole affair untouched.
/// </para>
/// <para>
/// Without suppression it does not, because mirroring is gated on the enchantment's effective scope
/// (<c>IsScopeEffectivelyPermanent</c>) rather than on the caller's intent. An enchantment carrying an
/// explicit <c>PermanentScope</c> override mirrors its <b>removal</b> to the deck version — and for a
/// <c>MergeAmount</c> stack whose total is at or below the removed amount, that deletes the deck-version
/// enchantment outright. The subsequent restore mirrors an application back, so the deck version is
/// rebuilt from whatever the replacer happened to record instead of simply never being touched.
/// Any drift between those two numbers is a permanent, silent change to the player's deck.
/// </para>
/// <para>
/// <see cref="System.Threading.AsyncLocal{T}"/> rather than a plain static so the flag survives awaits
/// and unwinds correctly on exceptions; same reasoning as the telemetry application-source hint.
/// Nesting restores the previous value rather than clearing, so an inner scope cannot switch mirroring
/// back on for an outer one.
/// </para>
/// </remarks>
internal static class DeckSyncSuppression
{
    private static readonly AsyncLocal<bool> SuppressedFlag = new();

    internal static bool IsSuppressed => SuppressedFlag.Value;

    internal static IDisposable Push()
    {
        bool previous = SuppressedFlag.Value;
        SuppressedFlag.Value = true;
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
            SuppressedFlag.Value = previous;
        }
    }
}

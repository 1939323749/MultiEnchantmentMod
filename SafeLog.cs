using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace MultiEnchantmentMod;

internal static class SafeLog
{
    internal static string GetSafeCardId(CardModel? card)
    {
        if (card == null)
        {
            return "null";
        }

        try
        {
            return card.Id?.ToString() ?? "unknown";
        }
        catch
        {
            return card.GetType().FullName ?? card.GetType().Name;
        }
    }

    internal static string GetSafeEnchantmentId(EnchantmentModel? enchantment)
    {
        if (enchantment == null)
        {
            return "null";
        }

        try
        {
            return enchantment.Id?.ToString() ?? "unknown";
        }
        catch
        {
            return enchantment.GetType().FullName ?? enchantment.GetType().Name;
        }
    }

    internal static string GetSafePlayerId(Player? player)
    {
        if (player == null)
        {
            return "null";
        }

        try
        {
            return player.NetId.ToString();
        }
        catch
        {
            return player.GetType().FullName ?? player.GetType().Name;
        }
    }

    internal static string GetSafeModelId(object? id)
    {
        try
        {
            return id?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}

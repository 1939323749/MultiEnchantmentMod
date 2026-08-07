using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Registry behind <see cref="MultiEnchantmentApi.SetCardEnchantmentLimit(ModelId, int?)"/> and
/// friends: how many enchantments a given <b>card</b> may carry at once.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <c>StackDefinition.MaxInstances</c>, which caps one enchantment <b>type</b> on a
/// card. This caps the card's total slot count across all types, which is what "this card may hold
/// two enchantments, every other card only one" needs.
/// </para>
/// <para>
/// Resolution order, most specific first: ModelId → card CLR type (walking up the base chain) →
/// dynamic rules, newest first → global default. A <c>null</c> at any level means "no opinion, keep
/// looking"; to express "explicitly unlimited" for one card while a stricter default is in force,
/// register <see cref="int.MaxValue"/>.
/// </para>
/// <para>
/// Registrations are process-global and expected during mod init, so this is a plain dictionary
/// guarded by a lock rather than anything fancier — reads happen on the game thread during
/// <c>CanEnchant</c>, writes essentially never after startup.
/// </para>
/// </remarks>
internal static class CardEnchantmentLimits
{
    private static readonly object Sync = new();
    private static readonly Dictionary<ModelId, int?> ById = new();
    private static readonly Dictionary<Type, int?> ByType = new();
    private static readonly List<Func<CardModel, int?>> Rules = new();
    private static int? _default;

    /// <summary><c>true</c> when nothing has been registered — lets the hot path bail immediately.</summary>
    internal static bool IsEmpty
    {
        get
        {
            lock (Sync)
            {
                return _default is null && ById.Count == 0 && ByType.Count == 0 && Rules.Count == 0;
            }
        }
    }

    internal static void SetDefault(int? max)
    {
        lock (Sync)
        {
            _default = max;
        }
    }

    internal static void Set(ModelId cardId, int? max)
    {
        lock (Sync)
        {
            if (max is null)
            {
                ById.Remove(cardId);
            }
            else
            {
                ById[cardId] = max;
            }
        }
    }

    internal static void Set(Type cardType, int? max)
    {
        lock (Sync)
        {
            if (max is null)
            {
                ByType.Remove(cardType);
            }
            else
            {
                ByType[cardType] = max;
            }
        }
    }

    internal static void AddRule(Func<CardModel, int?> rule)
    {
        lock (Sync)
        {
            Rules.Add(rule);
        }
    }

    internal static void Clear()
    {
        lock (Sync)
        {
            ById.Clear();
            ByType.Clear();
            Rules.Clear();
            _default = null;
        }
    }

    /// <summary>The cap in force for <paramref name="card"/>, or <c>null</c> when uncapped.</summary>
    internal static int? Resolve(CardModel? card)
    {
        if (card == null)
        {
            return null;
        }

        lock (Sync)
        {
            if (ById.TryGetValue(card.Id, out int? byId))
            {
                return byId;
            }

            // Walk the base chain so a mod can cap a whole family by its shared base card class.
            for (Type? t = card.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                if (ByType.TryGetValue(t, out int? byType))
                {
                    return byType;
                }
            }

            // Newest rule wins, so a later registration can override an earlier one.
            for (int i = Rules.Count - 1; i >= 0; i--)
            {
                int? fromRule = SafeInvoke(Rules[i], card);
                if (fromRule is not null)
                {
                    return fromRule;
                }
            }

            return _default;
        }
    }

    /// <summary>
    /// A throwing rule must not take the enchant pipeline down with it: log once and treat the rule
    /// as having no opinion. Same posture as the author-<c>CanEnchant</c> probe.
    /// </summary>
    private static readonly HashSet<Func<CardModel, int?>> FaultedRules = new();

    private static int? SafeInvoke(Func<CardModel, int?> rule, CardModel card)
    {
        try
        {
            return rule(card);
        }
        catch (Exception ex)
        {
            if (FaultedRules.Add(rule))
            {
                MultiEnchantmentMod.Logger.Warn(
                    "[MultiEnchantment] A card-enchantment-limit rule threw and will be treated as " +
                    $"'no opinion' from now on: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }
    }
}

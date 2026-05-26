using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

internal static partial class MultiEnchantmentSupport
{
    public static bool HaveSameEnchantments(CardModel? left, CardModel? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        List<OrderedEnchantmentEntry> leftEnchantments = GetOrderedEnchantmentEntries(left);
        List<OrderedEnchantmentEntry> rightEnchantments = GetOrderedEnchantmentEntries(right);
        if (leftEnchantments.Count != rightEnchantments.Count)
        {
            return false;
        }

        for (int i = 0; i < leftEnchantments.Count; i++)
        {
            OrderedEnchantmentEntry leftEnchantment = leftEnchantments[i];
            OrderedEnchantmentEntry rightEnchantment = rightEnchantments[i];
            if (!HaveSameEnchantmentState(leftEnchantment, rightEnchantment))
            {
                return false;
            }
        }

        return true;
    }

    public static int GetEnchantmentsHashCode(CardModel? card)
    {
        HashCode hash = new();
        foreach (OrderedEnchantmentEntry enchantment in GetOrderedEnchantmentEntries(card))
        {
            AddEnchantmentStateToHash(ref hash, enchantment);
        }

        return hash.ToHashCode();
    }

    private static bool HaveSameEnchantmentState(OrderedEnchantmentEntry left, OrderedEnchantmentEntry right)
    {
        // Multiplayer card grouping must compare gameplay-relevant state, not just model ID.
        // Status affects behavior immediately, and Props can carry per-enchantment saved state.
        if (!left.Enchantment.Id.Equals(right.Enchantment.Id) ||
            left.EffectiveAmount != right.EffectiveAmount ||
            left.Enchantment.Status != right.Enchantment.Status)
        {
            return false;
        }

        return SavedPropertiesComparer.HaveSame(left.Enchantment.Props, right.Enchantment.Props);
    }

    private static void AddEnchantmentStateToHash(ref HashCode hash, OrderedEnchantmentEntry enchantment)
    {
        hash.Add(enchantment.Enchantment.Id);
        hash.Add(enchantment.EffectiveAmount);
        hash.Add(enchantment.Enchantment.Status);
        hash.Add(SavedPropertiesComparer.GetHashCode(enchantment.Enchantment.Props));
    }
}

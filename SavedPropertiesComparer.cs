using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MultiEnchantmentMod;

internal static class SavedPropertiesComparer
{
    public static bool HaveSame(SavedProperties? left, SavedProperties? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return HaveSameSavedPropertyList(left.ints, right.ints, static (l, r) => l == r) &&
               HaveSameSavedPropertyList(left.bools, right.bools, static (l, r) => l == r) &&
               HaveSameSavedPropertyList(left.strings, right.strings, HaveSameString) &&
               HaveSameSavedPropertyList(left.intArrays, right.intArrays, HaveSameIntArray) &&
               HaveSameSavedPropertyList(left.modelIds, right.modelIds, static (l, r) => Equals(l, r)) &&
               HaveSameSavedPropertyList(left.cards, right.cards, HaveSameSerializableCard) &&
               HaveSameSavedPropertyList(left.cardArrays, right.cardArrays, HaveSameSerializableCardArray);
    }

    public static int GetHashCode(SavedProperties? props)
    {
        HashCode hash = new();
        hash.Add(props != null);
        if (props == null)
        {
            return hash.ToHashCode();
        }

        AddSavedPropertyListToHash(ref hash, props.ints, static value => value);
        AddSavedPropertyListToHash(ref hash, props.bools, static value => value ? 1 : 0);
        AddSavedPropertyListToHash(ref hash, props.strings, GetStringHashCode);
        AddSavedPropertyListToHash(ref hash, props.intArrays, GetIntArrayHashCode);
        AddSavedPropertyListToHash(ref hash, props.modelIds, GetModelIdHashCode);
        AddSavedPropertyListToHash(ref hash, props.cards, GetSerializableCardHashCode);
        AddSavedPropertyListToHash(ref hash, props.cardArrays, GetSerializableCardArrayHashCode);
        return hash.ToHashCode();
    }

    private static bool HaveSameSavedPropertyList<T>(
        List<SavedProperties.SavedProperty<T>>? left,
        List<SavedProperties.SavedProperty<T>>? right,
        Func<T, T, bool> valueComparer)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            SavedProperties.SavedProperty<T> leftProperty = left[i];
            SavedProperties.SavedProperty<T> rightProperty = right[i];
            if (!string.Equals(leftProperty.name, rightProperty.name, StringComparison.Ordinal) ||
                !valueComparer(leftProperty.value, rightProperty.value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveSameString(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static bool HaveSameIntArray(int[]? left, int[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HaveSameSerializableCard(SerializableCard? left, SerializableCard? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return Equals(left.Id, right.Id) &&
               left.CurrentUpgradeLevel == right.CurrentUpgradeLevel &&
               HaveSameSerializableEnchantment(left.Enchantment, right.Enchantment) &&
               HaveSame(left.Props, right.Props) &&
               left.FloorAddedToDeck == right.FloorAddedToDeck;
    }

    private static bool HaveSameSerializableEnchantment(SerializableEnchantment? left, SerializableEnchantment? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        return Equals(left.Id, right.Id) &&
               left.Amount == right.Amount &&
               HaveSame(left.Props, right.Props);
    }

    private static bool HaveSameSerializableCardArray(SerializableCard[]? left, SerializableCard[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (!HaveSameSerializableCard(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddSavedPropertyListToHash<T>(
        ref HashCode hash,
        List<SavedProperties.SavedProperty<T>>? properties,
        Func<T, int> valueHasher)
    {
        hash.Add(properties != null);
        if (properties == null)
        {
            return;
        }

        hash.Add(properties.Count);
        foreach (SavedProperties.SavedProperty<T> property in properties)
        {
            hash.Add(GetStringHashCode(property.name));
            hash.Add(valueHasher(property.value));
        }
    }

    private static int GetStringHashCode(string? value)
    {
        return value == null ? 0 : StringComparer.Ordinal.GetHashCode(value);
    }

    private static int GetIntArrayHashCode(int[]? values)
    {
        HashCode hash = new();
        hash.Add(values != null);
        if (values == null)
        {
            return hash.ToHashCode();
        }

        hash.Add(values.Length);
        foreach (int value in values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static int GetModelIdHashCode(ModelId? value)
    {
        HashCode hash = new();
        hash.Add(value != null);
        if (value == null)
        {
            return hash.ToHashCode();
        }

        hash.Add(GetStringHashCode(value.Category));
        hash.Add(GetStringHashCode(value.Entry));
        return hash.ToHashCode();
    }

    private static int GetSerializableCardHashCode(SerializableCard? value)
    {
        HashCode hash = new();
        hash.Add(value != null);
        if (value == null)
        {
            return hash.ToHashCode();
        }

        hash.Add(GetModelIdHashCode(value.Id));
        hash.Add(value.CurrentUpgradeLevel);
        hash.Add(GetSerializableEnchantmentHashCode(value.Enchantment));
        hash.Add(GetHashCode(value.Props));
        hash.Add(value.FloorAddedToDeck);
        return hash.ToHashCode();
    }

    private static int GetSerializableEnchantmentHashCode(SerializableEnchantment? value)
    {
        HashCode hash = new();
        hash.Add(value != null);
        if (value == null)
        {
            return hash.ToHashCode();
        }

        hash.Add(GetModelIdHashCode(value.Id));
        hash.Add(value.Amount);
        hash.Add(GetHashCode(value.Props));
        return hash.ToHashCode();
    }

    private static int GetSerializableCardArrayHashCode(SerializableCard[]? values)
    {
        HashCode hash = new();
        hash.Add(values != null);
        if (values == null)
        {
            return hash.ToHashCode();
        }

        hash.Add(values.Length);
        foreach (SerializableCard value in values)
        {
            hash.Add(GetSerializableCardHashCode(value));
        }

        return hash.ToHashCode();
    }
}

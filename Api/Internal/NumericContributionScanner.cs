using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api.Internal;

internal static class NumericContributionScanner
{
    private static readonly ConcurrentDictionary<Type, EnergyCostContribution[]> EnergyCache = new();
    private static readonly ConcurrentDictionary<Type, CardPlayCountContribution[]> PlayCountCache = new();

    public static IEnumerable<EnergyCostContribution> ScanEnergyCost(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return EnergyCache.GetOrAdd(type, BuildEnergyContributionsFor);
    }

    public static IEnumerable<CardPlayCountContribution> ScanCardPlayCount(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return PlayCountCache.GetOrAdd(type, BuildPlayCountContributionsFor);
    }

    private static EnergyCostContribution[] BuildEnergyContributionsFor(Type type)
    {
        List<EnergyCostContribution> results = new();
        foreach (MethodInfo method in GetOrderedDeclaredMethods(type))
        {
            if (!method.GetCustomAttributes<ModifyEnergyCostAttribute>(inherit: false).Any())
            {
                continue;
            }

            ValidateSignatureOrThrow(type, method, typeof(decimal), typeof(decimal), nameof(ModifyEnergyCostAttribute));
            results.Add(BuildDecimalDelegate(method));
        }

        return results.ToArray();
    }

    private static CardPlayCountContribution[] BuildPlayCountContributionsFor(Type type)
    {
        List<CardPlayCountContribution> results = new();
        foreach (MethodInfo method in GetOrderedDeclaredMethods(type))
        {
            if (!method.GetCustomAttributes<ModifyCardPlayCountAttribute>(inherit: false).Any())
            {
                continue;
            }

            ValidateSignatureOrThrow(type, method, typeof(int), typeof(int), nameof(ModifyCardPlayCountAttribute));
            results.Add(BuildIntDelegate(method));
        }

        return results.ToArray();
    }

    private static MethodInfo[] GetOrderedDeclaredMethods(Type type)
    {
        MethodInfo[] methods = type.GetMethods(
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly);
        Array.Sort(methods, static (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
        return methods;
    }

    private static void ValidateSignatureOrThrow(
        Type type,
        MethodInfo method,
        Type returnType,
        Type valueType,
        string attributeName)
    {
        if (method.ReturnType != returnType)
        {
            throw new InvalidOperationException(
                $"[{type.FullName}.{method.Name}] [{attributeName}] requires return type {returnType.Name} " +
                $"but is {method.ReturnType.Name}.");
        }

        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(EnchantmentStackSnapshot) ||
            parameters[1].ParameterType != valueType)
        {
            throw new InvalidOperationException(
                $"[{type.FullName}.{method.Name}] [{attributeName}] requires parameters " +
                $"(EnchantmentStackSnapshot, {valueType.Name}); got " +
                $"({string.Join(", ", parameters.Select(p => p.ParameterType.Name))}).");
        }
    }

    private static EnergyCostContribution BuildDecimalDelegate(MethodInfo method)
    {
        if (method.IsStatic)
        {
            return (EnergyCostContribution)Delegate.CreateDelegate(typeof(EnergyCostContribution), method);
        }

        object? receiver = ModifyDynamicVarScanner.TryCreateReceiverForScanner(method.DeclaringType!);
        return (snapshot, current) => (decimal)method.Invoke(receiver, new object[] { snapshot, current })!;
    }

    private static CardPlayCountContribution BuildIntDelegate(MethodInfo method)
    {
        if (method.IsStatic)
        {
            return (CardPlayCountContribution)Delegate.CreateDelegate(typeof(CardPlayCountContribution), method);
        }

        object? receiver = ModifyDynamicVarScanner.TryCreateReceiverForScanner(method.DeclaringType!);
        return (snapshot, current) => (int)method.Invoke(receiver, new object[] { snapshot, current })!;
    }
}

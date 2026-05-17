using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Discovers <see cref="ModifyDynamicVarAttribute"/>-tagged methods on a type and turns them into
/// <see cref="DynamicVarContribution"/> records. Used by both
/// <see cref="EnchantmentDefinition{TEnchantment}"/> (for Tier B definition-class methods plus
/// methods on the enchantment model itself) and <see cref="AssemblyScanner"/> (for Tier A
/// attribute-only enchantments where no definition class exists).
/// </summary>
/// <remarks>
/// <para>
/// Method requirements:
/// <list type="bullet">
///   <item>Return type <see cref="decimal"/>.</item>
///   <item>Two parameters: <c>(EnchantmentStackSnapshot, decimal)</c>.</item>
///   <item>Instance or static methods both allowed. Instance methods are converted to open
///   delegates that ignore the receiver — callers should treat the method as pure-stateless over
///   the snapshot + current value pair.</item>
/// </list>
/// </para>
/// <para>
/// Per-method ordering within a single type is by <see cref="MemberInfo.MetadataToken"/>, which is
/// a stable proxy for source-code order. Multiple <see cref="ModifyDynamicVarAttribute"/> on the
/// same method are treated as independent contributions for different keys.
/// </para>
/// </remarks>
internal static class ModifyDynamicVarScanner
{
    private static readonly ConcurrentDictionary<Type, DynamicVarContribution[]> Cache = new();

    private static readonly Type[] RequiredSignature =
    {
        typeof(EnchantmentStackSnapshot),
        typeof(decimal),
    };

    /// <summary>
    /// Returns every <see cref="ModifyDynamicVarAttribute"/>-derived contribution on the given
    /// type. Results are cached per type; subsequent calls return the same array reference. Throws
    /// when a method carries the attribute but has a bad signature — fail-fast so authors notice
    /// during startup.
    /// </summary>
    public static IEnumerable<DynamicVarContribution> ScanType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Cache.GetOrAdd(type, BuildContributionsFor);
    }

    private static DynamicVarContribution[] BuildContributionsFor(Type type)
    {
        List<DynamicVarContribution> results = new();

        MethodInfo[] methods = type.GetMethods(
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly);

        // MetadataToken is a stable proxy for declaration order — see ECMA-335 II.22.26. We don't
        // sort across types here; the calling layer composes definition-class methods before
        // enchantment-model methods (see EnchantmentDefinition.DynamicVarContributions).
        Array.Sort(methods, static (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));

        foreach (MethodInfo method in methods)
        {
            ModifyDynamicVarAttribute[] attributes =
                method.GetCustomAttributes<ModifyDynamicVarAttribute>(inherit: false).ToArray();
            if (attributes.Length == 0)
            {
                continue;
            }

            ValidateSignatureOrThrow(type, method);

            Func<EnchantmentStackSnapshot, decimal, decimal> contributionFn = BuildDelegate(method);
            foreach (ModifyDynamicVarAttribute attribute in attributes)
            {
                results.Add(new DynamicVarContribution(attribute.VarKey, contributionFn));
            }
        }

        return results.ToArray();
    }

    private static void ValidateSignatureOrThrow(Type type, MethodInfo method)
    {
        if (method.ReturnType != typeof(decimal))
        {
            throw new InvalidOperationException(
                $"[{type.FullName}.{method.Name}] [ModifyDynamicVar] requires return type decimal " +
                $"but is {method.ReturnType.Name}.");
        }

        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != RequiredSignature.Length ||
            parameters[0].ParameterType != RequiredSignature[0] ||
            parameters[1].ParameterType != RequiredSignature[1])
        {
            throw new InvalidOperationException(
                $"[{type.FullName}.{method.Name}] [ModifyDynamicVar] requires parameters " +
                $"(EnchantmentStackSnapshot, decimal); got " +
                $"({string.Join(", ", parameters.Select(p => p.ParameterType.Name))}).");
        }
    }

    private static Func<EnchantmentStackSnapshot, decimal, decimal> BuildDelegate(MethodInfo method)
    {
        if (method.IsStatic)
        {
            return (Func<EnchantmentStackSnapshot, decimal, decimal>)Delegate.CreateDelegate(
                typeof(Func<EnchantmentStackSnapshot, decimal, decimal>),
                method);
        }

        // Instance method: produce an open delegate that supplies a stable placeholder receiver
        // when invoked. The placeholder is the type's default-constructed instance when possible;
        // otherwise (no default ctor), an uninitialized object via FormatterServices. Authors are
        // documented to treat the method as receiver-less.
        object? receiver = TryCreateReceiver(method.DeclaringType!);
        return (snapshot, current) => (decimal)method.Invoke(receiver, new object[] { snapshot, current })!;
    }

    private static object? TryCreateReceiver(Type type)
    {
        try
        {
            ConstructorInfo? defaultCtor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (defaultCtor != null)
            {
                return defaultCtor.Invoke(null);
            }

            // Uninitialized fallback: methods that touch state on `this` will likely NRE here,
            // which is the documented behavior — authors shouldn't touch `this`. Use the modern
            // RuntimeHelpers.GetUninitializedObject (FormatterServices.GetUninitializedObject was
            // marked obsolete in .NET 8).
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        }
        catch
        {
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod;

public enum EnchantmentStackBehavior
{
    DisallowDuplicate,
    MergeAmount,
    DuplicateInstance,
    ExistenceStack,
}

public interface IEnchantmentStackBehaviorProvider
{
    int Priority { get; }

    bool AppliesTo(Type enchantmentType);

    EnchantmentStackBehavior GetBehavior(Type enchantmentType);

    int GetVisualStackCount(EnchantmentModel enchantment);

    void ApplyMergedAmountDelta(EnchantmentModel enchantment, int addedAmount);

    void RefreshMergedState(EnchantmentModel enchantment);
}

public interface IEnchantmentKeywordSourceProvider
{
    int Priority { get; }

    bool AppliesTo(Type enchantmentType);

    IEnumerable<CardKeyword> GetTrackedKeywords(Type enchantmentType);

    int GetKeywordSourceAmount(EnchantmentModel enchantment, CardKeyword keyword);
}

public static class MultiEnchantmentStackApi
{
    private static readonly object DiscoveryLock = new();
    private static readonly List<IEnchantmentStackBehaviorProvider> Providers = new();
    private static readonly List<IEnchantmentKeywordSourceProvider> KeywordProviders = new();
    private static readonly HashSet<Type> AutoRegisteredProviderTypes = new();
    private static int _lastAssemblyCount = -1;

    public static IReadOnlyList<IEnchantmentStackBehaviorProvider> RegisteredProviders => Providers;
    public static IReadOnlyList<IEnchantmentKeywordSourceProvider> RegisteredKeywordProviders => KeywordProviders;

    public static void RegisterProvider(IEnchantmentStackBehaviorProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (Providers.Contains(provider) ||
            Providers.Any(existing => existing.GetType() == provider.GetType()))
        {
            return;
        }

        Providers.Add(provider);
        Providers.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
    }

    public static void UnregisterProvider(IEnchantmentStackBehaviorProvider provider)
    {
        if (provider == null)
        {
            return;
        }

        Providers.Remove(provider);
    }

    public static void RegisterKeywordProvider(IEnchantmentKeywordSourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (KeywordProviders.Contains(provider) ||
            KeywordProviders.Any(existing => existing.GetType() == provider.GetType()))
        {
            return;
        }

        KeywordProviders.Add(provider);
        KeywordProviders.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
    }

    public static void UnregisterKeywordProvider(IEnchantmentKeywordSourceProvider provider)
    {
        if (provider == null)
        {
            return;
        }

        KeywordProviders.Remove(provider);
    }

    internal static IEnchantmentStackBehaviorProvider? ResolveProvider(Type enchantmentType)
    {
        DiscoverProvidersFromLoadedAssemblies();

        foreach (IEnchantmentStackBehaviorProvider provider in Providers)
        {
            if (provider.AppliesTo(enchantmentType))
            {
                return provider;
            }
        }

        return null;
    }

    internal static IEnumerable<IEnchantmentKeywordSourceProvider> ResolveKeywordProviders(Type enchantmentType)
    {
        DiscoverProvidersFromLoadedAssemblies();
        return KeywordProviders.Where(provider => provider.AppliesTo(enchantmentType));
    }

    private static void DiscoverProvidersFromLoadedAssemblies()
    {
        lock (DiscoveryLock)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (assemblies.Length == _lastAssemblyCount)
            {
                return;
            }

            _lastAssemblyCount = assemblies.Length;
            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(static type => type != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    bool isStackProvider = typeof(IEnchantmentStackBehaviorProvider).IsAssignableFrom(type);
                    bool isKeywordProvider = typeof(IEnchantmentKeywordSourceProvider).IsAssignableFrom(type);
                    if (!isStackProvider && !isKeywordProvider)
                    {
                        continue;
                    }

                    if (type.IsAbstract ||
                        type.IsInterface ||
                        type.ContainsGenericParameters ||
                        !AutoRegisteredProviderTypes.Add(type))
                    {
                        continue;
                    }

                    bool needsStackRegistration = isStackProvider &&
                                                  !Providers.Any(existing => existing.GetType() == type);
                    bool needsKeywordRegistration = isKeywordProvider &&
                                                    !KeywordProviders.Any(existing => existing.GetType() == type);
                    if (!needsStackRegistration && !needsKeywordRegistration)
                    {
                        continue;
                    }

                    ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
                    if (ctor == null)
                    {
                        continue;
                    }

                    object? instance;
                    try
                    {
                        instance = ctor.Invoke(null);
                    }
                    catch (Exception ex)
                    {
                        MultiEnchantmentMod.Logger.Warn(
                            $"[StackApi] Failed to instantiate provider {type.FullName} from {assembly.GetName().Name}: {ex.GetBaseException().Message}");
                        continue;
                    }

                    if (needsStackRegistration && instance is IEnchantmentStackBehaviorProvider provider)
                    {
                        RegisterProvider(provider);
                    }

                    if (needsKeywordRegistration && instance is IEnchantmentKeywordSourceProvider keywordProvider)
                    {
                        RegisterKeywordProvider(keywordProvider);
                    }
                }
            }
        }
    }
}

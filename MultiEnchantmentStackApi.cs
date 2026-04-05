using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod;

public enum EnchantmentStackBehavior
{
    DisallowDuplicate,
    MergeAmount,
    DuplicateInstance,
    ExistenceStack,
}

public enum EnchantmentStatusAggregation
{
    None,
    Shared,
    PerInstance,
    PresenceOnly,
}

public enum EnchantmentHookKind
{
    OnEnchant,
    OnPlay,
    AfterCardPlayed,
    AfterCardDrawn,
    AfterPlayerTurnStart,
    BeforeFlush,
}

public enum HookExecutionMode
{
    Default,
    MergedTotal,
    PerVisualSlice,
    PerLiveInstance,
    FirstActiveInstanceOnly,
}

public sealed record EnchantmentStackDefinition(
    EnchantmentStackBehavior Behavior,
    EnchantmentStatusAggregation StatusAggregation);

public sealed record EnchantmentExecutionPolicy(
    HookExecutionMode DefaultMode = HookExecutionMode.Default,
    HookExecutionMode OnEnchant = HookExecutionMode.Default,
    HookExecutionMode OnPlay = HookExecutionMode.Default,
    HookExecutionMode AfterCardPlayed = HookExecutionMode.Default,
    HookExecutionMode AfterCardDrawn = HookExecutionMode.Default,
    HookExecutionMode AfterPlayerTurnStart = HookExecutionMode.Default,
    HookExecutionMode BeforeFlush = HookExecutionMode.Default)
{
    public HookExecutionMode GetExecutionMode(EnchantmentHookKind hookKind)
    {
        HookExecutionMode mode = hookKind switch
        {
            EnchantmentHookKind.OnEnchant => OnEnchant,
            EnchantmentHookKind.OnPlay => OnPlay,
            EnchantmentHookKind.AfterCardPlayed => AfterCardPlayed,
            EnchantmentHookKind.AfterCardDrawn => AfterCardDrawn,
            EnchantmentHookKind.AfterPlayerTurnStart => AfterPlayerTurnStart,
            EnchantmentHookKind.BeforeFlush => BeforeFlush,
            _ => HookExecutionMode.Default,
        };

        return mode == HookExecutionMode.Default
            ? DefaultMode
            : mode;
    }
}

public sealed record EnchantmentStackSlice(
    int Amount,
    EnchantmentStatus Status,
    int VisualOrder)
{
    public bool IsActive => Status != EnchantmentStatus.Disabled;
}

public sealed record EnchantmentStackSnapshot(
    CardModel? Card,
    Type EnchantmentType,
    EnchantmentModel AnchorInstance,
    EnchantmentStackDefinition Definition,
    int TotalAmount,
    IReadOnlyList<EnchantmentStackSlice> GameplaySlices,
    IReadOnlyList<EnchantmentStackSlice> VisualSlices,
    IReadOnlyList<EnchantmentModel> LiveInstances)
{
    public int ActiveInstanceCount => LiveInstances.Count(instance => instance.Status != EnchantmentStatus.Disabled);
    public int ActiveTotalAmount => GameplaySlices.Where(static slice => slice.IsActive).Sum(static slice => slice.Amount);
    public int ActiveGameplaySliceCount => GameplaySlices.Count(static slice => slice.IsActive);
    public int ActiveVisualSliceCount => VisualSlices.Count(static slice => slice.IsActive);

    public int GetExecutionCount(HookExecutionMode executionMode)
    {
        return executionMode switch
        {
            HookExecutionMode.MergedTotal => ActiveTotalAmount,
            HookExecutionMode.PerVisualSlice => ActiveVisualSliceCount,
            HookExecutionMode.PerLiveInstance => ActiveInstanceCount,
            HookExecutionMode.FirstActiveInstanceOnly => ActiveInstanceCount > 0 ? 1 : 0,
            _ => ActiveInstanceCount,
        };
    }
}

public interface IEnchantmentStackDefinitionProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    int Priority { get; }

    EnchantmentStackDefinition GetDefinition();
}

public interface IEnchantmentMergedStateProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    int Priority { get; }

    void ApplyMergedAmountDelta(TEnchantment enchantment, int addedAmount);

    void RefreshMergedState(TEnchantment enchantment);
}

public interface IEnchantmentExecutionPolicyProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    int Priority { get; }

    EnchantmentExecutionPolicy GetExecutionPolicy();
}

public interface IEnchantmentKeywordSourceProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    int Priority { get; }

    IEnumerable<CardKeyword> GetTrackedKeywords();

    int GetKeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword);
}

public interface IEnchantmentPresentationProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    int Priority { get; }

    IReadOnlyList<int>? GetVisualSliceAmounts(EnchantmentStackSnapshot snapshot);

    bool TryFormatExtraCardText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText);
}

public static class MultiEnchantmentStackApi
{
    private static readonly object DiscoveryLock = new();
    private static readonly HashSet<Type> AutoRegisteredProviderTypes = new();
    private static readonly List<IStackDefinitionProviderRegistration> DefinitionProviders = new();
    private static readonly List<IMergedStateProviderRegistration> MergedStateProviders = new();
    private static readonly List<IExecutionPolicyProviderRegistration> ExecutionPolicyProviders = new();
    private static readonly List<IKeywordSourceProviderRegistration> KeywordProviders = new();
    private static readonly List<IPresentationProviderRegistration> PresentationProviders = new();
    private static int _lastAssemblyCount = -1;

    public static void RegisterDefinitionProvider<TEnchantment>(
        IEnchantmentStackDefinitionProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            DefinitionProviders,
            new StackDefinitionProviderRegistration<TEnchantment>(provider),
            "definition");
    }

    public static void UnregisterDefinitionProvider<TEnchantment>(
        IEnchantmentStackDefinitionProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(DefinitionProviders, provider, typeof(TEnchantment));
    }

    public static void RegisterMergedStateProvider<TEnchantment>(
        IEnchantmentMergedStateProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            MergedStateProviders,
            new MergedStateProviderRegistration<TEnchantment>(provider),
            "merged-state");
    }

    public static void UnregisterMergedStateProvider<TEnchantment>(
        IEnchantmentMergedStateProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(MergedStateProviders, provider, typeof(TEnchantment));
    }

    public static void RegisterExecutionPolicyProvider<TEnchantment>(
        IEnchantmentExecutionPolicyProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            ExecutionPolicyProviders,
            new ExecutionPolicyProviderRegistration<TEnchantment>(provider),
            "execution-policy");
    }

    public static void UnregisterExecutionPolicyProvider<TEnchantment>(
        IEnchantmentExecutionPolicyProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(ExecutionPolicyProviders, provider, typeof(TEnchantment));
    }

    public static void RegisterKeywordProvider<TEnchantment>(
        IEnchantmentKeywordSourceProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterMultiProvider(
            KeywordProviders,
            new KeywordSourceProviderRegistration<TEnchantment>(provider),
            "keyword");
    }

    public static void UnregisterKeywordProvider<TEnchantment>(
        IEnchantmentKeywordSourceProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(KeywordProviders, provider, typeof(TEnchantment));
    }

    public static void RegisterPresentationProvider<TEnchantment>(
        IEnchantmentPresentationProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            PresentationProviders,
            new PresentationProviderRegistration<TEnchantment>(provider),
            "presentation");
    }

    public static void UnregisterPresentationProvider<TEnchantment>(
        IEnchantmentPresentationProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(PresentationProviders, provider, typeof(TEnchantment));
    }

    public static int RegisterCompanionProviders<TEnchantment>(Assembly? assembly = null)
        where TEnchantment : EnchantmentModel
    {
        return RegisterCompanionProviders(typeof(TEnchantment), assembly);
    }

    public static int RegisterCompanionProviders(Type enchantmentType, Assembly? assembly = null)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"Companion providers can only target {nameof(EnchantmentModel)} types.",
                nameof(enchantmentType));
        }

        Assembly targetAssembly = assembly ?? enchantmentType.Assembly;
        lock (DiscoveryLock)
        {
            return DiscoverProvidersFromAssembly(targetAssembly, enchantmentType);
        }
    }

    public static EnchantmentStackDefinition GetDefinition(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return MultiEnchantmentStackSupport.GetDefinition(enchantmentType);
    }

    public static EnchantmentExecutionPolicy GetExecutionPolicy(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return MultiEnchantmentStackSupport.GetExecutionPolicy(enchantmentType);
    }

    public static HookExecutionMode GetExecutionMode(Type enchantmentType, EnchantmentHookKind hookKind)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return MultiEnchantmentStackSupport.GetExecutionMode(enchantmentType, hookKind);
    }

    public static EnchantmentStackSnapshot GetSnapshot(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        return MultiEnchantmentStackSupport.GetSnapshot(enchantment);
    }

    public static IReadOnlyList<EnchantmentStackSnapshot> GetSnapshots(CardModel? card)
    {
        return MultiEnchantmentStackSupport.GetSnapshots(card);
    }

    public static int GetHookExecutionCount(EnchantmentModel enchantment, EnchantmentHookKind hookKind)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        EnchantmentStackSnapshot snapshot = GetSnapshot(enchantment);
        if (snapshot.Definition.Behavior == EnchantmentStackBehavior.MergeAmount &&
            !ReferenceEquals(snapshot.AnchorInstance, enchantment))
        {
            return 0;
        }

        return snapshot.GetExecutionCount(GetExecutionMode(snapshot.EnchantmentType, hookKind));
    }

    internal static IStackDefinitionProviderRegistration? ResolveDefinitionProvider(Type enchantmentType)
    {
        DiscoverProvidersFromLoadedAssemblies();
        return ResolveSingleProvider(DefinitionProviders, enchantmentType);
    }

    internal static IMergedStateProviderRegistration? ResolveMergedStateProvider(Type enchantmentType)
    {
        DiscoverProvidersFromLoadedAssemblies();
        return ResolveSingleProvider(MergedStateProviders, enchantmentType);
    }

    internal static IExecutionPolicyProviderRegistration? ResolveExecutionPolicyProvider(Type enchantmentType)
    {
        DiscoverProvidersFromLoadedAssemblies();
        return ResolveSingleProvider(ExecutionPolicyProviders, enchantmentType);
    }

    internal static IEnumerable<IKeywordSourceProviderRegistration> ResolveKeywordProviders(Type enchantmentType)
    {
        DiscoverProvidersFromLoadedAssemblies();
        return KeywordProviders.Where(provider => provider.EnchantmentType == enchantmentType);
    }

    internal static IPresentationProviderRegistration? ResolvePresentationProvider(Type enchantmentType)
    {
        DiscoverProvidersFromLoadedAssemblies();
        return ResolveSingleProvider(PresentationProviders, enchantmentType);
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
                if (!CouldContainStackProviders(assembly))
                {
                    continue;
                }

                DiscoverProvidersFromAssembly(assembly);
            }
        }
    }

    private static int DiscoverProvidersFromAssembly(Assembly assembly, Type? targetEnchantmentType = null)
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
            return 0;
        }

        int registeredCount = 0;
        foreach (Type type in types)
        {
            if (type.IsAbstract ||
                type.IsInterface ||
                type.ContainsGenericParameters ||
                !AutoRegisteredProviderTypes.Add(type))
            {
                continue;
            }

            List<(Type InterfaceType, Type EnchantmentType)> supportedInterfaces =
                GetSupportedProviderInterfaces(type);
            if (targetEnchantmentType != null)
            {
                supportedInterfaces = supportedInterfaces
                    .Where(pair => pair.EnchantmentType == targetEnchantmentType)
                    .ToList();
            }

            if (supportedInterfaces.Count == 0)
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

            foreach ((Type interfaceType, Type enchantmentType) in supportedInterfaces)
            {
                RegisterDiscoveredProvider(instance, interfaceType, enchantmentType);
                registeredCount++;
            }
        }

        return registeredCount;
    }

    private static bool CouldContainStackProviders(Assembly assembly)
    {
        Assembly apiAssembly = typeof(MultiEnchantmentStackApi).Assembly;
        if (assembly == apiAssembly)
        {
            return true;
        }

        try
        {
            return assembly.GetReferencedAssemblies()
                .Any(reference => string.Equals(
                    reference.Name,
                    apiAssembly.GetName().Name,
                    StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static List<(Type InterfaceType, Type EnchantmentType)> GetSupportedProviderInterfaces(Type type)
    {
        List<(Type InterfaceType, Type EnchantmentType)> result = new();
        foreach (Type interfaceType in type.GetInterfaces())
        {
            if (!interfaceType.IsGenericType)
            {
                continue;
            }

            Type genericType = interfaceType.GetGenericTypeDefinition();
            if (genericType != typeof(IEnchantmentStackDefinitionProvider<>) &&
                genericType != typeof(IEnchantmentMergedStateProvider<>) &&
                genericType != typeof(IEnchantmentExecutionPolicyProvider<>) &&
                genericType != typeof(IEnchantmentKeywordSourceProvider<>) &&
                genericType != typeof(IEnchantmentPresentationProvider<>))
            {
                continue;
            }

            Type enchantmentType = interfaceType.GetGenericArguments()[0];
            if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
            {
                continue;
            }

            result.Add((genericType, enchantmentType));
        }

        return result;
    }

    private static void RegisterDiscoveredProvider(object? instance, Type interfaceType, Type enchantmentType)
    {
        if (instance == null)
        {
            return;
        }

        MethodInfo? registerMethod = interfaceType switch
        {
            var type when type == typeof(IEnchantmentStackDefinitionProvider<>) =>
                typeof(MultiEnchantmentStackApi).GetMethod(nameof(RegisterDiscoveredDefinitionProvider), BindingFlags.NonPublic | BindingFlags.Static),
            var type when type == typeof(IEnchantmentMergedStateProvider<>) =>
                typeof(MultiEnchantmentStackApi).GetMethod(nameof(RegisterDiscoveredMergedStateProvider), BindingFlags.NonPublic | BindingFlags.Static),
            var type when type == typeof(IEnchantmentExecutionPolicyProvider<>) =>
                typeof(MultiEnchantmentStackApi).GetMethod(nameof(RegisterDiscoveredExecutionPolicyProvider), BindingFlags.NonPublic | BindingFlags.Static),
            var type when type == typeof(IEnchantmentKeywordSourceProvider<>) =>
                typeof(MultiEnchantmentStackApi).GetMethod(nameof(RegisterDiscoveredKeywordProvider), BindingFlags.NonPublic | BindingFlags.Static),
            var type when type == typeof(IEnchantmentPresentationProvider<>) =>
                typeof(MultiEnchantmentStackApi).GetMethod(nameof(RegisterDiscoveredPresentationProvider), BindingFlags.NonPublic | BindingFlags.Static),
            _ => null,
        };

        registerMethod?.MakeGenericMethod(enchantmentType).Invoke(null, new[] { instance });
    }

    private static void RegisterDiscoveredDefinitionProvider<TEnchantment>(object instance)
        where TEnchantment : EnchantmentModel
    {
        RegisterDefinitionProvider((IEnchantmentStackDefinitionProvider<TEnchantment>)instance);
    }

    private static void RegisterDiscoveredMergedStateProvider<TEnchantment>(object instance)
        where TEnchantment : EnchantmentModel
    {
        RegisterMergedStateProvider((IEnchantmentMergedStateProvider<TEnchantment>)instance);
    }

    private static void RegisterDiscoveredExecutionPolicyProvider<TEnchantment>(object instance)
        where TEnchantment : EnchantmentModel
    {
        RegisterExecutionPolicyProvider((IEnchantmentExecutionPolicyProvider<TEnchantment>)instance);
    }

    private static void RegisterDiscoveredKeywordProvider<TEnchantment>(object instance)
        where TEnchantment : EnchantmentModel
    {
        RegisterKeywordProvider((IEnchantmentKeywordSourceProvider<TEnchantment>)instance);
    }

    private static void RegisterDiscoveredPresentationProvider<TEnchantment>(object instance)
        where TEnchantment : EnchantmentModel
    {
        RegisterPresentationProvider((IEnchantmentPresentationProvider<TEnchantment>)instance);
    }

    private static void RegisterSingleProvider<TRegistration>(
        List<TRegistration> registrations,
        TRegistration registration,
        string category)
        where TRegistration : class, ISingleProviderRegistration
    {
        if (registrations.Any(existing =>
                existing.EnchantmentType == registration.EnchantmentType &&
                ReferenceEquals(existing.ProviderInstance, registration.ProviderInstance)))
        {
            return;
        }

        registrations.Add(registration);
        registrations.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
        if (registrations.Count(existing => existing.EnchantmentType == registration.EnchantmentType) > 1)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] Multiple {category} providers registered for {registration.EnchantmentType.FullName}. The highest-priority provider will win.");
        }
    }

    private static void RegisterMultiProvider<TRegistration>(
        List<TRegistration> registrations,
        TRegistration registration,
        string category)
        where TRegistration : class, IProviderRegistration
    {
        if (registrations.Any(existing =>
                existing.EnchantmentType == registration.EnchantmentType &&
                ReferenceEquals(existing.ProviderInstance, registration.ProviderInstance)))
        {
            return;
        }

        registrations.Add(registration);
        registrations.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
        if (registrations.Count(existing => existing.EnchantmentType == registration.EnchantmentType) > 1)
        {
            MultiEnchantmentMod.Logger.Info(
                $"[StackApi] Multiple {category} providers registered for {registration.EnchantmentType.FullName}. They will be evaluated in priority order.");
        }
    }

    private static void UnregisterProvider<TRegistration>(
        List<TRegistration> registrations,
        object provider,
        Type enchantmentType)
        where TRegistration : class, IProviderRegistration
    {
        if (provider == null)
        {
            return;
        }

        registrations.RemoveAll(existing =>
            existing.EnchantmentType == enchantmentType &&
            ReferenceEquals(existing.ProviderInstance, provider));
    }

    private static TRegistration? ResolveSingleProvider<TRegistration>(
        IEnumerable<TRegistration> registrations,
        Type enchantmentType)
        where TRegistration : class, ISingleProviderRegistration
    {
        return registrations.FirstOrDefault(provider => provider.EnchantmentType == enchantmentType);
    }

    internal interface IProviderRegistration
    {
        int Priority { get; }
        Type EnchantmentType { get; }
        Type ProviderType { get; }
        object ProviderInstance { get; }
    }

    internal interface ISingleProviderRegistration : IProviderRegistration
    {
    }

    internal interface IStackDefinitionProviderRegistration : ISingleProviderRegistration
    {
        EnchantmentStackDefinition GetDefinition();
    }

    internal interface IMergedStateProviderRegistration : ISingleProviderRegistration
    {
        void ApplyMergedAmountDelta(EnchantmentModel enchantment, int addedAmount);
        void RefreshMergedState(EnchantmentModel enchantment);
    }

    internal interface IExecutionPolicyProviderRegistration : ISingleProviderRegistration
    {
        EnchantmentExecutionPolicy GetExecutionPolicy();
    }

    internal interface IKeywordSourceProviderRegistration : IProviderRegistration
    {
        IEnumerable<CardKeyword> GetTrackedKeywords();
        int GetKeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword);
    }

    internal interface IPresentationProviderRegistration : ISingleProviderRegistration
    {
        IReadOnlyList<int>? GetVisualSliceAmounts(EnchantmentStackSnapshot snapshot);
        bool TryFormatExtraCardText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText);
    }

    private sealed class StackDefinitionProviderRegistration<TEnchantment> : IStackDefinitionProviderRegistration
        where TEnchantment : EnchantmentModel
    {
        private readonly IEnchantmentStackDefinitionProvider<TEnchantment> _provider;

        public StackDefinitionProviderRegistration(IEnchantmentStackDefinitionProvider<TEnchantment> provider)
        {
            _provider = provider;
        }

        public int Priority => _provider.Priority;
        public Type EnchantmentType => typeof(TEnchantment);
        public Type ProviderType => _provider.GetType();
        public object ProviderInstance => _provider;

        public EnchantmentStackDefinition GetDefinition()
        {
            return _provider.GetDefinition();
        }
    }

    private sealed class MergedStateProviderRegistration<TEnchantment> : IMergedStateProviderRegistration
        where TEnchantment : EnchantmentModel
    {
        private readonly IEnchantmentMergedStateProvider<TEnchantment> _provider;

        public MergedStateProviderRegistration(IEnchantmentMergedStateProvider<TEnchantment> provider)
        {
            _provider = provider;
        }

        public int Priority => _provider.Priority;
        public Type EnchantmentType => typeof(TEnchantment);
        public Type ProviderType => _provider.GetType();
        public object ProviderInstance => _provider;

        public void ApplyMergedAmountDelta(EnchantmentModel enchantment, int addedAmount)
        {
            _provider.ApplyMergedAmountDelta((TEnchantment)enchantment, addedAmount);
        }

        public void RefreshMergedState(EnchantmentModel enchantment)
        {
            _provider.RefreshMergedState((TEnchantment)enchantment);
        }
    }

    private sealed class ExecutionPolicyProviderRegistration<TEnchantment> : IExecutionPolicyProviderRegistration
        where TEnchantment : EnchantmentModel
    {
        private readonly IEnchantmentExecutionPolicyProvider<TEnchantment> _provider;

        public ExecutionPolicyProviderRegistration(IEnchantmentExecutionPolicyProvider<TEnchantment> provider)
        {
            _provider = provider;
        }

        public int Priority => _provider.Priority;
        public Type EnchantmentType => typeof(TEnchantment);
        public Type ProviderType => _provider.GetType();
        public object ProviderInstance => _provider;

        public EnchantmentExecutionPolicy GetExecutionPolicy()
        {
            return _provider.GetExecutionPolicy();
        }
    }

    private sealed class KeywordSourceProviderRegistration<TEnchantment> : IKeywordSourceProviderRegistration
        where TEnchantment : EnchantmentModel
    {
        private readonly IEnchantmentKeywordSourceProvider<TEnchantment> _provider;

        public KeywordSourceProviderRegistration(IEnchantmentKeywordSourceProvider<TEnchantment> provider)
        {
            _provider = provider;
        }

        public int Priority => _provider.Priority;
        public Type EnchantmentType => typeof(TEnchantment);
        public Type ProviderType => _provider.GetType();
        public object ProviderInstance => _provider;

        public IEnumerable<CardKeyword> GetTrackedKeywords()
        {
            return _provider.GetTrackedKeywords();
        }

        public int GetKeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword)
        {
            return _provider.GetKeywordSourceAmount(snapshot, keyword);
        }
    }

    private sealed class PresentationProviderRegistration<TEnchantment> : IPresentationProviderRegistration
        where TEnchantment : EnchantmentModel
    {
        private readonly IEnchantmentPresentationProvider<TEnchantment> _provider;

        public PresentationProviderRegistration(IEnchantmentPresentationProvider<TEnchantment> provider)
        {
            _provider = provider;
        }

        public int Priority => _provider.Priority;
        public Type EnchantmentType => typeof(TEnchantment);
        public Type ProviderType => _provider.GetType();
        public object ProviderInstance => _provider;

        public IReadOnlyList<int>? GetVisualSliceAmounts(EnchantmentStackSnapshot snapshot)
        {
            return _provider.GetVisualSliceAmounts(snapshot);
        }

        public bool TryFormatExtraCardText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText)
        {
            return _provider.TryFormatExtraCardText(snapshot, defaultText, out formattedText);
        }
    }
}

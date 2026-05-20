using System;
using System.Collections.Generic;
using System.Linq;
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
    BeforePlayPhaseStart,
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
    HookExecutionMode BeforePlayPhaseStart = HookExecutionMode.Default,
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
            EnchantmentHookKind.BeforePlayPhaseStart => BeforePlayPhaseStart,
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

internal interface IEnchantmentStackDefinitionProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    EnchantmentStackDefinition GetDefinition();
}

internal interface IEnchantmentMergedStateProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    void ApplyMergedAmountDelta(TEnchantment enchantment, int addedAmount);

    void RefreshMergedState(TEnchantment enchantment);
}

internal interface IEnchantmentExecutionPolicyProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    EnchantmentExecutionPolicy GetExecutionPolicy();
}

internal interface IEnchantmentKeywordSourceProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    IEnumerable<CardKeyword> GetTrackedKeywords();

    int GetKeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword);
}

internal interface IEnchantmentPresentationProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    IReadOnlyList<int>? GetVisualSliceAmounts(EnchantmentStackSnapshot snapshot);

    bool TryFormatExtraCardText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText);
}

internal interface IEnchantmentLifecycleProvider<TEnchantment>
    where TEnchantment : EnchantmentModel
{
    Api.EnchantmentScope GetScope();

    void OnApplied(CardModel card, TEnchantment enchantment);

    bool OnRemoved(CardModel card, TEnchantment enchantment, Api.RemovalReason reason);

    void OnCombatStart(CardModel card, TEnchantment enchantment);

    void OnCombatEnd(CardModel card, TEnchantment enchantment);

    void OnTurnStart(CardModel card, TEnchantment enchantment);

    void OnTurnEnd(CardModel card, TEnchantment enchantment);

    /// <summary>
    /// Fires after a save load or multiplayer packet-receive has fully reconstructed the
    /// enchantment onto its card. <em>Not</em> a substitute for <see cref="OnApplied"/>; both
    /// callbacks coexist for the same enchantment over its lifetime (one on fresh apply, the
    /// other on every subsequent restore). Use this hook to rebuild any external runtime cache
    /// (e.g. a <c>ConditionalWeakTable&lt;CardModel, T&gt;</c>) that doesn't survive serialization.
    /// </summary>
    void OnRestored(CardModel card, TEnchantment enchantment);
}

public static class MultiEnchantmentStackApi
{
    private static readonly List<IStackDefinitionProviderRegistration> DefinitionProviders = new();
    private static readonly List<IMergedStateProviderRegistration> MergedStateProviders = new();
    private static readonly List<IExecutionPolicyProviderRegistration> ExecutionPolicyProviders = new();
    private static readonly List<IKeywordSourceProviderRegistration> KeywordProviders = new();
    private static readonly List<IPresentationProviderRegistration> PresentationProviders = new();
    private static readonly List<ILifecycleProviderRegistration> LifecycleProviders = new();

    internal static void RegisterDefinitionProvider<TEnchantment>(
        IEnchantmentStackDefinitionProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            DefinitionProviders,
            new StackDefinitionProviderRegistration<TEnchantment>(provider),
            "definition");
    }

    internal static void UnregisterDefinitionProvider<TEnchantment>(
        IEnchantmentStackDefinitionProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(DefinitionProviders, provider, typeof(TEnchantment));
    }

    internal static void RegisterMergedStateProvider<TEnchantment>(
        IEnchantmentMergedStateProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            MergedStateProviders,
            new MergedStateProviderRegistration<TEnchantment>(provider),
            "merged-state");
    }

    internal static void UnregisterMergedStateProvider<TEnchantment>(
        IEnchantmentMergedStateProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(MergedStateProviders, provider, typeof(TEnchantment));
    }

    internal static void RegisterExecutionPolicyProvider<TEnchantment>(
        IEnchantmentExecutionPolicyProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            ExecutionPolicyProviders,
            new ExecutionPolicyProviderRegistration<TEnchantment>(provider),
            "execution-policy");
    }

    internal static void UnregisterExecutionPolicyProvider<TEnchantment>(
        IEnchantmentExecutionPolicyProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(ExecutionPolicyProviders, provider, typeof(TEnchantment));
    }

    internal static void RegisterKeywordProvider<TEnchantment>(
        IEnchantmentKeywordSourceProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterMultiProvider(
            KeywordProviders,
            new KeywordSourceProviderRegistration<TEnchantment>(provider),
            "keyword");
    }

    internal static void UnregisterKeywordProvider<TEnchantment>(
        IEnchantmentKeywordSourceProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(KeywordProviders, provider, typeof(TEnchantment));
    }

    internal static void RegisterPresentationProvider<TEnchantment>(
        IEnchantmentPresentationProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            PresentationProviders,
            new PresentationProviderRegistration<TEnchantment>(provider),
            "presentation");
    }

    internal static void UnregisterPresentationProvider<TEnchantment>(
        IEnchantmentPresentationProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(PresentationProviders, provider, typeof(TEnchantment));
    }

    internal static void RegisterLifecycleProvider<TEnchantment>(
        IEnchantmentLifecycleProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(provider);
        RegisterSingleProvider(
            LifecycleProviders,
            new LifecycleProviderRegistration<TEnchantment>(provider),
            "lifecycle");
    }

    internal static void UnregisterLifecycleProvider<TEnchantment>(
        IEnchantmentLifecycleProvider<TEnchantment> provider)
        where TEnchantment : EnchantmentModel
    {
        UnregisterProvider(LifecycleProviders, provider, typeof(TEnchantment));
    }

    internal static EnchantmentStackDefinition GetDefinition(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return MultiEnchantmentStackSupport.GetDefinition(enchantmentType);
    }

    internal static EnchantmentExecutionPolicy GetExecutionPolicy(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return MultiEnchantmentStackSupport.GetExecutionPolicy(enchantmentType);
    }

    internal static HookExecutionMode GetExecutionMode(Type enchantmentType, EnchantmentHookKind hookKind)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return MultiEnchantmentStackSupport.GetExecutionMode(enchantmentType, hookKind);
    }

    internal static EnchantmentStackSnapshot GetSnapshot(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        return MultiEnchantmentStackSupport.GetSnapshot(enchantment);
    }

    internal static IReadOnlyList<EnchantmentStackSnapshot> GetSnapshots(CardModel? card)
    {
        return MultiEnchantmentStackSupport.GetSnapshots(card);
    }

    internal static int GetHookExecutionCount(EnchantmentModel enchantment, EnchantmentHookKind hookKind)
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
        Api.Internal.AssemblyScanner.EnsureScanned();
        return ResolveSingleProvider(DefinitionProviders, enchantmentType);
    }

    internal static IMergedStateProviderRegistration? ResolveMergedStateProvider(Type enchantmentType)
    {
        Api.Internal.AssemblyScanner.EnsureScanned();
        return ResolveSingleProvider(MergedStateProviders, enchantmentType);
    }

    internal static IExecutionPolicyProviderRegistration? ResolveExecutionPolicyProvider(Type enchantmentType)
    {
        Api.Internal.AssemblyScanner.EnsureScanned();
        return ResolveSingleProvider(ExecutionPolicyProviders, enchantmentType);
    }

    internal static IEnumerable<IKeywordSourceProviderRegistration> ResolveKeywordProviders(Type enchantmentType)
    {
        Api.Internal.AssemblyScanner.EnsureScanned();
        return KeywordProviders.Where(provider => provider.EnchantmentType == enchantmentType);
    }

    internal static IPresentationProviderRegistration? ResolvePresentationProvider(Type enchantmentType)
    {
        Api.Internal.AssemblyScanner.EnsureScanned();
        return ResolveSingleProvider(PresentationProviders, enchantmentType);
    }

    internal static ILifecycleProviderRegistration? ResolveLifecycleProvider(Type enchantmentType)
    {
        Api.Internal.AssemblyScanner.EnsureScanned();
        return ResolveSingleProvider(LifecycleProviders, enchantmentType);
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
        if (registrations.Count(existing => existing.EnchantmentType == registration.EnchantmentType) > 1)
        {
            MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] Multiple {category} providers registered for {registration.EnchantmentType.FullName}. The most recently registered provider will win.");
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
        if (registrations.Count(existing => existing.EnchantmentType == registration.EnchantmentType) > 1)
        {
            MultiEnchantmentMod.Logger.Info(
                $"[StackApi] Multiple {category} providers registered for {registration.EnchantmentType.FullName}. They will be evaluated in registration order.");
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
        return registrations.LastOrDefault(provider => provider.EnchantmentType == enchantmentType);
    }

    internal interface IProviderRegistration
    {
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

    internal interface ILifecycleProviderRegistration : ISingleProviderRegistration
    {
        Api.EnchantmentScope GetScope();
        void OnApplied(CardModel card, EnchantmentModel enchantment);
        bool OnRemoved(CardModel card, EnchantmentModel enchantment, Api.RemovalReason reason);
        void OnCombatStart(CardModel card, EnchantmentModel enchantment);
        void OnCombatEnd(CardModel card, EnchantmentModel enchantment);
        void OnTurnStart(CardModel card, EnchantmentModel enchantment);
        void OnTurnEnd(CardModel card, EnchantmentModel enchantment);
        void OnRestored(CardModel card, EnchantmentModel enchantment);
    }

    private sealed class StackDefinitionProviderRegistration<TEnchantment> : IStackDefinitionProviderRegistration
        where TEnchantment : EnchantmentModel
    {
        private readonly IEnchantmentStackDefinitionProvider<TEnchantment> _provider;

        public StackDefinitionProviderRegistration(IEnchantmentStackDefinitionProvider<TEnchantment> provider)
        {
            _provider = provider;
        }

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

    private sealed class LifecycleProviderRegistration<TEnchantment> : ILifecycleProviderRegistration
        where TEnchantment : EnchantmentModel
    {
        private readonly IEnchantmentLifecycleProvider<TEnchantment> _provider;

        public LifecycleProviderRegistration(IEnchantmentLifecycleProvider<TEnchantment> provider)
        {
            _provider = provider;
        }

        public Type EnchantmentType => typeof(TEnchantment);
        public Type ProviderType => _provider.GetType();
        public object ProviderInstance => _provider;

        public Api.EnchantmentScope GetScope()
        {
            return _provider.GetScope();
        }

        public void OnApplied(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnApplied(card, (TEnchantment)enchantment);
        }

        public bool OnRemoved(CardModel card, EnchantmentModel enchantment, Api.RemovalReason reason)
        {
            return _provider.OnRemoved(card, (TEnchantment)enchantment, reason);
        }

        public void OnCombatStart(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCombatStart(card, (TEnchantment)enchantment);
        }

        public void OnCombatEnd(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCombatEnd(card, (TEnchantment)enchantment);
        }

        public void OnTurnStart(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnTurnStart(card, (TEnchantment)enchantment);
        }

        public void OnTurnEnd(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnTurnEnd(card, (TEnchantment)enchantment);
        }

        public void OnRestored(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnRestored(card, (TEnchantment)enchantment);
        }
    }
}

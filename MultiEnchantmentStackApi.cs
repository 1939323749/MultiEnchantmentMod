using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Models;

// The public types in this file (EnchantmentStackBehavior / EnchantmentStatusAggregation /
// EnchantmentHookKind / HookExecutionMode / EnchantmentStackDefinition / EnchantmentExecutionPolicy
// / EnchantmentStackSlice / EnchantmentStackSnapshot / MultiEnchantmentStackApi) live in the
// legacy MultiEnchantmentMod namespace rather than MultiEnchantmentMod.Api for historical
// reasons. They surface in v2 public delegate signatures (TrackKeyword, FormatExtraText,
// VisualSlices, ModifyDynamicVar) and ExecutionPolicyBuilder, so downstream mods using v2 must
// `using MultiEnchantmentMod;` alongside `using MultiEnchantmentMod.Api;`.
//
// Moving them into MultiEnchantmentMod.Api would be a source-breaking change for downstream
// authors and is therefore deferred to the next major API version. The internal provider
// interfaces below stay `internal` — they are not part of any contract a downstream mod should
// implement.

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
    IReadOnlyList<EnchantmentModel> LiveInstances,
    IReadOnlyDictionary<EnchantmentModel, Api.ScopeRuntimeStateView>? ScopeStates = null)
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

    /// <summary>
    /// Convenience accessor for the scope runtime state of a specific enchantment instance.
    /// Returns <c>null</c> when <paramref name="enchantment"/> is not in this snapshot's
    /// <see cref="ScopeStates"/> (e.g. the enchantment has no scope configured, or the
    /// snapshot was produced by a code path that doesn't populate scope states).
    /// </summary>
    public Api.ScopeRuntimeStateView? StateOf(EnchantmentModel enchantment)
    {
        if (ScopeStates == null || enchantment == null) return null;
        return ScopeStates.TryGetValue(enchantment, out Api.ScopeRuntimeStateView? view) ? view : null;
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

    // Dispatched only for enchantments where MultiEnchantmentScopeSupport.IsActive returns true
    // at the moment the event fires. See MultiEnchantmentScopeSupport.DispatchOn*ForCard.

    void OnCardPlayed(CardModel card, TEnchantment enchantment);
    void OnCardDrawn(CardModel card, TEnchantment enchantment);
    void OnCardExhausted(CardModel card, TEnchantment enchantment);
    void OnCardDiscarded(CardModel card, TEnchantment enchantment);
    void OnCardEnteredCombat(CardModel card, TEnchantment enchantment);

    // Unlike the per-card hooks above, these fire for ANY card event in combat. Opt-in: the
    // adapter null-checks the entry field and returns immediately when unset, so enchantments
    // that don't register these hooks pay zero cost. Parameters are
    // (eventCard, selfCard, selfEnchantment) — event card first.

    void OnAnyCardPlayed(CardModel playedCard, CardModel selfCard, TEnchantment enchantment) { }
    void OnAnyCardDrawn(CardModel drawnCard, CardModel selfCard, TEnchantment enchantment) { }
    void OnAnyCardExhausted(CardModel exhaustedCard, CardModel selfCard, TEnchantment enchantment) { }
    void OnAnyCardDiscarded(CardModel discardedCard, CardModel selfCard, TEnchantment enchantment) { }

    // Fires when another enchantment is added to / removed from the same card.

    void OnSiblingApplied(CardModel card, TEnchantment self, EnchantmentModel newSibling) { }
    void OnSiblingRemoved(CardModel card, TEnchantment self, EnchantmentModel removedSibling, Api.RemovalReason reason) { }

    /// <summary>
    /// Bridge to <c>Hook.AfterDamageReceived</c>. Dispatched per active enchantment whose card
    /// is owned by <see cref="Api.DamageReceivedContext.Target"/>'s player.
    /// </summary>
    void OnAfterDamageReceived(CardModel card, TEnchantment enchantment, Api.DamageReceivedContext context);

    void OnSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side);
    void OnBeforeSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side);
    void OnBeforeAttack(CardModel card, TEnchantment enchantment, AttackCommand command);
    void OnAfterAttack(CardModel card, TEnchantment enchantment, AttackCommand command);

    void OnCardChangedPiles(CardModel card, TEnchantment enchantment, PileType oldPile, AbstractModel? source);
    void OnCardRetained(CardModel card, TEnchantment enchantment);
    void OnBeforeBlockGained(CardModel card, TEnchantment enchantment, Api.BlockGainContext context);
    void OnBlockGained(CardModel card, TEnchantment enchantment, Api.BlockGainContext context);

    /// <summary>Guard hook. Returning <c>false</c> from any active enchantment vetoes the death.</summary>
    bool OnShouldDie(CardModel card, TEnchantment enchantment, Creature creature);

    /// <summary>
    /// When <c>true</c>, this provider carries an active-status predicate (see
    /// <see cref="IEnchantmentRegistration.WhenActive"/> / <see cref="ShouldBeActive"/>). The
    /// runtime will call <see cref="ShouldBeActive"/> and sync <c>enchantment.Status</c>
    /// accordingly. Defaults to <c>false</c> for compatibility with providers that don't use
    /// this feature.
    /// </summary>
    bool HasActiveStatusPredicate => false;

    /// <summary>
    /// Evaluates the active-status predicate. Only called when
    /// <see cref="HasActiveStatusPredicate"/> is <c>true</c>. Must return <c>true</c>
    /// (active / <c>Status.Normal</c>) or <c>false</c> (inactive / <c>Status.Disabled</c>).
    /// </summary>
    bool ShouldBeActive(CardModel card, TEnchantment enchantment) => true;
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
        void OnCardPlayed(CardModel card, EnchantmentModel enchantment);
        void OnCardDrawn(CardModel card, EnchantmentModel enchantment);
        void OnCardExhausted(CardModel card, EnchantmentModel enchantment);
        void OnCardDiscarded(CardModel card, EnchantmentModel enchantment);
        void OnCardEnteredCombat(CardModel card, EnchantmentModel enchantment);
        void OnAfterDamageReceived(CardModel card, EnchantmentModel enchantment, Api.DamageReceivedContext context);
        void OnSideTurnStart(CardModel card, EnchantmentModel enchantment, CombatSide side);
        void OnBeforeSideTurnStart(CardModel card, EnchantmentModel enchantment, CombatSide side);
        void OnBeforeAttack(CardModel card, EnchantmentModel enchantment, AttackCommand command);
        void OnAfterAttack(CardModel card, EnchantmentModel enchantment, AttackCommand command);
        void OnCardChangedPiles(CardModel card, EnchantmentModel enchantment, PileType oldPile, AbstractModel? source);
        void OnCardRetained(CardModel card, EnchantmentModel enchantment);
        void OnBeforeBlockGained(CardModel card, EnchantmentModel enchantment, Api.BlockGainContext context);
        void OnBlockGained(CardModel card, EnchantmentModel enchantment, Api.BlockGainContext context);
        bool OnShouldDie(CardModel card, EnchantmentModel enchantment, Creature creature);

        void OnAnyCardPlayed(CardModel playedCard, CardModel selfCard, EnchantmentModel enchantment) { }
        void OnAnyCardDrawn(CardModel drawnCard, CardModel selfCard, EnchantmentModel enchantment) { }
        void OnAnyCardExhausted(CardModel exhaustedCard, CardModel selfCard, EnchantmentModel enchantment) { }
        void OnAnyCardDiscarded(CardModel discardedCard, CardModel selfCard, EnchantmentModel enchantment) { }

        void OnSiblingApplied(CardModel card, EnchantmentModel self, EnchantmentModel newSibling) { }
        void OnSiblingRemoved(CardModel card, EnchantmentModel self, EnchantmentModel removedSibling, Api.RemovalReason reason) { }

        bool HasActiveStatusPredicate => false;
        bool ShouldBeActive(CardModel card, EnchantmentModel enchantment) => true;
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

        public void OnCardPlayed(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCardPlayed(card, (TEnchantment)enchantment);
        }

        public void OnCardDrawn(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCardDrawn(card, (TEnchantment)enchantment);
        }

        public void OnCardExhausted(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCardExhausted(card, (TEnchantment)enchantment);
        }

        public void OnCardDiscarded(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCardDiscarded(card, (TEnchantment)enchantment);
        }

        public void OnCardEnteredCombat(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCardEnteredCombat(card, (TEnchantment)enchantment);
        }

        public void OnAfterDamageReceived(CardModel card, EnchantmentModel enchantment, Api.DamageReceivedContext context)
        {
            _provider.OnAfterDamageReceived(card, (TEnchantment)enchantment, context);
        }

        public void OnSideTurnStart(CardModel card, EnchantmentModel enchantment, CombatSide side)
        {
            _provider.OnSideTurnStart(card, (TEnchantment)enchantment, side);
        }

        public void OnBeforeSideTurnStart(CardModel card, EnchantmentModel enchantment, CombatSide side)
        {
            _provider.OnBeforeSideTurnStart(card, (TEnchantment)enchantment, side);
        }

        public void OnBeforeAttack(CardModel card, EnchantmentModel enchantment, AttackCommand command)
        {
            _provider.OnBeforeAttack(card, (TEnchantment)enchantment, command);
        }

        public void OnAfterAttack(CardModel card, EnchantmentModel enchantment, AttackCommand command)
        {
            _provider.OnAfterAttack(card, (TEnchantment)enchantment, command);
        }

        public void OnCardChangedPiles(CardModel card, EnchantmentModel enchantment, PileType oldPile, AbstractModel? source)
        {
            _provider.OnCardChangedPiles(card, (TEnchantment)enchantment, oldPile, source);
        }

        public void OnCardRetained(CardModel card, EnchantmentModel enchantment)
        {
            _provider.OnCardRetained(card, (TEnchantment)enchantment);
        }

        public void OnBeforeBlockGained(CardModel card, EnchantmentModel enchantment, Api.BlockGainContext context)
        {
            _provider.OnBeforeBlockGained(card, (TEnchantment)enchantment, context);
        }

        public void OnBlockGained(CardModel card, EnchantmentModel enchantment, Api.BlockGainContext context)
        {
            _provider.OnBlockGained(card, (TEnchantment)enchantment, context);
        }

        public bool OnShouldDie(CardModel card, EnchantmentModel enchantment, Creature creature)
        {
            return _provider.OnShouldDie(card, (TEnchantment)enchantment, creature);
        }

        public void OnAnyCardPlayed(CardModel playedCard, CardModel selfCard, EnchantmentModel enchantment)
        {
            _provider.OnAnyCardPlayed(playedCard, selfCard, (TEnchantment)enchantment);
        }

        public void OnAnyCardDrawn(CardModel drawnCard, CardModel selfCard, EnchantmentModel enchantment)
        {
            _provider.OnAnyCardDrawn(drawnCard, selfCard, (TEnchantment)enchantment);
        }

        public void OnAnyCardExhausted(CardModel exhaustedCard, CardModel selfCard, EnchantmentModel enchantment)
        {
            _provider.OnAnyCardExhausted(exhaustedCard, selfCard, (TEnchantment)enchantment);
        }

        public void OnAnyCardDiscarded(CardModel discardedCard, CardModel selfCard, EnchantmentModel enchantment)
        {
            _provider.OnAnyCardDiscarded(discardedCard, selfCard, (TEnchantment)enchantment);
        }

        public void OnSiblingApplied(CardModel card, EnchantmentModel self, EnchantmentModel newSibling)
        {
            _provider.OnSiblingApplied(card, (TEnchantment)self, newSibling);
        }

        public void OnSiblingRemoved(CardModel card, EnchantmentModel self, EnchantmentModel removedSibling, Api.RemovalReason reason)
        {
            _provider.OnSiblingRemoved(card, (TEnchantment)self, removedSibling, reason);
        }

        public bool HasActiveStatusPredicate => _provider.HasActiveStatusPredicate;

        public bool ShouldBeActive(CardModel card, EnchantmentModel enchantment) =>
            _provider.ShouldBeActive(card, (TEnchantment)enchantment);
    }
}

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Concrete builder behind <see cref="IEnchantmentRegistration"/>. Generic on <typeparamref name="TEnchantment"/>
/// so its <see cref="Commit"/> can call into the strongly-typed
/// <see cref="EnchantmentRegistry.Install{TEnchantment}"/> entry point. Instantiated by
/// <c>MultiEnchantmentApi.Register&lt;T&gt;()</c> and, via <see cref="Activator.CreateInstance(Type)"/>,
/// from <c>MultiEnchantmentApi.Register(Type)</c>.
/// </summary>
internal sealed class EnchantmentRegistration<TEnchantment> : IEnchantmentRegistration
    where TEnchantment : EnchantmentModel
{
    private readonly EnchantmentEntry _entry = new() { EnchantmentType = typeof(TEnchantment) };
    private bool _committed;

    public Type EnchantmentType => typeof(TEnchantment);

    public IEnchantmentRegistration Stack(StackBehavior behavior, StatusAggregation status)
    {
        EnsureNotCommitted();
        _entry.Definition = new StackDefinition(behavior, status);
        return this;
    }

    public IEnchantmentRegistration Execution(Action<ExecutionPolicyBuilder> configure)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(configure);
        ExecutionPolicyBuilder builder = new();
        configure(builder);
        _entry.ExecutionPolicy = builder.Build();
        return this;
    }

    public IEnchantmentRegistration OnMergedDelta(Action<EnchantmentModel, int> action)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(action);
        _entry.OnMergedDelta = action;
        return this;
    }

    public IEnchantmentRegistration OnMergedRefresh(Action<EnchantmentModel> action)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(action);
        _entry.OnMergedRefresh = action;
        return this;
    }

    public IEnchantmentRegistration WithScope(EnchantmentScope scope)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(scope);
        _entry.GetScope = () => scope;
        return this;
    }

    public IEnchantmentRegistration LingerForTurns(int turns)
    {
        return WithScope(EnchantmentScope.LingerForTurns(turns));
    }

    public IEnchantmentRegistration MaxActivations(int n, ActivationTrigger? t = null)
    {
        return WithScope(EnchantmentScope.MaxActivations(n, t));
    }

    public IEnchantmentRegistration RemoveWhen(
        Func<CardModel, EnchantmentModel, bool> predicate,
        params ActivationTrigger[] checkOn)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(checkOn);
        return WithScope(EnchantmentScope.RemoveWhen(predicate, checkOn));
    }

    public IEnchantmentRegistration WhenActive(Func<CardModel, EnchantmentModel, bool> predicate)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(predicate);
        _entry.GetScope = () => EnchantmentScope.ConditionalActive(predicate);
        return this;
    }

    public IEnchantmentRegistration OnApplied(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        _entry.OnApplied = handler;
        return this;
    }

    public IEnchantmentRegistration OnRemoved(Func<CardModel, EnchantmentModel, RemovalReason, bool> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        _entry.OnRemoved = handler;
        return this;
    }

    public IEnchantmentRegistration OnCombatStart(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        _entry.OnCombatStart = handler;
        return this;
    }

    public IEnchantmentRegistration OnCombatEnd(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        _entry.OnCombatEnd = handler;
        return this;
    }

    public IEnchantmentRegistration OnTurnStart(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        _entry.OnTurnStart = handler;
        return this;
    }

    public IEnchantmentRegistration OnTurnEnd(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        _entry.OnTurnEnd = handler;
        return this;
    }

    public IEnchantmentRegistration OnRestored(Action<CardModel, EnchantmentModel> handler)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(handler);
        _entry.OnRestored = handler;
        return this;
    }

    public IEnchantmentRegistration TrackKeyword(CardKeyword keyword, Func<EnchantmentStackSnapshot, int> amountFn)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(amountFn);
        _entry.Keywords.Add(new KeywordContribution(keyword, amountFn));
        return this;
    }

    public IEnchantmentRegistration FormatExtraText(PresentationTextFormatter formatter)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(formatter);
        _entry.FormatExtraText = formatter;
        return this;
    }

    public IEnchantmentRegistration VisualSlices(Func<EnchantmentStackSnapshot, IReadOnlyList<int>?> compute)
    {
        EnsureNotCommitted();
        ArgumentNullException.ThrowIfNull(compute);
        _entry.GetVisualSliceAmounts = compute;
        return this;
    }

    public IEnchantmentRegistration ModifyDynamicVar(
        string varKey,
        Func<EnchantmentStackSnapshot, decimal, decimal> contribution)
    {
        EnsureNotCommitted();
        if (string.IsNullOrEmpty(varKey))
        {
            throw new ArgumentException("VarKey must be a non-empty string.", nameof(varKey));
        }
        ArgumentNullException.ThrowIfNull(contribution);
        _entry.DynamicVarContributions.Add(new DynamicVarContribution(varKey, contribution));
        return this;
    }

    public IDisposable Commit()
    {
        EnsureNotCommitted();
        _committed = true;
        return EnchantmentRegistry.Install<TEnchantment>(_entry);
    }

    private void EnsureNotCommitted()
    {
        if (_committed)
        {
            throw new InvalidOperationException(
                "This registration has already been committed; create a new MultiEnchantmentApi.Register<T>() builder if you need to add more.");
        }
    }
}

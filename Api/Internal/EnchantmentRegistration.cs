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

    public IEnchantmentRegistration WithPriority(int priority)
    {
        EnsureNotCommitted();
        _entry.Priority = priority;
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

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api.Internal;
// MultiEnchantmentMod is BOTH the legacy namespace and the bootstrap class name, so we use
// aliases for the types we pull across.
using LegacyExecutionPolicy = MultiEnchantmentMod.EnchantmentExecutionPolicy;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// One-class companion for an enchantment that participates in the stacking system. Subclass this,
/// optionally tag the subclass with <see cref="EnchantmentDefinitionAttribute"/>, and the scanner
/// will pick it up alongside the enchantment model itself.
/// </summary>
/// <remarks>
/// Defaults read from <see cref="EnchantmentAttribute"/> (if present on <typeparamref name="TEnchantment"/>)
/// and from <see cref="BuiltInDefaults"/> for built-in / unknown types. Override any virtual
/// member to inject custom behavior. The subclass must have a parameterless constructor —
/// enforced at runtime and by analyzer rule MEM004.
/// </remarks>
public abstract class EnchantmentDefinition<TEnchantment> : IEnchantmentDefinition
    where TEnchantment : EnchantmentModel
{
    /// <inheritdoc/>
    public Type EnchantmentType => typeof(TEnchantment);

    /// <inheritdoc/>
    /// <remarks>
    /// Materializes the virtual-method bag below into an <see cref="EnchantmentRegistry"/> entry
    /// and installs the corresponding adapter shims into the legacy provider tables. Idempotent
    /// only in the sense that calling <c>Register()</c> twice on the same instance creates two
    /// registrations (and two disposables); typical usage is to call it once from the assembly
    /// scanner or from a manual <c>[ModInitializer]</c>.
    /// </remarks>
    public IDisposable Register()
    {
        EnchantmentEntry entry = new()
        {
            EnchantmentType = typeof(TEnchantment),
            Priority = Priority,
            Definition = GetDefinition(),
            ExecutionPolicy = GetExecutionPolicy(),
            OnMergedDelta = (model, addedAmount) => InvokeOnMergedDelta((TEnchantment)model, addedAmount),
            OnMergedRefresh = model => InvokeOnMergedRefresh((TEnchantment)model),
            FormatExtraText = (EnchantmentStackSnapshot s, string def, out string text) =>
                InvokeTryFormatExtraText(s, def, out text),
            GetVisualSliceAmounts = InvokeGetVisualSliceAmounts,
        };

        foreach (CardKeyword keyword in InvokeTrackedKeywords())
        {
            entry.Keywords.Add(new KeywordContribution(
                keyword,
                snapshot => InvokeKeywordSourceAmount(snapshot, keyword)));
        }

        return EnchantmentRegistry.Install<TEnchantment>(entry);
    }

    /// <summary>
    /// Tie-breaker when multiple definitions target the same enchantment type. Higher wins.
    /// Override or set <see cref="EnchantmentDefinitionAttribute.Priority"/> on the subclass.
    /// </summary>
    public virtual int Priority => 0;

    /// <summary>
    /// Returns the stacking contract for this enchantment. Defaults to whatever
    /// <see cref="EnchantmentAttribute"/> on the model type declares, falling back to the built-in
    /// matrix and finally to <see cref="StackDefinition.Default"/>.
    /// </summary>
    public virtual StackDefinition GetDefinition()
    {
        EnchantmentAttribute? attribute = (EnchantmentAttribute?)Attribute.GetCustomAttribute(
            typeof(TEnchantment), typeof(EnchantmentAttribute));
        return attribute != null
            ? new StackDefinition(attribute.Stack, attribute.Status)
            : BuiltInDefaults.GetDefinition(typeof(TEnchantment));
    }

    /// <summary>
    /// Returns the per-hook execution policy when this definition needs to override the
    /// runtime's built-in modes for its <see cref="StackBehavior"/>. Defaults read from
    /// <see cref="EnchantmentExecutionAttribute"/> on either the enchantment type or the
    /// definition subclass; <c>null</c> means "don't install a custom policy", which lets
    /// <c>MultiEnchantmentStackSupport.GetExecutionPolicy</c> use the behavior-derived
    /// defaults from <see cref="MultiEnchantmentStackSupport"/>'s switch.
    /// </summary>
    public virtual LegacyExecutionPolicy? GetExecutionPolicy()
    {
        EnchantmentExecutionAttribute? attribute =
            (EnchantmentExecutionAttribute?)Attribute.GetCustomAttribute(
                GetType(), typeof(EnchantmentExecutionAttribute))
            ?? (EnchantmentExecutionAttribute?)Attribute.GetCustomAttribute(
                typeof(TEnchantment), typeof(EnchantmentExecutionAttribute));

        if (attribute == null)
        {
            // No explicit override — return null so EnchantmentRegistry.Install doesn't wire an
            // adapter shim that would otherwise force-override the legacy behavior-derived
            // defaults with an all-Default record.
            return null;
        }

        return new LegacyExecutionPolicy(
            DefaultMode: attribute.All,
            OnEnchant: attribute.OnEnchant,
            OnPlay: attribute.OnPlay,
            AfterCardPlayed: attribute.AfterCardPlayed,
            AfterCardDrawn: attribute.AfterCardDrawn,
            AfterPlayerTurnStart: attribute.AfterPlayerTurnStart,
            BeforePlayPhaseStart: attribute.BeforePlayPhaseStart,
            BeforeFlush: attribute.BeforeFlush);
    }

    /// <summary>
    /// Invoked once per merge application (i.e. every time <c>Amount</c> grows because another
    /// instance of the same type was applied to the card). The default implementation does
    /// nothing — override to apply incremental side effects (e.g. lower energy cost, add a stat).
    /// Called on the anchor instance; <paramref name="addedAmount"/> is the delta from this
    /// application alone, not the running total.
    /// </summary>
    protected virtual void OnMergedDelta(TEnchantment enchantment, int addedAmount) { }

    /// <summary>
    /// Invoked whenever a merged enchantment's derived state needs to resync (after restoring
    /// from a save, after a merge delta runs, etc.). Default implementation re-runs
    /// <see cref="EnchantmentModel.RecalculateValues"/> and refreshes the owner card's DynamicVars.
    /// </summary>
    protected virtual void OnMergedRefresh(TEnchantment enchantment)
    {
        enchantment.RecalculateValues();
        enchantment.Card?.DynamicVars.RecalculateForUpgradeOrEnchant();
    }

    /// <summary>
    /// Card keywords that this enchantment can add or remove while it's active. Each keyword
    /// returned here will trigger <see cref="KeywordSourceAmount"/> on every refresh; the sum
    /// across all definitions and keyword providers determines whether the keyword is present.
    /// Defaults to the union of every <see cref="EnchantmentKeywordAttribute"/> on the type.
    /// </summary>
    protected virtual IEnumerable<CardKeyword> TrackedKeywords
    {
        get
        {
            HashSet<CardKeyword> keywords = new();
            foreach (Attribute raw in Attribute.GetCustomAttributes(
                         typeof(TEnchantment), typeof(EnchantmentKeywordAttribute)))
            {
                if (raw is EnchantmentKeywordAttribute attribute)
                {
                    keywords.Add(attribute.Keyword);
                }
            }
            foreach (Attribute raw in Attribute.GetCustomAttributes(
                         GetType(), typeof(EnchantmentKeywordAttribute)))
            {
                if (raw is EnchantmentKeywordAttribute attribute)
                {
                    keywords.Add(attribute.Keyword);
                }
            }
            return keywords;
        }
    }

    /// <summary>
    /// Returns how much this enchantment contributes to <paramref name="keyword"/> given the
    /// provided snapshot. Default reads from any matching <see cref="EnchantmentKeywordAttribute"/>
    /// on the type or this definition class.
    /// </summary>
    protected virtual int KeywordSourceAmount(EnchantmentStackSnapshot snapshot, CardKeyword keyword)
    {
        EnchantmentKeywordAttribute? attribute = FindKeywordAttribute(keyword);
        if (attribute == null)
        {
            return 0;
        }

        return attribute.Mode switch
        {
            KeywordEvalMode.PerInstance => snapshot.ActiveInstanceCount,
            KeywordEvalMode.PerTotalAmount => snapshot.ActiveTotalAmount,
            KeywordEvalMode.Constant => attribute.Constant,
            // Custom: fall through to 0 — the subclass is expected to override this method.
            _ => 0,
        };
    }

    /// <summary>
    /// Optional override that returns custom visual slice amounts. Return <c>null</c> to let the
    /// default per-slice computation apply.
    /// </summary>
    protected virtual IReadOnlyList<int>? GetVisualSliceAmounts(EnchantmentStackSnapshot snapshot) => null;

    /// <summary>
    /// Optional override that supplies a custom extra-text string for the card description.
    /// Return <c>false</c> to keep the default text.
    /// </summary>
    protected virtual bool TryFormatExtraText(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText)
    {
        formattedText = defaultText;
        return false;
    }

    private EnchantmentKeywordAttribute? FindKeywordAttribute(CardKeyword keyword)
    {
        foreach (Attribute raw in Attribute.GetCustomAttributes(
                     GetType(), typeof(EnchantmentKeywordAttribute)))
        {
            if (raw is EnchantmentKeywordAttribute attribute && attribute.Keyword == keyword)
            {
                return attribute;
            }
        }

        foreach (Attribute raw in Attribute.GetCustomAttributes(
                     typeof(TEnchantment), typeof(EnchantmentKeywordAttribute)))
        {
            if (raw is EnchantmentKeywordAttribute attribute && attribute.Keyword == keyword)
            {
                return attribute;
            }
        }

        return null;
    }

    // --- Internal accessors so the adapter / registry can reach the protected virtuals ---
    internal void InvokeOnMergedDelta(TEnchantment enchantment, int addedAmount) => OnMergedDelta(enchantment, addedAmount);
    internal void InvokeOnMergedRefresh(TEnchantment enchantment) => OnMergedRefresh(enchantment);
    internal IEnumerable<CardKeyword> InvokeTrackedKeywords() => TrackedKeywords;
    internal int InvokeKeywordSourceAmount(EnchantmentStackSnapshot s, CardKeyword k) => KeywordSourceAmount(s, k);
    internal IReadOnlyList<int>? InvokeGetVisualSliceAmounts(EnchantmentStackSnapshot s) => GetVisualSliceAmounts(s);
    internal bool InvokeTryFormatExtraText(EnchantmentStackSnapshot s, string defaultText, out string text)
        => TryFormatExtraText(s, defaultText, out text);
}

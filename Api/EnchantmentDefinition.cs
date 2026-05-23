using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
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
            Definition = GetDefinition(),
            ExecutionPolicy = GetExecutionPolicy(),
        };

        if (Overrides(nameof(OnMergedDelta), typeof(TEnchantment), typeof(int)))
        {
            entry.OnMergedDelta = (model, addedAmount) => InvokeOnMergedDelta((TEnchantment)model, addedAmount);
        }

        if (Overrides(nameof(OnMergedRefresh), typeof(TEnchantment)))
        {
            entry.OnMergedRefresh = model => InvokeOnMergedRefresh((TEnchantment)model);
        }

        if (Overrides(nameof(TryFormatExtraText), typeof(EnchantmentStackSnapshot), typeof(string), typeof(string).MakeByRefType()))
        {
            entry.FormatExtraText = (EnchantmentStackSnapshot s, string def, out string text) =>
                InvokeTryFormatExtraText(s, def, out text);
        }

        if (Overrides(nameof(GetVisualSliceAmounts), typeof(EnchantmentStackSnapshot)))
        {
            entry.GetVisualSliceAmounts = InvokeGetVisualSliceAmounts;
        }

        if (HasExplicitScopeOrLifecycle())
        {
            entry.GetScope = InvokeScope;
            entry.OnApplied = (card, model) => InvokeOnApplied(card, (TEnchantment)model);
            entry.OnRemoved = (card, model, reason) => InvokeOnRemoved(card, (TEnchantment)model, reason);
            entry.OnCombatStart = (card, model) => InvokeOnCombatStart(card, (TEnchantment)model);
            entry.OnCombatEnd = (card, model) => InvokeOnCombatEnd(card, (TEnchantment)model);
            entry.OnTurnStart = (card, model) => InvokeOnTurnStart(card, (TEnchantment)model);
            entry.OnTurnEnd = (card, model) => InvokeOnTurnEnd(card, (TEnchantment)model);
            entry.OnRestored = (card, model) => InvokeOnRestored(card, (TEnchantment)model);
            entry.OnCardPlayed = (card, model) => InvokeOnCardPlayed(card, (TEnchantment)model);
            entry.OnCardDrawn = (card, model) => InvokeOnCardDrawn(card, (TEnchantment)model);
            entry.OnCardExhausted = (card, model) => InvokeOnCardExhausted(card, (TEnchantment)model);
            entry.OnCardDiscarded = (card, model) => InvokeOnCardDiscarded(card, (TEnchantment)model);
            entry.OnCardEnteredCombat = (card, model) => InvokeOnCardEnteredCombat(card, (TEnchantment)model);
            entry.OnAfterDamageReceived = (card, model, ctx) => InvokeOnAfterDamageReceived(card, (TEnchantment)model, ctx);
            entry.OnSideTurnStart = (card, model, side) => InvokeOnSideTurnStart(card, (TEnchantment)model, side);
            entry.OnBeforeSideTurnStart = (card, model, side) => InvokeOnBeforeSideTurnStart(card, (TEnchantment)model, side);
            entry.OnBeforeAttack = (card, model, cmd) => InvokeOnBeforeAttack(card, (TEnchantment)model, cmd);
            entry.OnAfterAttack = (card, model, cmd) => InvokeOnAfterAttack(card, (TEnchantment)model, cmd);
            entry.OnCardChangedPiles = (card, model, pile, source) => InvokeOnCardChangedPiles(card, (TEnchantment)model, pile, source);
            entry.OnCardRetained = (card, model) => InvokeOnCardRetained(card, (TEnchantment)model);
            entry.OnBeforeBlockGained = (card, model, ctx) => InvokeOnBeforeBlockGained(card, (TEnchantment)model, ctx);
            entry.OnBlockGained = (card, model, ctx) => InvokeOnBlockGained(card, (TEnchantment)model, ctx);
            entry.OnShouldDie = (card, model, creature) => InvokeOnShouldDie(card, (TEnchantment)model, creature);
            entry.OnAnyCardPlayed = (played, self, model) => InvokeOnAnyCardPlayed(played, self, (TEnchantment)model);
            entry.OnAnyCardDrawn = (drawn, self, model) => InvokeOnAnyCardDrawn(drawn, self, (TEnchantment)model);
            entry.OnAnyCardExhausted = (exhausted, self, model) => InvokeOnAnyCardExhausted(exhausted, self, (TEnchantment)model);
            entry.OnAnyCardDiscarded = (discarded, self, model) => InvokeOnAnyCardDiscarded(discarded, self, (TEnchantment)model);
            entry.OnSiblingApplied = (card, self, sibling) => InvokeOnSiblingApplied(card, (TEnchantment)self, sibling);
            entry.OnSiblingRemoved = (card, self, sibling, reason) => InvokeOnSiblingRemoved(card, (TEnchantment)self, sibling, reason);
        }

        foreach (CardKeyword keyword in InvokeTrackedKeywords())
        {
            entry.Keywords.Add(new KeywordContribution(
                keyword,
                snapshot => InvokeKeywordSourceAmount(snapshot, keyword)));
        }

        foreach (DynamicVarContribution contribution in InvokeDynamicVarContributions())
        {
            entry.DynamicVarContributions.Add(contribution);
        }

        return EnchantmentRegistry.Install<TEnchantment>(entry);
    }

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

    public virtual EnchantmentScope Scope
    {
        get
        {
            EnchantmentAttribute? attribute = (EnchantmentAttribute?)Attribute.GetCustomAttribute(
                typeof(TEnchantment), typeof(EnchantmentAttribute));
            if (attribute == null)
            {
                return EnchantmentScope.Permanent;
            }

            if (attribute.MaxActivations > 0)
            {
                return EnchantmentScope.MaxActivations(attribute.MaxActivations, attribute.Activation);
            }

            if (attribute.LingerTurns > 0)
            {
                return EnchantmentScope.LingerForTurns(attribute.LingerTurns);
            }

            return attribute.Scope switch
            {
                ScopeKind.UntilCombatEnds => EnchantmentScope.UntilCombatEnds,
                ScopeKind.UntilTurnEnds => EnchantmentScope.UntilTurnEnds,
                ScopeKind.LingerForTurns => EnchantmentScope.LingerForTurns(attribute.LingerTurns),
                ScopeKind.MaxActivations => EnchantmentScope.MaxActivations(attribute.MaxActivations, attribute.Activation),
                _ => EnchantmentScope.Permanent,
            };
        }
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

    protected virtual void OnApplied(CardModel card, TEnchantment enchantment) { }
    protected virtual bool OnRemoved(CardModel card, TEnchantment enchantment, RemovalReason reason) => true;

    /// <summary>
    /// Fires when another enchantment is attached to the same card. Safe to call
    /// <see cref="MultiEnchantmentApi.RemoveEnchantment"/> from within the handler.
    /// </summary>
    protected virtual void OnSiblingApplied(CardModel card, TEnchantment self, EnchantmentModel newSibling) { }

    /// <summary>
    /// Fires when another enchantment is removed from the same card.
    /// </summary>
    protected virtual void OnSiblingRemoved(CardModel card, TEnchantment self, EnchantmentModel removedSibling, RemovalReason reason) { }
    protected virtual void OnCombatStart(CardModel card, TEnchantment enchantment) { }
    protected virtual void OnCombatEnd(CardModel card, TEnchantment enchantment) { }
    protected virtual void OnTurnStart(CardModel card, TEnchantment enchantment) { }
    protected virtual void OnTurnEnd(CardModel card, TEnchantment enchantment) { }

    /// <summary>
    /// Fires after the enchantment has been reconstructed from save / multiplayer packet data
    /// and reattached to its card. Use this hook (not <see cref="OnApplied"/>) to rebuild any
    /// runtime cache that doesn't survive serialization.
    /// </summary>
    protected virtual void OnRestored(CardModel card, TEnchantment enchantment) { }

    // === Phase 3a — vanilla card-event hook bridges ===========================================
    // Each callback only fires when the enchantment is active (see MultiEnchantmentScopeSupport
    // .IsActive). They mirror Hook.AfterCardPlayed / AfterCardDrawn / AfterCardExhausted /
    // AfterCardDiscarded / AfterCardEnteredCombat respectively. Default implementations do
    // nothing — override to react.

    /// <summary>Bridge to vanilla <c>Hook.AfterCardPlayed</c> scoped to this enchantment's card.</summary>
    protected virtual void OnCardPlayed(CardModel card, TEnchantment enchantment) { }

    /// <summary>Bridge to vanilla <c>Hook.AfterCardDrawn</c> scoped to this enchantment's card.</summary>
    protected virtual void OnCardDrawn(CardModel card, TEnchantment enchantment) { }

    /// <summary>Bridge to vanilla <c>Hook.AfterCardExhausted</c> scoped to this enchantment's card.</summary>
    protected virtual void OnCardExhausted(CardModel card, TEnchantment enchantment) { }

    /// <summary>Bridge to vanilla <c>Hook.AfterCardDiscarded</c> scoped to this enchantment's card.</summary>
    protected virtual void OnCardDiscarded(CardModel card, TEnchantment enchantment) { }

    /// <summary>
    /// Bridge to vanilla <c>Hook.AfterCardEnteredCombat</c>. Fires on every entry — initial deck
    /// sweep, Astrolabe copies, Madness-generated cards — distinct from <see cref="OnCombatStart"/>,
    /// which fires once per combat per card.
    /// </summary>
    protected virtual void OnCardEnteredCombat(CardModel card, TEnchantment enchantment) { }

    // === Phase 4 — broadcast card-event hooks ================================================
    // These fire for ANY card event in combat, not just the card carrying this enchantment.
    // Opt-in: enchantments that do not override these methods are never visited by the broadcast
    // dispatcher (the entry field stays null and the adapter returns immediately).

    /// <summary>
    /// Fires after <b>any</b> card is played in combat. <paramref name="playedCard"/> is the card
    /// that was just played; <paramref name="selfCard"/> is the card carrying this enchantment.
    /// </summary>
    protected virtual void OnAnyCardPlayed(CardModel playedCard, CardModel selfCard, TEnchantment enchantment) { }

    /// <summary>
    /// Fires after <b>any</b> card is drawn in combat. Broadcast counterpart of
    /// <see cref="OnCardDrawn"/>.
    /// </summary>
    protected virtual void OnAnyCardDrawn(CardModel drawnCard, CardModel selfCard, TEnchantment enchantment) { }

    /// <summary>
    /// Fires after <b>any</b> card is exhausted in combat. Broadcast counterpart of
    /// <see cref="OnCardExhausted"/>.
    /// </summary>
    protected virtual void OnAnyCardExhausted(CardModel exhaustedCard, CardModel selfCard, TEnchantment enchantment) { }

    /// <summary>
    /// Fires after <b>any</b> card is discarded in combat. Broadcast counterpart of
    /// <see cref="OnCardDiscarded"/>.
    /// </summary>
    protected virtual void OnAnyCardDiscarded(CardModel discardedCard, CardModel selfCard, TEnchantment enchantment) { }

    /// <summary>
    /// Bridge to vanilla <c>Hook.AfterDamageReceived</c> scoped to the player owning this
    /// enchantment's card. <paramref name="context"/> bundles target / damage result / dealer /
    /// source so handlers can branch without taking five parameters.
    /// </summary>
    protected virtual void OnAfterDamageReceived(CardModel card, TEnchantment enchantment, DamageReceivedContext context) { }

    // === Phase 3b — combat-flow bridges ======================================================

    /// <summary>Bridge to <c>Hook.AfterSideTurnStart</c>. Fires for both player and enemy turns.</summary>
    protected virtual void OnSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side) { }

    /// <summary>Bridge to <c>Hook.BeforeSideTurnStart</c>.</summary>
    protected virtual void OnBeforeSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side) { }

    /// <summary>Bridge to <c>Hook.BeforeAttack</c>. <paramref name="command"/> exposes attacker / results / card source.</summary>
    protected virtual void OnBeforeAttack(CardModel card, TEnchantment enchantment, AttackCommand command) { }

    /// <summary>Bridge to <c>Hook.AfterAttack</c>.</summary>
    protected virtual void OnAfterAttack(CardModel card, TEnchantment enchantment, AttackCommand command) { }

    // === Phase 3c — pile / guard / block bridges ============================================

    /// <summary>Bridge to <c>Hook.AfterCardChangedPiles</c>. Inspect <c>card.Pile.Type</c> for the new pile.</summary>
    protected virtual void OnCardChangedPiles(CardModel card, TEnchantment enchantment, PileType oldPile, AbstractModel? source) { }

    /// <summary>Bridge to <c>Hook.AfterCardRetained</c>.</summary>
    protected virtual void OnCardRetained(CardModel card, TEnchantment enchantment) { }

    /// <summary>Bridge to <c>Hook.BeforeBlockGained</c>.</summary>
    protected virtual void OnBeforeBlockGained(CardModel card, TEnchantment enchantment, BlockGainContext context) { }

    /// <summary>Bridge to <c>Hook.AfterBlockGained</c>.</summary>
    protected virtual void OnBlockGained(CardModel card, TEnchantment enchantment, BlockGainContext context) { }

    /// <summary>
    /// Guard hook bridging <c>Hook.ShouldDie</c>. Return <c>false</c> to prevent the creature
    /// from dying; <c>true</c> means "no objection". Default returns <c>true</c>.
    /// </summary>
    protected virtual bool OnShouldDie(CardModel card, TEnchantment enchantment, Creature creature) => true;

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
    /// Dynamic-variable contributions registered by this definition. The default scans methods on
    /// both this definition class and <typeparamref name="TEnchantment"/> for
    /// <see cref="ModifyDynamicVarAttribute"/> and yields one contribution per match. Override to
    /// add or replace programmatically.
    /// </summary>
    protected virtual IEnumerable<DynamicVarContribution> DynamicVarContributions =>
        Internal.ModifyDynamicVarScanner.ScanType(GetType())
            .Concat(Internal.ModifyDynamicVarScanner.ScanType(typeof(TEnchantment)));

    /// <summary>
    /// Optional override that supplies custom extra text for the card description. The
    /// <paramref name="defaultText"/> argument is the vanilla/localized text when present, or an
    /// empty string when the enchantment has no base extra text. Return <c>true</c> with non-empty
    /// <paramref name="formattedText"/> to create or replace the displayed text; return
    /// <c>false</c> to keep the default text when one exists.
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

    private bool HasExplicitScopeOrLifecycle()
    {
        return Attribute.GetCustomAttribute(typeof(TEnchantment), typeof(EnchantmentAttribute)) is EnchantmentAttribute attribute &&
               (attribute.Scope != ScopeKind.Permanent || attribute.MaxActivations > 0 || attribute.LingerTurns > 0) ||
               Overrides("get_Scope") ||
               Overrides(nameof(OnApplied), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnRemoved), typeof(CardModel), typeof(TEnchantment), typeof(RemovalReason)) ||
               Overrides(nameof(OnCombatStart), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnCombatEnd), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnTurnStart), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnTurnEnd), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnRestored), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnCardPlayed), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnCardDrawn), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnCardExhausted), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnCardDiscarded), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnCardEnteredCombat), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnAfterDamageReceived), typeof(CardModel), typeof(TEnchantment), typeof(DamageReceivedContext)) ||
               Overrides(nameof(OnSideTurnStart), typeof(CardModel), typeof(TEnchantment), typeof(CombatSide)) ||
               Overrides(nameof(OnBeforeSideTurnStart), typeof(CardModel), typeof(TEnchantment), typeof(CombatSide)) ||
               Overrides(nameof(OnBeforeAttack), typeof(CardModel), typeof(TEnchantment), typeof(AttackCommand)) ||
               Overrides(nameof(OnAfterAttack), typeof(CardModel), typeof(TEnchantment), typeof(AttackCommand)) ||
               Overrides(nameof(OnCardChangedPiles), typeof(CardModel), typeof(TEnchantment), typeof(PileType), typeof(AbstractModel)) ||
               Overrides(nameof(OnCardRetained), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnBeforeBlockGained), typeof(CardModel), typeof(TEnchantment), typeof(BlockGainContext)) ||
               Overrides(nameof(OnBlockGained), typeof(CardModel), typeof(TEnchantment), typeof(BlockGainContext)) ||
               Overrides(nameof(OnShouldDie), typeof(CardModel), typeof(TEnchantment), typeof(Creature)) ||
               Overrides(nameof(OnAnyCardPlayed), typeof(CardModel), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnAnyCardDrawn), typeof(CardModel), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnAnyCardExhausted), typeof(CardModel), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnAnyCardDiscarded), typeof(CardModel), typeof(CardModel), typeof(TEnchantment)) ||
               Overrides(nameof(OnSiblingApplied), typeof(CardModel), typeof(TEnchantment), typeof(EnchantmentModel)) ||
               Overrides(nameof(OnSiblingRemoved), typeof(CardModel), typeof(TEnchantment), typeof(EnchantmentModel), typeof(RemovalReason));
    }

    private bool Overrides(string methodName, params Type[] parameterTypes)
    {
        MethodInfo? method = GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        return method != null && method.DeclaringType != typeof(EnchantmentDefinition<TEnchantment>);
    }

    // --- Internal accessors so the adapter / registry can reach the protected virtuals ---
    internal IEnumerable<DynamicVarContribution> InvokeDynamicVarContributions() => DynamicVarContributions;
    internal void InvokeOnMergedDelta(TEnchantment enchantment, int addedAmount) => OnMergedDelta(enchantment, addedAmount);
    internal void InvokeOnMergedRefresh(TEnchantment enchantment) => OnMergedRefresh(enchantment);
    internal IEnumerable<CardKeyword> InvokeTrackedKeywords() => TrackedKeywords;
    internal int InvokeKeywordSourceAmount(EnchantmentStackSnapshot s, CardKeyword k) => KeywordSourceAmount(s, k);
    internal IReadOnlyList<int>? InvokeGetVisualSliceAmounts(EnchantmentStackSnapshot s) => GetVisualSliceAmounts(s);
    internal bool InvokeTryFormatExtraText(EnchantmentStackSnapshot s, string defaultText, out string text)
        => TryFormatExtraText(s, defaultText, out text);
    internal EnchantmentScope InvokeScope() => Scope;
    internal void InvokeOnApplied(CardModel card, TEnchantment enchantment) => OnApplied(card, enchantment);
    internal bool InvokeOnRemoved(CardModel card, TEnchantment enchantment, RemovalReason reason) => OnRemoved(card, enchantment, reason);
    internal void InvokeOnCombatStart(CardModel card, TEnchantment enchantment) => OnCombatStart(card, enchantment);
    internal void InvokeOnCombatEnd(CardModel card, TEnchantment enchantment) => OnCombatEnd(card, enchantment);
    internal void InvokeOnTurnStart(CardModel card, TEnchantment enchantment) => OnTurnStart(card, enchantment);
    internal void InvokeOnTurnEnd(CardModel card, TEnchantment enchantment) => OnTurnEnd(card, enchantment);
    internal void InvokeOnRestored(CardModel card, TEnchantment enchantment) => OnRestored(card, enchantment);
    internal void InvokeOnCardPlayed(CardModel card, TEnchantment enchantment) => OnCardPlayed(card, enchantment);
    internal void InvokeOnCardDrawn(CardModel card, TEnchantment enchantment) => OnCardDrawn(card, enchantment);
    internal void InvokeOnCardExhausted(CardModel card, TEnchantment enchantment) => OnCardExhausted(card, enchantment);
    internal void InvokeOnCardDiscarded(CardModel card, TEnchantment enchantment) => OnCardDiscarded(card, enchantment);
    internal void InvokeOnCardEnteredCombat(CardModel card, TEnchantment enchantment) => OnCardEnteredCombat(card, enchantment);
    internal void InvokeOnAfterDamageReceived(CardModel card, TEnchantment enchantment, DamageReceivedContext context) => OnAfterDamageReceived(card, enchantment, context);
    internal void InvokeOnSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side) => OnSideTurnStart(card, enchantment, side);
    internal void InvokeOnBeforeSideTurnStart(CardModel card, TEnchantment enchantment, CombatSide side) => OnBeforeSideTurnStart(card, enchantment, side);
    internal void InvokeOnBeforeAttack(CardModel card, TEnchantment enchantment, AttackCommand command) => OnBeforeAttack(card, enchantment, command);
    internal void InvokeOnAfterAttack(CardModel card, TEnchantment enchantment, AttackCommand command) => OnAfterAttack(card, enchantment, command);
    internal void InvokeOnCardChangedPiles(CardModel card, TEnchantment enchantment, PileType oldPile, AbstractModel? source) => OnCardChangedPiles(card, enchantment, oldPile, source);
    internal void InvokeOnCardRetained(CardModel card, TEnchantment enchantment) => OnCardRetained(card, enchantment);
    internal void InvokeOnBeforeBlockGained(CardModel card, TEnchantment enchantment, BlockGainContext context) => OnBeforeBlockGained(card, enchantment, context);
    internal void InvokeOnBlockGained(CardModel card, TEnchantment enchantment, BlockGainContext context) => OnBlockGained(card, enchantment, context);
    internal bool InvokeOnShouldDie(CardModel card, TEnchantment enchantment, Creature creature) => OnShouldDie(card, enchantment, creature);
    internal void InvokeOnAnyCardPlayed(CardModel played, CardModel self, TEnchantment enchantment) => OnAnyCardPlayed(played, self, enchantment);
    internal void InvokeOnAnyCardDrawn(CardModel drawn, CardModel self, TEnchantment enchantment) => OnAnyCardDrawn(drawn, self, enchantment);
    internal void InvokeOnAnyCardExhausted(CardModel exhausted, CardModel self, TEnchantment enchantment) => OnAnyCardExhausted(exhausted, self, enchantment);
    internal void InvokeOnAnyCardDiscarded(CardModel discarded, CardModel self, TEnchantment enchantment) => OnAnyCardDiscarded(discarded, self, enchantment);
    internal void InvokeOnSiblingApplied(CardModel card, TEnchantment self, EnchantmentModel newSibling) => OnSiblingApplied(card, self, newSibling);
    internal void InvokeOnSiblingRemoved(CardModel card, TEnchantment self, EnchantmentModel removedSibling, RemovalReason reason) => OnSiblingRemoved(card, self, removedSibling, reason);
}

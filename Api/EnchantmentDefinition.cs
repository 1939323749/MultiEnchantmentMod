using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
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
using EnchantmentVisualSlice = MultiEnchantmentMod.EnchantmentVisualSlice;

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
    /// Materializes the virtual-method bag below into an <see cref="EnchantmentRegistry"/> entry.
    /// Idempotent
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

        if (Overrides(nameof(GetVisualSlices), typeof(EnchantmentStackSnapshot)))
        {
            entry.GetVisualSlices = InvokeGetVisualSlices;
        }

        if (Overrides(nameof(ShouldBeActive), typeof(CardModel), typeof(TEnchantment)))
        {
            entry.GetActiveStatus = (card, model) => InvokeShouldBeActive(card, (TEnchantment)model);
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
            entry.OnCardAppliedPower = (card, model, ctx) => InvokeOnCardAppliedPower(card, (TEnchantment)model, ctx);
            entry.OnCardTransformed = (card, model, replacement) => InvokeOnCardTransformed(card, (TEnchantment)model, replacement);
            entry.OnCardCloned = (card, model, clone) => InvokeOnCardCloned(card, (TEnchantment)model, clone);
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

        foreach (EnergyCostContribution contribution in InvokeEnergyCostContributions())
        {
            entry.EnergyCostContributions.Add(contribution);
        }

        foreach (CardPlayCountContribution contribution in InvokeCardPlayCountContributions())
        {
            entry.CardPlayCountContributions.Add(contribution);
        }

        foreach (PowerAmountGivenContribution contribution in InvokePowerAmountGivenContributions())
        {
            entry.PowerAmountGivenContributions.Add(contribution);
        }

        entry.HistoryDisplay = HistoryDisplay;
        entry.HistoryGroupHeader = HistoryGroupHeader;

        if (Overrides("get_" + nameof(PresentationStyle)))
        {
            entry.PresentationStyle = PresentationStyle;
        }

        if (Overrides(nameof(OnPlayStacked), typeof(StackedOnPlayContext)))
        {
            entry.OnPlayStacked = InvokeOnPlayStacked;
        }

        if (Overrides(nameof(BeforeCardPlayedStacked), typeof(StackedBeforeCardPlayedContext)))
        {
            entry.BeforeCardPlayedStacked = InvokeBeforeCardPlayedStacked;
        }

        if (Overrides(nameof(AfterCardPlayedStacked), typeof(StackedAfterCardPlayedContext)))
        {
            entry.AfterCardPlayedStacked = InvokeAfterCardPlayedStacked;
        }

        if (Overrides(nameof(AfterSiblingAppliedStacked), typeof(StackedAfterSiblingAppliedContext)))
        {
            entry.AfterSiblingAppliedStacked = InvokeAfterSiblingAppliedStacked;
        }

        if (Overrides(nameof(AfterCardDrawnStacked), typeof(StackedAfterCardDrawnContext)))
        {
            entry.AfterCardDrawnStacked = InvokeAfterCardDrawnStacked;
        }

        if (Overrides(nameof(AfterAnyCardDrawnStacked), typeof(StackedAfterCardDrawnContext)))
        {
            entry.AfterAnyCardDrawnStacked = InvokeAfterAnyCardDrawnStacked;
        }

        if (Overrides(nameof(BeforeFlushStacked), typeof(StackedBeforeFlushContext)))
        {
            entry.BeforeFlushStacked = InvokeBeforeFlushStacked;
        }

        if (Overrides(nameof(AfterDamageGivenStacked), typeof(StackedAfterDamageGivenContext)))
        {
            entry.AfterDamageGivenStacked = InvokeAfterDamageGivenStacked;
        }

        if (Overrides(nameof(FormatHistoryText), typeof(string), typeof(string)))
        {
            entry.HistoryTextFormatter = InvokeFormatHistoryText;
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
            // No explicit override — return null so direct registry dispatch keeps using the
            // legacy behavior-derived defaults instead of an all-Default record.
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

            return attribute.Scope switch
            {
                ScopeKind.UntilCombatEnds => EnchantmentScope.UntilCombatEnds,
                ScopeKind.UntilTurnEnds => EnchantmentScope.UntilTurnEnds,
                ScopeKind.LingerForTurns => EnchantmentScope.LingerForTurns(attribute.LingerTurns),
                ScopeKind.MaxActivations => EnchantmentScope.MaxActivations(attribute.MaxActivations, attribute.Activation),
                _ when attribute.MaxActivations > 0 => EnchantmentScope.MaxActivations(attribute.MaxActivations, attribute.Activation),
                _ when attribute.LingerTurns > 0 => EnchantmentScope.LingerForTurns(attribute.LingerTurns),
                _ => EnchantmentScope.Permanent,
            };
        }
    }

    /// <summary>
    /// Controls how this enchantment appears in the per-floor battle history tooltip.
    /// Defaults to the attribute value if present, otherwise <see cref="HistoryDisplayMode.Auto"/>.
    /// </summary>
    public virtual HistoryDisplayMode HistoryDisplay
    {
        get
        {
            if (typeof(ExtraIconEnchantmentModel).IsAssignableFrom(typeof(TEnchantment)))
            {
                return HistoryDisplayMode.Hidden;
            }

            EnchantmentAttribute? attribute = (EnchantmentAttribute?)Attribute.GetCustomAttribute(
                typeof(TEnchantment), typeof(EnchantmentAttribute));
            return attribute?.HistoryDisplay ?? HistoryDisplayMode.Auto;
        }
    }

    /// <summary>
    /// Custom group header for <see cref="HistoryDisplayMode.CustomGroup"/>. Defaults to the
    /// attribute value if present, otherwise <c>null</c>.
    /// </summary>
    public virtual string? HistoryGroupHeader
    {
        get
        {
            EnchantmentAttribute? attribute = (EnchantmentAttribute?)Attribute.GetCustomAttribute(
                typeof(TEnchantment), typeof(EnchantmentAttribute));
            return attribute?.HistoryGroupHeader;
        }
    }

    /// <summary>
    /// Controls card-UI presentation details such as badge backing, extra-text BBCode wrapping,
    /// and icon scale.
    /// </summary>
    public virtual EnchantmentPresentationStyle PresentationStyle =>
        typeof(ExtraIconEnchantmentModel).IsAssignableFrom(typeof(TEnchantment))
            ? ExtraIconPresentation.Default
            : new EnchantmentPresentationStyle();

    /// <summary>
    /// Optional custom text formatter for battle history display. Return <c>null</c> to use
    /// the default format. Receives card title and enchantment title.
    /// </summary>
    protected virtual string? FormatHistoryText(string cardTitle, string enchantmentTitle) => null;

    /// <summary>
    /// When overridden to return <c>false</c>, the enchantment's <c>Status</c> is set to
    /// <c>Disabled</c> and it is treated as inactive — no lifecycle callbacks, no dynamic-variable
    /// contributions, no derived keywords, and a dimmed visual badge. When it returns <c>true</c>
    /// (or when left at the default), the enchantment behaves normally. Only wired when this
    /// definition actually overrides the method; returning <c>true</c> unconditionally is a no-op.
    /// Definition-based counterpart of <see cref="IEnchantmentRegistration.WhenActive"/>.
    /// </summary>
    /// <remarks>
    /// <para>This predicate does not occupy the scope slot — it composes freely with
    /// <see cref="Scope"/> for lifetime/removal behavior (e.g. <c>UntilCombatEnds</c> +
    /// <c>ShouldBeActive</c>).</para>
    /// </remarks>
    protected virtual bool ShouldBeActive(CardModel card, TEnchantment enchantment) => true;

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

    /// <summary>
    /// Fires after this enchantment's card applied a power and the amount change fully resolved
    /// (bridge to vanilla <c>Hook.AfterPowerAmountChanged</c> filtered to the card as
    /// <c>cardSource</c>).
    /// </summary>
    protected virtual void OnCardAppliedPower(CardModel card, TEnchantment enchantment, PowerAppliedContext context) { }

    /// <summary>
    /// Fires after this enchantment's card was transformed into <paramref name="replacement"/>
    /// (vanilla <c>CardCmd.Transform</c>). Compatible-enchantment copying for the covered vanilla
    /// transforms has already run — use this to migrate custom runtime state or clean up
    /// card-keyed caches.
    /// </summary>
    protected virtual void OnCardTransformed(CardModel card, TEnchantment enchantment, CardModel replacement) { }

    /// <summary>
    /// Fires after this enchantment's card was cloned by a gameplay effect. The clone has already
    /// inherited all enchantments, including this one. UI preview clones do not fire this hook.
    /// </summary>
    protected virtual void OnCardCloned(CardModel card, TEnchantment enchantment, CardModel clone) { }
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
    // dispatcher (the entry field stays null and direct dispatch skips it).

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
    /// across all definitions and registrations determines whether the keyword is present.
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
    /// Optional custom visual slices with per-badge status. Override this when UI badges need
    /// their own active/disabled state. Return <c>null</c> to use
    /// <see cref="GetVisualSliceAmounts"/> or the default visual slice computation.
    /// <c>ShowAmount</c> only decides whether the badge draws a number; the slice itself is
    /// available for both numbered and non-numbered enchantments. When <c>ShowAmount</c> is
    /// true, returned amounts must sum to the snapshot total; when false, amounts only act as
    /// positive placeholders for badge count/layout.
    /// </summary>
    protected virtual IReadOnlyList<EnchantmentVisualSlice>? GetVisualSlices(EnchantmentStackSnapshot snapshot) => null;

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
    /// Optional power-amount-given contributions. Contributions fold over the running amount from
    /// <c>Hook.ModifyPowerAmountGiven</c> whenever this enchantment's card is the power
    /// application's <c>cardSource</c>.
    /// </summary>
    protected virtual IEnumerable<PowerAmountGivenContribution> PowerAmountGivenContributions =>
        Enumerable.Empty<PowerAmountGivenContribution>();

    /// <summary>
    /// Optional combat energy-cost contributions. Contributions fold over the running combat
    /// cost from <c>Hook.ModifyEnergyCostInCombat</c>.
    /// </summary>
    protected virtual IEnumerable<EnergyCostContribution> EnergyCostContributions =>
        Internal.NumericContributionScanner.ScanEnergyCost(GetType())
            .Concat(Internal.NumericContributionScanner.ScanEnergyCost(typeof(TEnchantment)));

    /// <summary>
    /// Optional card play-count contributions. Contributions fold over the running play count in
    /// the card-play wrapper after vanilla <c>Hook.ModifyCardPlayCount</c> has run.
    /// </summary>
    protected virtual IEnumerable<CardPlayCountContribution> CardPlayCountContributions =>
        Internal.NumericContributionScanner.ScanCardPlayCount(GetType())
            .Concat(Internal.NumericContributionScanner.ScanCardPlayCount(typeof(TEnchantment)));

    protected virtual Task OnPlayStacked(StackedOnPlayContext context) => Task.CompletedTask;
    protected virtual Task BeforeCardPlayedStacked(StackedBeforeCardPlayedContext context) => Task.CompletedTask;
    protected virtual Task AfterCardPlayedStacked(StackedAfterCardPlayedContext context) => Task.CompletedTask;
    protected virtual Task AfterSiblingAppliedStacked(StackedAfterSiblingAppliedContext context) => Task.CompletedTask;
    protected virtual Task AfterCardDrawnStacked(StackedAfterCardDrawnContext context) => Task.CompletedTask;
    protected virtual Task AfterAnyCardDrawnStacked(StackedAfterCardDrawnContext context) => Task.CompletedTask;
    protected virtual Task BeforeFlushStacked(StackedBeforeFlushContext context) => Task.CompletedTask;
    protected virtual Task AfterDamageGivenStacked(StackedAfterDamageGivenContext context) => Task.CompletedTask;

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
               Overrides(nameof(OnSiblingRemoved), typeof(CardModel), typeof(TEnchantment), typeof(EnchantmentModel), typeof(RemovalReason)) ||
               Overrides(nameof(OnCardAppliedPower), typeof(CardModel), typeof(TEnchantment), typeof(PowerAppliedContext)) ||
               Overrides(nameof(OnCardTransformed), typeof(CardModel), typeof(TEnchantment), typeof(CardModel)) ||
               Overrides(nameof(OnCardCloned), typeof(CardModel), typeof(TEnchantment), typeof(CardModel));
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

    // --- Internal accessors so the registry can reach the protected virtuals ---
    internal IEnumerable<DynamicVarContribution> InvokeDynamicVarContributions() => DynamicVarContributions;
    internal IEnumerable<EnergyCostContribution> InvokeEnergyCostContributions() => EnergyCostContributions;
    internal IEnumerable<CardPlayCountContribution> InvokeCardPlayCountContributions() => CardPlayCountContributions;
    internal void InvokeOnMergedDelta(TEnchantment enchantment, int addedAmount) => OnMergedDelta(enchantment, addedAmount);
    internal void InvokeOnMergedRefresh(TEnchantment enchantment) => OnMergedRefresh(enchantment);
    internal IEnumerable<CardKeyword> InvokeTrackedKeywords() => TrackedKeywords;
    internal int InvokeKeywordSourceAmount(EnchantmentStackSnapshot s, CardKeyword k) => KeywordSourceAmount(s, k);
    internal IReadOnlyList<int>? InvokeGetVisualSliceAmounts(EnchantmentStackSnapshot s) => GetVisualSliceAmounts(s);
    internal IReadOnlyList<EnchantmentVisualSlice>? InvokeGetVisualSlices(EnchantmentStackSnapshot s) => GetVisualSlices(s);
    internal bool InvokeTryFormatExtraText(EnchantmentStackSnapshot s, string defaultText, out string text)
        => TryFormatExtraText(s, defaultText, out text);
    internal EnchantmentScope InvokeScope() => Scope;
    internal bool InvokeShouldBeActive(CardModel card, TEnchantment enchantment) => ShouldBeActive(card, enchantment);
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
    internal void InvokeOnCardAppliedPower(CardModel card, TEnchantment enchantment, PowerAppliedContext context) => OnCardAppliedPower(card, enchantment, context);
    internal void InvokeOnCardTransformed(CardModel card, TEnchantment enchantment, CardModel replacement) => OnCardTransformed(card, enchantment, replacement);
    internal void InvokeOnCardCloned(CardModel card, TEnchantment enchantment, CardModel clone) => OnCardCloned(card, enchantment, clone);
    internal IEnumerable<PowerAmountGivenContribution> InvokePowerAmountGivenContributions() => PowerAmountGivenContributions;
    internal string? InvokeFormatHistoryText(string cardTitle, string enchantmentTitle) => FormatHistoryText(cardTitle, enchantmentTitle);
    internal Task InvokeOnPlayStacked(StackedOnPlayContext context) => OnPlayStacked(context);
    internal Task InvokeBeforeCardPlayedStacked(StackedBeforeCardPlayedContext context) => BeforeCardPlayedStacked(context);
    internal Task InvokeAfterCardPlayedStacked(StackedAfterCardPlayedContext context) => AfterCardPlayedStacked(context);
    internal Task InvokeAfterSiblingAppliedStacked(StackedAfterSiblingAppliedContext context) => AfterSiblingAppliedStacked(context);
    internal Task InvokeAfterCardDrawnStacked(StackedAfterCardDrawnContext context) => AfterCardDrawnStacked(context);
    internal Task InvokeAfterAnyCardDrawnStacked(StackedAfterCardDrawnContext context) => AfterAnyCardDrawnStacked(context);
    internal Task InvokeBeforeFlushStacked(StackedBeforeFlushContext context) => BeforeFlushStacked(context);
    internal Task InvokeAfterDamageGivenStacked(StackedAfterDamageGivenContext context) => AfterDamageGivenStacked(context);
}

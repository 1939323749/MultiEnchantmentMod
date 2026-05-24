using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using EnchantmentStackSnapshot = MultiEnchantmentMod.EnchantmentStackSnapshot;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Delegate signature for <see cref="IEnchantmentRegistration.FormatExtraText"/>. Same try-pattern
/// as the legacy <c>IEnchantmentPresentationProvider.TryFormatExtraCardText</c>.
/// </summary>
public delegate bool PresentationTextFormatter(
    EnchantmentStackSnapshot snapshot,
    string defaultText,
    out string formattedText);

/// <summary>
/// Fluent builder returned by <c>MultiEnchantmentApi.Register&lt;T&gt;()</c> and
/// <c>MultiEnchantmentApi.Register(Type)</c>. All setters return the same builder, ending with
/// <see cref="Commit"/>. Calling any setter after <see cref="Commit"/> throws.
/// </summary>
/// <remarks>
/// <para>
/// Typical pattern:
/// </para>
/// <code>
/// MultiEnchantmentApi.Register&lt;Goopy&gt;()
///     .Stack(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
///     .TrackKeyword(CardKeyword.Exhaust, snap => snap.ActiveInstanceCount)
///     .Commit();
/// </code>
/// <para>
/// Strongly-typed lambdas are provided by the extension methods in
/// <see cref="EnchantmentRegistrationExtensions"/>.
/// </para>
/// </remarks>
public interface IEnchantmentRegistration
{
    /// <summary>The enchantment model type this registration targets.</summary>
    Type EnchantmentType { get; }

    /// <summary>
    /// Sets the stacking behavior and status aggregation. Overwrites previous calls. Required
    /// before <see cref="Commit"/> unless you only want to register secondary behavior (delta /
    /// keyword) on top of an existing definition.
    /// </summary>
    IEnchantmentRegistration Stack(StackBehavior behavior, StatusAggregation status);

    /// <summary>
    /// Sets the full <see cref="StackDefinition"/> in one call. Use this overload when you need
    /// to configure <see cref="StackDefinition.MaxInstances"/> or
    /// <see cref="StackDefinition.OnOverflow"/> alongside the basic
    /// <see cref="StackBehavior"/> / <see cref="StatusAggregation"/> pair. Default
    /// implementation falls back to <see cref="Stack(StackBehavior, StatusAggregation)"/> for
    /// adapters that pre-date the overload (cap / overflow are silently dropped on those).
    /// </summary>
    IEnchantmentRegistration Stack(StackDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Stack(definition.Behavior, definition.Status);
    }

    /// <summary>
    /// Configures per-hook execution modes via the fluent
    /// <see cref="ExecutionPolicyBuilder"/>. Overwrites any previous execution policy.
    /// </summary>
    IEnchantmentRegistration Execution(Action<ExecutionPolicyBuilder> configure);

    /// <summary>
    /// Called once per merge application. <paramref name="action"/> receives the anchor instance
    /// and the delta amount added by this single application. Only meaningful for
    /// <see cref="StackBehavior.MergeAmount"/>.
    /// </summary>
    IEnchantmentRegistration OnMergedDelta(Action<EnchantmentModel, int> action);

    /// <summary>
    /// Called when merged enchantment state needs to resync (after save restore, etc.). Replaces
    /// the default implementation that just re-runs <c>RecalculateValues</c>.
    /// </summary>
    IEnchantmentRegistration OnMergedRefresh(Action<EnchantmentModel> action);

    /// <summary>
    /// Fires after the enchantment has been reconstructed from save / packet data and reattached
    /// to its card. Distinct from <see cref="OnApplied"/> (which is "freshly attached, never
    /// before"): <see cref="OnRestored"/> fires every time a card travels across the
    /// serialization boundary, including each multiplayer packet round-trip. Use it to rebuild
    /// any external runtime cache that doesn't survive serialization.
    /// </summary>
    IEnchantmentRegistration OnRestored(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires once after this enchantment's owning card is played. Bridges vanilla
    /// <c>Hook.AfterCardPlayed</c>. Receives the card that was just played and the enchantment
    /// instance attached to it. Inactive enchantments (gated by scope / <see cref="WhenActive"/>)
    /// do not receive this callback.
    /// </summary>
    IEnchantmentRegistration OnCardPlayed(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires once after this enchantment's owning card is drawn into the hand. Bridges vanilla
    /// <c>Hook.AfterCardDrawn</c>. Inactive enchantments do not receive this callback.
    /// </summary>
    IEnchantmentRegistration OnCardDrawn(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires once after this enchantment's owning card is exhausted (any pile → exhaust pile).
    /// Bridges vanilla <c>Hook.AfterCardExhausted</c>. Inactive enchantments do not receive this
    /// callback.
    /// </summary>
    IEnchantmentRegistration OnCardExhausted(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires once after this enchantment's owning card is discarded. Bridges vanilla
    /// <c>Hook.AfterCardDiscarded</c>. Inactive enchantments do not receive this callback.
    /// </summary>
    IEnchantmentRegistration OnCardDiscarded(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires every time this enchantment's owning card enters combat — including Astrolabe /
    /// Madness / relic-generated mid-combat additions as well as the initial deck-setup sweep.
    /// Distinct from <see cref="OnCombatStart"/>, which fires once per combat per card. Bridges
    /// vanilla <c>Hook.AfterCardEnteredCombat</c>. Inactive enchantments do not receive this
    /// callback.
    /// </summary>
    IEnchantmentRegistration OnCardEnteredCombat(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires after <b>any</b> card is played in combat — not just the card carrying this
    /// enchantment. <paramref name="handler"/> receives the played card, this enchantment's owning
    /// card, and this enchantment instance. Opt-in: enchantments that do not register this hook
    /// are never visited by the broadcast dispatcher. Use for cross-card reactive effects
    /// (e.g. "after the 3rd card this turn, trigger").
    /// </summary>
    IEnchantmentRegistration OnAnyCardPlayed(Action<CardModel /*playedCard*/, CardModel /*selfCard*/, EnchantmentModel /*self*/> handler);

    /// <summary>
    /// Fires after <b>any</b> card is drawn in combat. Broadcast counterpart of
    /// <see cref="OnCardDrawn"/>. Opt-in.
    /// </summary>
    IEnchantmentRegistration OnAnyCardDrawn(Action<CardModel, CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires after <b>any</b> card is exhausted in combat. Broadcast counterpart of
    /// <see cref="OnCardExhausted"/>. Opt-in.
    /// </summary>
    IEnchantmentRegistration OnAnyCardExhausted(Action<CardModel, CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires after <b>any</b> card is discarded in combat. Broadcast counterpart of
    /// <see cref="OnCardDiscarded"/>. Opt-in.
    /// </summary>
    IEnchantmentRegistration OnAnyCardDiscarded(Action<CardModel, CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires once after the owner of this enchantment's card receives damage. Bridges vanilla
    /// <c>Hook.AfterDamageReceived</c>. The <see cref="DamageReceivedContext"/> bundles target /
    /// result / dealer / source so handlers can branch on the damage shape. Inactive
    /// enchantments do not receive this callback.
    /// </summary>
    IEnchantmentRegistration OnAfterDamageReceived(Action<CardModel, EnchantmentModel, DamageReceivedContext> handler);

    /// <summary>
    /// Fires after a side's turn starts. Bridges vanilla <c>Hook.AfterSideTurnStart</c>. The
    /// <see cref="CombatSide"/> parameter is the side whose turn is starting — fans out to every
    /// active enchantment on every card in both players' combat states so handlers can branch
    /// on "is this my side?" themselves. Use <see cref="OnTurnStart"/> for the legacy
    /// "player turn started" semantic; <c>OnSideTurnStart</c> additionally fires for enemy turns.
    /// </summary>
    IEnchantmentRegistration OnSideTurnStart(Action<CardModel, EnchantmentModel, CombatSide> handler);

    /// <summary>
    /// Fires just before a side's turn starts. Bridges vanilla <c>Hook.BeforeSideTurnStart</c>.
    /// Use for setup that must happen before vanilla turn-start effects (block clearing, draw,
    /// energy reset). Inactive enchantments do not receive this callback.
    /// </summary>
    IEnchantmentRegistration OnBeforeSideTurnStart(Action<CardModel, EnchantmentModel, CombatSide> handler);

    /// <summary>
    /// Fires before an attack is resolved. Bridges vanilla <c>Hook.BeforeAttack</c>. The
    /// <c>AttackCommand</c> exposes the attacker, results and card source so handlers can filter
    /// by attacker identity. Vanilla doesn't deliver a <c>PlayerChoiceContext</c> here, so
    /// handlers can read/modify state but cannot execute follow-up commands.
    /// </summary>
    IEnchantmentRegistration OnBeforeAttack(Action<CardModel, EnchantmentModel, AttackCommand> handler);

    /// <summary>
    /// Fires after an attack is resolved. Bridges vanilla <c>Hook.AfterAttack</c>. Same
    /// constraint as <see cref="OnBeforeAttack"/> regarding follow-up commands.
    /// </summary>
    IEnchantmentRegistration OnAfterAttack(Action<CardModel, EnchantmentModel, AttackCommand> handler);

    /// <summary>
    /// Fires every time this enchantment's owning card moves between piles. Bridges vanilla
    /// <c>Hook.AfterCardChangedPiles</c>. <paramref name="handler"/> receives the old pile type
    /// and the source (relic / card / power that caused the move, may be null). Inspect
    /// <c>card.Pile.Type</c> for the new pile.
    /// </summary>
    IEnchantmentRegistration OnCardChangedPiles(Action<CardModel, EnchantmentModel, PileType, AbstractModel?> handler);

    /// <summary>
    /// Fires once after this enchantment's card is retained at end of turn. Bridges vanilla
    /// <c>Hook.AfterCardRetained</c>.
    /// </summary>
    IEnchantmentRegistration OnCardRetained(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Fires just before this enchantment's owner gains block. Bridges vanilla
    /// <c>Hook.BeforeBlockGained</c>.
    /// </summary>
    IEnchantmentRegistration OnBeforeBlockGained(Action<CardModel, EnchantmentModel, BlockGainContext> handler);

    /// <summary>
    /// Fires after this enchantment's owner gains block. Bridges vanilla
    /// <c>Hook.AfterBlockGained</c>.
    /// </summary>
    IEnchantmentRegistration OnBlockGained(Action<CardModel, EnchantmentModel, BlockGainContext> handler);

    /// <summary>
    /// Guard hook bridging vanilla <c>Hook.ShouldDie</c>. <paramref name="handler"/> returns
    /// <c>false</c> to prevent the creature from dying; <c>true</c> means "this enchantment does
    /// not object to the death". When multiple enchantments register, the creature dies only if
    /// EVERY active handler returns <c>true</c> (same semantics as vanilla — any single veto
    /// prevents death).
    /// </summary>
    IEnchantmentRegistration OnShouldDie(Func<CardModel, EnchantmentModel, Creature, bool> handler);

    IEnchantmentRegistration WithScope(EnchantmentScope scope);
    IEnchantmentRegistration LingerForTurns(int turns);
    IEnchantmentRegistration MaxActivations(int n, ActivationTrigger? t = null);

    /// <summary>
    /// Legacy scope-gating predicate. When <paramref name="predicate"/> returns <c>false</c>, the
    /// enchantment remains attached but does not receive active-gated callbacks. Does not mutate
    /// <c>enchantment.Status</c>.
    /// </summary>
    IEnchantmentRegistration WhenActive(Func<CardModel, EnchantmentModel, bool> predicate);

    /// <summary>
    /// Declares an active-status predicate for this enchantment. When <paramref name="predicate"/>
    /// returns <c>true</c>, <c>enchantment.Status = Normal</c>; when <c>false</c>,
    /// <c>enchantment.Status = Disabled</c>. The predicate is re-evaluated at refresh points
    /// (apply, restore, pile change, turn/combat boundaries) and whenever
    /// <see cref="MultiEnchantmentApi.NotifyPropsChanged"/> is called.
    /// </summary>
    /// <remarks>
    /// <para>This predicate does <em>not</em> replace <see cref="EnchantmentScope"/> — it
    /// composes freely with <see cref="WithScope"/>, <see cref="LingerForTurns"/>,
    /// <see cref="MaxActivations"/>, and <see cref="RemoveWhen"/>. The enchantment stays
    /// attached under its registered scope; only its <c>Status</c> and activity are
    /// affected.</para>
    /// </remarks>
    IEnchantmentRegistration WhenActiveStatus(Func<CardModel, EnchantmentModel, bool> predicate);

    /// <summary>
    /// Schedules removal as soon as <paramref name="predicate"/> evaluates to <c>true</c>. The
    /// predicate is re-checked whenever any of <paramref name="checkOn"/> fires. Equivalent to
    /// <c>WithScope(EnchantmentScope.RemoveWhen(predicate, checkOn))</c>; provided as a fluent
    /// shorthand for parity with <see cref="LingerForTurns"/> / <see cref="MaxActivations"/>.
    /// </summary>
    IEnchantmentRegistration RemoveWhen(
        Func<CardModel, EnchantmentModel, bool> predicate,
        params ActivationTrigger[] checkOn);
    IEnchantmentRegistration OnApplied(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnRemoved(Func<CardModel, EnchantmentModel, RemovalReason, bool> handler);

    /// <summary>
    /// Fires when another enchantment is attached to the same card as this one.
    /// <paramref name="handler"/> receives the owning card, this enchantment, and the newly
    /// applied sibling. Safe to call <see cref="RemoveEnchantment"/> from within the handler
    /// (iteration uses <c>.ToList()</c> snapshots).
    /// </summary>
    IEnchantmentRegistration OnSiblingApplied(Action<CardModel, EnchantmentModel /*self*/, EnchantmentModel /*newSibling*/> handler);

    /// <summary>
    /// Fires when another enchantment is removed from the same card as this one.
    /// <paramref name="handler"/> receives the owning card, this enchantment, the removed
    /// sibling, and the reason for removal.
    /// </summary>
    IEnchantmentRegistration OnSiblingRemoved(Action<CardModel, EnchantmentModel /*self*/, EnchantmentModel /*removedSibling*/, RemovalReason> handler);
    IEnchantmentRegistration OnCombatStart(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnCombatEnd(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnTurnStart(Action<CardModel, EnchantmentModel> handler);
    IEnchantmentRegistration OnTurnEnd(Action<CardModel, EnchantmentModel> handler);

    /// <summary>
    /// Declares that this enchantment contributes (or removes) the given card keyword while
    /// active. <paramref name="amountFn"/> receives the current stack snapshot and returns the
    /// contribution amount (negative removes, zero is no-op). Can be called multiple times for
    /// different keywords.
    /// </summary>
    IEnchantmentRegistration TrackKeyword(CardKeyword keyword, Func<EnchantmentStackSnapshot, int> amountFn);

    /// <summary>
    /// Supplies an extra-card-text formatter for the description box. <paramref name="formatter"/>
    /// receives the vanilla/localized default text when present, or an empty string when the
    /// enchantment has no base extra text. Return <c>true</c> with non-empty formatted text to
    /// create or replace the displayed extra text; return <c>false</c> to preserve the default text
    /// when one exists.
    /// </summary>
    IEnchantmentRegistration FormatExtraText(PresentationTextFormatter formatter);

    /// <summary>
    /// Supplies custom visual slice amounts (per badge). Return <c>null</c> from
    /// <paramref name="compute"/> to fall back to the default slice computation.
    /// </summary>
    IEnchantmentRegistration VisualSlices(Func<EnchantmentStackSnapshot, IReadOnlyList<int>?> compute);

    /// <summary>
    /// Declares that this enchantment contributes to a named dynamic variable on the card. Multiple
    /// enchantments touching the same key compose in "card application order × registration order
    /// on the same enchantment"; no separate priority layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invocation count by stack behavior:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>MergeAmount</c>: <b>once per active gameplay slice</b>. Two merged stacks → two
    ///   invocations, each with a single-slice snapshot whose <c>TotalAmount</c> equals the slice
    ///   amount (typically 1). Write per-stack formulas like <c>current + 5m</c> or
    ///   <c>current * 2m</c>; the pipeline handles scaling.</item>
    ///   <item><c>ExistenceStack</c>: <b>once per type</b> regardless of instance count
    ///   (presence-only semantics). Snapshot contains the full type-wide view.</item>
    ///   <item><c>DuplicateInstance</c>: <b>once per type</b> (the dedup mirrors ExistenceStack to
    ///   keep behavior predictable). If you want per-instance scaling, multiply by
    ///   <c>snapshot.ActiveInstanceCount</c> inside the formula.</item>
    /// </list>
    /// <para>
    /// Caveat: don't pair <c>ModifyDynamicVar("damage", ...)</c> with an
    /// <c>EnchantDamageAdditive</c>/<c>EnchantBlockAdditive</c> override on the same enchantment.
    /// Both channels stack; pick exactly one for any given key.
    /// </para>
    /// </remarks>
    /// <param name="varKey">
    /// The dynamic-variable key (e.g. <c>"damage"</c>, <c>"block"</c>, <c>"Times"</c>,
    /// <c>"Combust"</c>). Matched case-insensitively against the runtime <c>DynamicVar.Name</c>
    /// (which is PascalCase in vanilla); authors may write the lowercase placeholder form here.
    /// </param>
    /// <param name="contribution">
    /// Returns the new running value given the current snapshot for this enchantment type and the
    /// running value so far. Calling this method multiple times for the same <paramref name="varKey"/>
    /// stacks contributions in registration order.
    /// </param>
    IEnchantmentRegistration ModifyDynamicVar(
        string varKey,
        Func<EnchantmentStackSnapshot, decimal, decimal> contribution);

    /// <summary>
    /// Sets how this enchantment appears in the per-floor battle history tooltip. Defaults to
    /// <see cref="HistoryDisplayMode.Auto"/>, which hides non-permanent scoped enchantments
    /// and shows permanent ones in the rewards section.
    /// </summary>
    IEnchantmentRegistration HistoryDisplay(HistoryDisplayMode mode);

    /// <summary>
    /// Sets how this enchantment appears in the battle history with a custom group header.
    /// Implies <see cref="HistoryDisplayMode.CustomGroup"/>.
    /// </summary>
    IEnchantmentRegistration HistoryDisplay(HistoryDisplayMode mode, string groupHeader);

    /// <summary>
    /// Sets a custom text formatter for battle history display. The formatter receives the card
    /// title and enchantment title and returns the full formatted line. Return <c>null</c> to
    /// fall back to the default format. Can be combined with any <see cref="HistoryDisplayMode"/>
    /// except <see cref="HistoryDisplayMode.Hidden"/>.
    /// </summary>
    IEnchantmentRegistration HistoryText(HistoryTextFormatter formatter);

    /// <summary>
    /// Finalizes the registration and writes it into the runtime registry. Returns a handle that
    /// removes the registration when disposed — useful for tests and hot-reload scenarios.
    /// Calling <see cref="Commit"/> more than once on the same builder throws.
    /// </summary>
    IDisposable Commit();
}

/// <summary>
/// Strongly-typed lambda overloads for <see cref="IEnchantmentRegistration"/>. The non-generic
/// interface is the authoritative contract; these extensions only add type sugar so consumers
/// don't have to cast <c>EnchantmentModel</c> to their concrete subtype.
/// </summary>
public static class EnchantmentRegistrationExtensions
{
    public static IEnchantmentRegistration OnMergedDelta<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<TEnchantment, int> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnMergedDelta((e, n) => action((TEnchantment)e, n));
    }

    public static IEnchantmentRegistration OnMergedRefresh<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnMergedRefresh(e => action((TEnchantment)e));
    }

    public static IEnchantmentRegistration OnApplied<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnApplied((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnRemoved<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<CardModel, TEnchantment, RemovalReason, bool> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.OnRemoved((card, enchantment, reason) => handler(card, (TEnchantment)enchantment, reason));
    }

    public static IEnchantmentRegistration OnSiblingApplied<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, EnchantmentModel> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnSiblingApplied((card, self, sibling) => action(card, (TEnchantment)self, sibling));
    }

    public static IEnchantmentRegistration OnSiblingRemoved<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, EnchantmentModel, RemovalReason> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnSiblingRemoved((card, self, sibling, reason) => action(card, (TEnchantment)self, sibling, reason));
    }

    public static IEnchantmentRegistration OnCombatStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCombatStart((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCombatEnd<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCombatEnd((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnTurnStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnTurnStart((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnTurnEnd<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnTurnEnd((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnRestored<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnRestored((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardPlayed<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardPlayed((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardDrawn<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardDrawn((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardExhausted<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardExhausted((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardDiscarded<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardDiscarded((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnCardEnteredCombat<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardEnteredCombat((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardPlayed<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardPlayed((played, self, enchantment) => action(played, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardDrawn<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardDrawn((drawn, self, enchantment) => action(drawn, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardExhausted<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardExhausted((exhausted, self, enchantment) => action(exhausted, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAnyCardDiscarded<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAnyCardDiscarded((discarded, self, enchantment) => action(discarded, self, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnAfterDamageReceived<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, DamageReceivedContext> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAfterDamageReceived((card, enchantment, ctx) => action(card, (TEnchantment)enchantment, ctx));
    }

    public static IEnchantmentRegistration OnSideTurnStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, CombatSide> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnSideTurnStart((card, enchantment, side) => action(card, (TEnchantment)enchantment, side));
    }

    public static IEnchantmentRegistration OnBeforeSideTurnStart<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, CombatSide> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBeforeSideTurnStart((card, enchantment, side) => action(card, (TEnchantment)enchantment, side));
    }

    public static IEnchantmentRegistration OnBeforeAttack<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, AttackCommand> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBeforeAttack((card, enchantment, cmd) => action(card, (TEnchantment)enchantment, cmd));
    }

    public static IEnchantmentRegistration OnAfterAttack<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, AttackCommand> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnAfterAttack((card, enchantment, cmd) => action(card, (TEnchantment)enchantment, cmd));
    }

    public static IEnchantmentRegistration OnCardChangedPiles<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, PileType, AbstractModel?> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardChangedPiles((card, enchantment, pile, source) => action(card, (TEnchantment)enchantment, pile, source));
    }

    public static IEnchantmentRegistration OnCardRetained<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnCardRetained((card, enchantment) => action(card, (TEnchantment)enchantment));
    }

    public static IEnchantmentRegistration OnBeforeBlockGained<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, BlockGainContext> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBeforeBlockGained((card, enchantment, ctx) => action(card, (TEnchantment)enchantment, ctx));
    }

    public static IEnchantmentRegistration OnBlockGained<TEnchantment>(
        this IEnchantmentRegistration registration,
        Action<CardModel, TEnchantment, BlockGainContext> action)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(action);
        return registration.OnBlockGained((card, enchantment, ctx) => action(card, (TEnchantment)enchantment, ctx));
    }

    public static IEnchantmentRegistration OnShouldDie<TEnchantment>(
        this IEnchantmentRegistration registration,
        Func<CardModel, TEnchantment, Creature, bool> handler)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(handler);
        return registration.OnShouldDie((card, enchantment, creature) => handler(card, (TEnchantment)enchantment, creature));
    }

    /// <summary>
    /// Strongly-typed flavor of <see cref="IEnchantmentRegistration.ModifyDynamicVar"/>. The
    /// snapshot / current-value pair maps directly to the non-generic overload; the
    /// <typeparamref name="TEnchantment"/> parameter is present for symmetry with the other
    /// strongly-typed callbacks and is supplied as the snapshot's anchor instance cast to
    /// <typeparamref name="TEnchantment"/>.
    /// </summary>
    public static IEnchantmentRegistration ModifyDynamicVar<TEnchantment>(
        this IEnchantmentRegistration registration,
        string varKey,
        Func<EnchantmentStackSnapshot, TEnchantment, decimal, decimal> contribution)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(contribution);
        return registration.ModifyDynamicVar(
            varKey,
            (snapshot, current) => contribution(snapshot, (TEnchantment)snapshot.AnchorInstance, current));
    }
}

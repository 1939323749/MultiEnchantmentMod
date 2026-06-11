using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Public entry point for the v2 enchantment stacking API. Third-party mods register their
/// enchantments here — either via attributes (and an assembly scan) or via the fluent
/// <see cref="Register{TEnchantment}"/> builder.
/// </summary>
/// <remarks>
/// <para>
/// Recommended integration pattern from a third-party mod's <c>[ModInitializer]</c>:
/// </para>
/// <code>
/// public static void Initialize()
/// {
///     if (!MultiEnchantmentApi.RequireApiVersion(2)) return;
///     MultiEnchantmentApi.ScanCallingAssembly();
/// }
/// </code>
/// </remarks>
public static partial class MultiEnchantmentApi
{
    private static readonly List<BeforeCardEnchantedHandler> BeforeCardEnchantedHandlers = new();
    private static readonly List<AfterCardEnchantedHandler> AfterCardEnchantedHandlers = new();

    [ThreadStatic]
    private static int _beforeCardEnchantedDepth;

    // Cascade-depth guard for AfterCardEnchanted dispatch. Incremented around handler invocation so
    // that an enchant triggered from inside a handler reports CascadeDepth > 0. Single-threaded by
    // the game's synchronization context, mirroring the ModifyDynamicVar reentrancy guard.
    [ThreadStatic]
    private static int _afterCardEnchantedDepth;

    /// <summary>The currently shipped API version. Re-export of <see cref="MultiEnchantmentApiVersion.Current"/>.</summary>
    public static int CurrentVersion => MultiEnchantmentApiVersion.Current;

    /// <summary>
    /// Starts a fluent registration for <typeparamref name="TEnchantment"/>. Chain
    /// <see cref="IEnchantmentRegistration"/> setters and finish with
    /// <see cref="IEnchantmentRegistration.Commit"/>.
    /// </summary>
    public static IEnchantmentRegistration Register<TEnchantment>()
        where TEnchantment : EnchantmentModel
    {
        return new EnchantmentRegistration<TEnchantment>();
    }

    /// <summary>
    /// Non-generic flavor of <see cref="Register{TEnchantment}"/> — needed for built-in
    /// migrations where the type is only available as a <see cref="Type"/> reference and for
    /// downstream tools / generators.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="enchantmentType"/> is not
    /// assignable to <see cref="EnchantmentModel"/>.</exception>
    public static IEnchantmentRegistration Register(Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"{enchantmentType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(enchantmentType));
        }

        Type registrationType = typeof(EnchantmentRegistration<>).MakeGenericType(enchantmentType);
        object instance = Activator.CreateInstance(registrationType)
            ?? throw new InvalidOperationException(
                $"Failed to instantiate registration builder for {enchantmentType.FullName}.");
        return (IEnchantmentRegistration)instance;
    }

    public static bool RemoveEnchantment(
        CardModel card,
        EnchantmentModel enchantment,
        RemovalReason reason = RemovalReason.Manual)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);
        return MultiEnchantmentScopeSupport.RemoveEnchantmentWithReason(card, enchantment, reason);
    }

    /// <summary>
    /// Applies <paramref name="enchantment"/> to <paramref name="card"/>, optionally overriding the
    /// registration-time scope for this concrete application only. Predicate-bearing scopes
    /// (<c>ConditionalActive</c> / <c>RemoveWhen</c>) are rejected because they cannot be persisted.
    /// </summary>
    /// <remarks>
    /// This synchronous path does <b>not</b> dispatch the card-level
    /// <see cref="AfterCardEnchanted"/> notification — only <see cref="EnchantAsync"/> does. Marker
    /// systems that must react immediately (for example "when this card is enchanted, auto-play it")
    /// must enchant via <see cref="EnchantAsync"/>. The per-enchantment
    /// <see cref="StackedAfterSiblingAppliedContext">AfterSiblingAppliedStacked</see> hook still
    /// fires from this path, but it is dispatched synchronously (see that type's remarks).
    /// </remarks>
    public static EnchantmentModel? Enchant(
        CardModel card,
        EnchantmentModel enchantment,
        decimal amount = 1,
        EnchantmentScope? scopeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);
        if (scopeOverride != null && global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.RejectNonPersistableScopeOverride(scopeOverride, nameof(Enchant), enchantment))
        {
            return null;
        }

        using (global::MultiEnchantmentMod.Telemetry.TelemetryCollector.PushApplicationSource("api"))
        {
            return global::MultiEnchantmentMod.MultiEnchantmentSupport.ApplyEnchantmentWithScopeOverride(
                choiceContext: null,
                enchantment,
                card,
                amount,
                scopeOverride);
        }
    }

    /// <summary>
    /// Async variant of <see cref="Enchant"/> that forwards an optional
    /// <see cref="PlayerChoiceContext"/> into stacked post-application hooks. Use this when
    /// downstream "after sibling applied" handlers need to run commands immediately.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Enchant"/>, this path dispatches the card-level
    /// <see cref="AfterCardEnchanted"/> notification once application completes, and awaits handlers
    /// so they may safely run commands / auto-play the card while the freshly applied enchantment is
    /// already live. Prefer this overload for "enchant then act" (autoplay-on-enchant) flows.
    /// </remarks>
    public static Task<EnchantmentModel?> EnchantAsync(
        PlayerChoiceContext? choiceContext,
        CardModel card,
        EnchantmentModel enchantment,
        decimal amount = 1,
        EnchantmentScope? scopeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);
        if (scopeOverride != null && global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.RejectNonPersistableScopeOverride(scopeOverride, nameof(EnchantAsync), enchantment))
        {
            return Task.FromResult<EnchantmentModel?>(null);
        }

        return EnchantAsyncWithSource(choiceContext, card, enchantment, amount, scopeOverride);
    }

    private static async Task<EnchantmentModel?> EnchantAsyncWithSource(
        PlayerChoiceContext? choiceContext,
        CardModel card,
        EnchantmentModel enchantment,
        decimal amount,
        EnchantmentScope? scopeOverride)
    {
        using (global::MultiEnchantmentMod.Telemetry.TelemetryCollector.PushApplicationSource("api_async"))
        {
            return await global::MultiEnchantmentMod.MultiEnchantmentSupport.ApplyEnchantmentWithScopeOverrideAsync(
                choiceContext,
                enchantment,
                card,
                amount,
                scopeOverride);
        }
    }

    /// <summary>
    /// Subscribes to a card-level notification fired after an enchantment has been successfully
    /// applied through an async fresh application pipeline. Use this for card keyword / marker
    /// systems such as "when this card is enchanted, autoplay it" without modelling the marker as
    /// an enchantment. Dispose the returned handle to unsubscribe.
    /// </summary>
    /// <remarks>
    /// Only the async application paths (<see cref="EnchantAsync"/> and
    /// <see cref="CopyEnchantmentAsync"/>) raise this notification. The synchronous
    /// <see cref="Enchant"/> / <see cref="CopyEnchantment"/> overloads and vanilla enchant paths do
    /// <b>not</b>, because the handler is awaited and may issue game commands. Enchant through the
    /// async overloads when this notification must fire.
    /// </remarks>
    /// <summary>
    /// Registers a handler called <b>before</b> an enchantment is applied. Handlers may inspect the
    /// context, cancel the application, or modify the amount. Fired on async paths only (matching
    /// <see cref="AfterCardEnchanted"/>). Dispose the returned handle to unsubscribe.
    /// </summary>
    public static IDisposable BeforeCardEnchanted(BeforeCardEnchantedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        BeforeCardEnchantedHandlers.Add(handler);
        return new BeforeCardEnchantedSubscription(handler);
    }

    public static IDisposable AfterCardEnchanted(AfterCardEnchantedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        AfterCardEnchantedHandlers.Add(handler);
        return new AfterCardEnchantedSubscription(handler);
    }

    /// <summary>
    /// Registers a provider for display-only markers. Providers run when card UI refreshes,
    /// including card-library / preview cards that do not have live combat enchantment instances.
    /// </summary>
    public static IDisposable RegisterMarkerDisplayProvider(MarkerDisplayProvider provider)
    {
        return MarkerDisplayRegistry.RegisterProvider(provider);
    }

    /// <summary>
    /// Convenience overload for static markers keyed by a card predicate.
    /// </summary>
    public static IDisposable RegisterMarker<TEnchantment>(
        Func<CardModel, bool> appliesTo,
        EnchantmentPresentationStyle? presentationStyle = null,
        MarkerDisplayPredicate? shouldDisplay = null)
        where TEnchantment : MarkerEnchantmentModel, new()
    {
        return RegisterMarker<TEnchantment>(
            appliesTo,
            new MarkerRegistrationOptions
            {
                PresentationStyle = presentationStyle,
                ShouldDisplay = shouldDisplay,
            });
    }

    /// <summary>
    /// Overload that supplies an explicit <paramref name="icon"/> texture. Use this to draw custom
    /// art: <c>EnchantmentModel.Icon</c> is not overridable (non-virtual; it resolves from a
    /// convention path), so passing a texture here — or shipping a file at the model's icon path — is
    /// how a marker gets its image. Pass <c>null</c> to fall back to
    /// <typeparamref name="TEnchantment"/>'s canonical model icon.
    /// </summary>
    public static IDisposable RegisterMarker<TEnchantment>(
        Func<CardModel, bool> appliesTo,
        Godot.Texture2D? icon,
        EnchantmentPresentationStyle? presentationStyle = null,
        MarkerDisplayPredicate? shouldDisplay = null)
        where TEnchantment : MarkerEnchantmentModel, new()
    {
        return RegisterMarker<TEnchantment>(
            appliesTo,
            new MarkerRegistrationOptions
            {
                Icon = icon,
                PresentationStyle = presentationStyle,
                ShouldDisplay = shouldDisplay,
            });
    }

    /// <summary>
    /// Convenience overload for static markers that need the common provider-only knobs
    /// (<see cref="MarkerRegistrationOptions.ShowAmount"/>,
    /// <see cref="MarkerRegistrationOptions.Amount"/>, or
    /// <see cref="MarkerRegistrationOptions.ShowWithLiveEnchantment"/>) without writing a full
    /// <see cref="MarkerDisplayProvider"/>.
    /// </summary>
    public static IDisposable RegisterMarker<TEnchantment>(
        Func<CardModel, bool> appliesTo,
        MarkerRegistrationOptions? options)
        where TEnchantment : MarkerEnchantmentModel, new()
    {
        ArgumentNullException.ThrowIfNull(appliesTo);
        options ??= new MarkerRegistrationOptions();
        return RegisterMarkerDisplayProvider(card =>
            appliesTo(card)
                ? new[]
                {
                    new MarkerDisplay
                    {
                        EnchantmentType = typeof(TEnchantment),
                        Icon = options.Icon,
                        PresentationStyle = options.PresentationStyle,
                        ShouldDisplay = options.ShouldDisplay,
                        ShowAmount = options.ShowAmount,
                        Amount = options.Amount,
                        ShowWithLiveEnchantment = options.ShowWithLiveEnchantment,
                    },
                }
                : Array.Empty<MarkerDisplay>());
    }

    /// <summary>
    /// Forces <paramref name="card"/> to re-evaluate its markers immediately — re-runs every
    /// display provider for the card and redraws its badges. Call this after you change state a
    /// provider's predicate reads, or after disposing a provider, so a card already on screen (for
    /// example in the compendium, which does not refresh on its own) updates now instead of on the
    /// next vanilla visual pass. No-op when <paramref name="card"/> is null.
    /// </summary>
    /// <remarks>
    /// Display-only icons are predicate-driven, so there is no "edit a registration in place" call:
    /// to change what shows, change the state your provider reads (or dispose + re-register), then
    /// call this to make it visible now. Stored marker instances already refresh on
    /// <see cref="RemoveEnchantment"/> / <see cref="NotifyPropsChanged"/>.
    /// </remarks>
    public static void RefreshMarkers(CardModel? card)
    {
        if (card == null)
        {
            return;
        }

        global::MultiEnchantmentMod.MultiEnchantmentSupport.RefreshMarkers(card);
    }

    /// <summary>
    /// Forces every currently-rendered card to re-evaluate its markers. Use after changing a
    /// global condition many providers read; prefer the per-card overload when you know which card
    /// changed.
    /// </summary>
    public static void RefreshMarkers()
    {
        global::MultiEnchantmentMod.MultiEnchantmentSupport.RefreshAllMarkers();
    }

    /// <summary>
    /// Changes or clears the per-instance scope override on an already-attached enchantment.
    /// Passing <c>null</c> clears the override and returns to the registration-time scope.
    /// </summary>
    public static bool SetScopeOverride(
        CardModel card,
        EnchantmentModel enchantment,
        EnchantmentScope? newScope)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(enchantment);
        if (newScope != null && global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.RejectNonPersistableScopeOverride(newScope, nameof(SetScopeOverride), enchantment))
        {
            return false;
        }

        if (!global::MultiEnchantmentMod.MultiEnchantmentSupport.GetEnchantments(card).Any(e => ReferenceEquals(e, enchantment)))
        {
            return false;
        }

        global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.SetScopeOverride(card, enchantment, newScope);
        global::MultiEnchantmentMod.MultiEnchantmentSupport.RefreshDerivedStateFor(enchantment);
        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="card"/> has an enchantment assignable to
    /// <typeparamref name="TEnchantment"/> in any multi-enchantment slot.
    /// </summary>
    public static bool HasEnchantment<TEnchantment>(CardModel? card)
        where TEnchantment : EnchantmentModel
    {
        return global::MultiEnchantmentMod.MultiEnchantmentSupport.HasEnchantment<TEnchantment>(card);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="card"/> has an enchantment assignable to
    /// <paramref name="enchantmentType"/> in any multi-enchantment slot.
    /// </summary>
    public static bool HasEnchantment(CardModel? card, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"{enchantmentType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(enchantmentType));
        }

        foreach (EnchantmentModel enchantment in global::MultiEnchantmentMod.MultiEnchantmentSupport.GetEnchantmentsForType(card, enchantmentType))
        {
            if (enchantmentType.IsInstanceOfType(enchantment))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the live enchantment instance assignable to <typeparamref name="TEnchantment"/> on
    /// <paramref name="card"/>, or <c>null</c> when the card has none. This is the read counterpart
    /// of <see cref="HasEnchantment{TEnchantment}(CardModel?)"/>: whenever that returns <c>true</c>,
    /// this returns the instance so you can read or mutate its <c>Amount</c>/<c>Props</c>. When several
    /// instances match, the first in application order is returned — use
    /// <see cref="GetEnchantments{TEnchantment}(CardModel?)"/> to get them all.
    /// </summary>
    public static TEnchantment? GetEnchantment<TEnchantment>(CardModel? card)
        where TEnchantment : EnchantmentModel =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport
            .GetEnchantmentsForType(card, typeof(TEnchantment))
            .OfType<TEnchantment>()
            .FirstOrDefault();

    /// <summary>
    /// Returns the live enchantment instance assignable to <paramref name="enchantmentType"/> on
    /// <paramref name="card"/>, or <c>null</c> when none match. This is the non-generic counterpart
    /// of <see cref="GetEnchantment{TEnchantment}(CardModel?)"/>.
    /// </summary>
    public static EnchantmentModel? GetEnchantment(CardModel? card, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"{enchantmentType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(enchantmentType));
        }

        return global::MultiEnchantmentMod.MultiEnchantmentSupport
            .GetEnchantmentsForType(card, enchantmentType)
            .FirstOrDefault(enchantment => enchantmentType.IsInstanceOfType(enchantment));
    }

    /// <summary>
    /// Returns every live enchantment instance assignable to <typeparamref name="TEnchantment"/> on
    /// <paramref name="card"/>, in application order. Useful when the same enchantment type can stack
    /// into multiple distinct instances. Returns an empty list when there are none.
    /// </summary>
    public static IReadOnlyList<TEnchantment> GetEnchantments<TEnchantment>(CardModel? card)
        where TEnchantment : EnchantmentModel =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport
            .GetEnchantmentsForType(card, typeof(TEnchantment))
            .OfType<TEnchantment>()
            .ToList();

    /// <summary>
    /// All gameplay enchantment instances on <paramref name="card"/>, in application order, excluding
    /// <see cref="MarkerEnchantmentModel"/> markers. Returns an empty list when there are none.
    /// (For markers use <see cref="GetMarkers"/>; for the full mixed list pass
    /// <c>includeMarkers: true</c> to the other overload.)
    /// </summary>
    public static IReadOnlyList<EnchantmentModel> GetEnchantments(CardModel? card) =>
        GetEnchantments(card, includeMarkers: false);

    /// <summary>
    /// All enchantment instances on <paramref name="card"/>, in application order. Pass
    /// <paramref name="includeMarkers"/> = <c>true</c> to include
    /// <see cref="MarkerEnchantmentModel"/> markers in the list.
    /// </summary>
    public static IReadOnlyList<EnchantmentModel> GetEnchantments(CardModel? card, bool includeMarkers)
    {
        IEnumerable<EnchantmentModel> source = includeMarkers
            ? global::MultiEnchantmentMod.MultiEnchantmentSupport.GetEnchantments(card)
            : global::MultiEnchantmentMod.MultiEnchantmentSupport.GetGameplayEnchantments(card);
        return source.ToList();
    }

    /// <summary>
    /// All stored <see cref="MarkerEnchantmentModel"/> markers currently attached to
    /// <paramref name="card"/>, in application order. Inspect each element's runtime type (and its
    /// <c>Amount</c>/<c>Props</c>) to learn <em>which</em> markers the card carries. Returns an empty
    /// list when there are none.
    /// </summary>
    /// <remarks>
    /// This reports markers that exist as real enchantment instances on the card — i.e. an
    /// <see cref="MarkerEnchantmentModel"/> subclass applied through the normal enchant pipeline
    /// (<see cref="Enchant"/>). Markers shown only via a registered display provider — including both
    /// <c>RegisterMarker&lt;T&gt;</c> overloads and <see cref="RegisterMarkerDisplayProvider"/> —
    /// are recomputed from their <c>appliesTo</c> predicate at render time and are <em>not</em> card
    /// instance state, so they never appear here. To test a display-provider marker, re-evaluate that
    /// predicate yourself.
    /// </remarks>
    public static IReadOnlyList<MarkerEnchantmentModel> GetMarkers(CardModel? card) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetMarkers(card);

    /// <summary>
    /// Returns the stored marker instance of type <typeparamref name="TMarker"/> on
    /// <paramref name="card"/>, or <c>null</c> when the card has no such marker. Use this when you
    /// already know the marker type and want its live instance to read <c>Amount</c>/<c>Props</c>.
    /// </summary>
    public static TMarker? GetMarker<TMarker>(CardModel? card)
        where TMarker : MarkerEnchantmentModel =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetMarkers(card).OfType<TMarker>().FirstOrDefault();

    /// <summary>
    /// Returns <c>true</c> when <paramref name="card"/> carries at least one stored
    /// <see cref="MarkerEnchantmentModel"/> marker. Equivalent to <c>GetMarkers(card).Count &gt; 0</c>.
    /// </summary>
    public static bool HasAnyMarker(CardModel? card) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetMarkers(card).Count > 0;

    /// <summary>
    /// The marker types currently <em>visible</em> on <paramref name="card"/>, in the
    /// same order as the icon row. Includes stored <see cref="MarkerEnchantmentModel"/> instances
    /// and display-only markers from registered providers after <c>ShouldDisplay</c>,
    /// live-enchantment suppression, <c>HideWhenDisabled</c>, and display-priority ordering.
    /// Use <see cref="GetShownMarkerDetails"/> when you also need amount/style/status details.
    /// </summary>
    public static IReadOnlyList<Type> GetShownMarkers(CardModel? card) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetShownMarkerTypes(card);

    /// <summary>
    /// Detailed snapshots for markers currently <em>visible</em> on
    /// <paramref name="card"/>, in the same order as the icon row. This is the rich counterpart to
    /// <see cref="GetShownMarkers"/>: it exposes the resolved icon, amount label, status,
    /// presentation style, and whether each marker came from stored card state or a display provider.
    /// </summary>
    public static IReadOnlyList<ShownMarker> GetShownMarkerDetails(CardModel? card) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetShownMarkerDetails(card);

    /// <summary>
    /// Returns <c>true</c> when the icon row currently shows a marker assignable to
    /// <paramref name="markerType"/> on <paramref name="card"/>. See <see cref="GetShownMarkers"/>.
    /// </summary>
    public static bool IsMarkerShown(CardModel? card, Type markerType)
    {
        ArgumentNullException.ThrowIfNull(markerType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(markerType))
        {
            throw new ArgumentException(
                $"{markerType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(markerType));
        }

        IReadOnlyList<Type> shown = global::MultiEnchantmentMod.MultiEnchantmentSupport.GetShownMarkerTypes(card);
        for (int i = 0; i < shown.Count; i++)
        {
            if (markerType.IsAssignableFrom(shown[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Generic form of <see cref="IsMarkerShown(CardModel?,Type)"/> for a known marker type.
    /// </summary>
    public static bool IsMarkerShown<TMarker>(CardModel? card)
        where TMarker : MarkerEnchantmentModel =>
        IsMarkerShown(card, typeof(TMarker));

    /// <summary>
    /// Returns <c>true</c> when <paramref name="card"/> carries any gameplay enchantment,
    /// excluding <see cref="MarkerEnchantmentModel"/> marker icons by default.
    /// </summary>
    public static bool HasAnyEnchantment(CardModel? card) =>
        HasAnyEnchantment(card, includeMarkers: false);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="card"/> carries any enchantment. Pass
    /// <paramref name="includeMarkers"/> = <c>true</c> to count lightweight marker icons too.
    /// </summary>
    public static bool HasAnyEnchantment(CardModel? card, bool includeMarkers) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.HasAnyEnchantments(card, includeMarkers);

    /// <summary>
    /// Total number of gameplay enchantment instances on <paramref name="card"/>, excluding
    /// <see cref="MarkerEnchantmentModel"/> marker icons by default. Counts instances, not
    /// distinct types.
    /// </summary>
    public static int GetEnchantmentCount(CardModel? card) =>
        GetEnchantmentCount(card, includeMarkers: false);

    /// <summary>
    /// Total number of enchantment instances on <paramref name="card"/>. Pass
    /// <paramref name="includeMarkers"/> = <c>true</c> to count lightweight marker icons too.
    /// </summary>
    public static int GetEnchantmentCount(CardModel? card, bool includeMarkers) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetEnchantmentTotalCount(card, includeMarkers);

    /// <summary>
    /// Number of instances assignable to <typeparamref name="TEnchantment"/> on
    /// <paramref name="card"/>. Counts instances, not summed <c>Amount</c> — use
    /// <see cref="GetTotalAmount{TEnchantment}(CardModel?)"/> for that.
    /// </summary>
    public static int GetEnchantmentCount<TEnchantment>(CardModel? card)
        where TEnchantment : EnchantmentModel =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport
            .GetEnchantmentsForType(card, typeof(TEnchantment))
            .OfType<TEnchantment>()
            .Count();

    /// <summary>
    /// Number of instances assignable to <paramref name="enchantmentType"/> on <paramref name="card"/>.
    /// Counts instances, not summed <c>Amount</c> — use
    /// <see cref="GetTotalAmount(CardModel?,Type)"/> for that.
    /// </summary>
    public static int GetEnchantmentCount(CardModel? card, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"{enchantmentType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(enchantmentType));
        }

        return global::MultiEnchantmentMod.MultiEnchantmentSupport
            .GetEnchantmentsForType(card, enchantmentType)
            .Count(enchantment => enchantmentType.IsInstanceOfType(enchantment));
    }

    /// <summary>
    /// Sum of <c>Amount</c> across every instance assignable to <typeparamref name="TEnchantment"/>
    /// on <paramref name="card"/>. Answers "how much Sharp does this card have in total" when the
    /// type stacks into multiple instances.
    /// </summary>
    public static int GetTotalAmount<TEnchantment>(CardModel? card)
        where TEnchantment : EnchantmentModel =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport
            .GetEnchantmentsForType(card, typeof(TEnchantment))
            .OfType<TEnchantment>()
            .Sum(static enchantment => enchantment.Amount);

    /// <summary>
    /// Sum of <c>Amount</c> across every instance assignable to
    /// <paramref name="enchantmentType"/> on <paramref name="card"/>.
    /// </summary>
    public static int GetTotalAmount(CardModel? card, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(enchantmentType);
        if (!typeof(EnchantmentModel).IsAssignableFrom(enchantmentType))
        {
            throw new ArgumentException(
                $"{enchantmentType.FullName} is not an {nameof(EnchantmentModel)} subclass.",
                nameof(enchantmentType));
        }

        return global::MultiEnchantmentMod.MultiEnchantmentSupport
            .GetEnchantmentsForType(card, enchantmentType)
            .Where(enchantment => enchantmentType.IsInstanceOfType(enchantment))
            .Sum(enchantment => enchantment.Amount);
    }

    /// <summary>
    /// Returns the current live enchantment instance most recently applied or merged onto
    /// <paramref name="card"/>. For <c>MergeAmount</c>, repeated applications continue to return
    /// the anchor instance that received the latest merge delta. Returns <c>null</c> when the
    /// card currently has no enchantments.
    /// </summary>
    public static EnchantmentModel? GetMostRecentlyAppliedEnchantment(CardModel? card) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetMostRecentlyAppliedEnchantment(card);

    /// <summary>
    /// Returns the enchantment most recently applied to <paramref name="card"/> during the current
    /// player turn, or <c>null</c> when nothing has been applied since the turn started. The pointer
    /// resets at the start of every player turn, so this answers "the enchantment I last injected
    /// <em>this turn</em>" for downstream re-injection cards. Unlike
    /// <see cref="GetMostRecentlyAppliedEnchantment"/> it does not fall back to pre-existing
    /// enchantments. This is transient runtime state and is never persisted to the save sidecar.
    /// </summary>
    public static EnchantmentModel? GetMostRecentlyAppliedEnchantmentThisTurn(CardModel? card) =>
        global::MultiEnchantmentMod.MultiEnchantmentSupport.GetMostRecentlyAppliedEnchantmentThisTurn(card);

    /// <summary>
    /// Clones <paramref name="source"/> and reapplies the clone onto <paramref name="target"/>
    /// through the normal v2 application pipeline. This preserves the source instance's mutable
    /// state (<c>Amount</c>, <c>Props</c>, custom fields). By default it resets runtime scope
    /// counters (remaining turns / activation counts) so the copy starts a fresh lifetime; pass
    /// <paramref name="preserveScopeProgress"/> = <c>true</c> to carry the source's live counters
    /// over instead (used by "move" semantics — see <see cref="MoveEnchantment"/>).
    /// Returns <c>null</c> when rejected by a gameplay/scope guard. As with <see cref="Enchant"/>, a
    /// target that fails its <c>CanEnchant</c> rules throws <see cref="InvalidOperationException"/>
    /// rather than returning <c>null</c>.
    /// </summary>
    public static EnchantmentModel? CopyEnchantment(
        CardModel target,
        EnchantmentModel source,
        EnchantmentScope? scopeOverride = null,
        bool preserveScopeProgress = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        if (!global::MultiEnchantmentMod.MultiEnchantmentSupport.IsGameplayEnchantment(source))
        {
            return null;
        }

        if (scopeOverride != null && global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.RejectNonPersistableScopeOverride(scopeOverride, nameof(CopyEnchantment), source))
        {
            return null;
        }

        return global::MultiEnchantmentMod.MultiEnchantmentSupport.CopyEnchantment(
            choiceContext: null,
            target,
            source,
            scopeOverride,
            preserveScopeProgress);
    }

    /// <summary>
    /// Async variant of <see cref="CopyEnchantment"/> that forwards an optional
    /// <see cref="PlayerChoiceContext"/> into post-application notifications.
    /// </summary>
    public static Task<EnchantmentModel?> CopyEnchantmentAsync(
        PlayerChoiceContext? choiceContext,
        CardModel target,
        EnchantmentModel source,
        EnchantmentScope? scopeOverride = null,
        bool preserveScopeProgress = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        if (!global::MultiEnchantmentMod.MultiEnchantmentSupport.IsGameplayEnchantment(source))
        {
            return Task.FromResult<EnchantmentModel?>(null);
        }

        if (scopeOverride != null && global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.RejectNonPersistableScopeOverride(scopeOverride, nameof(CopyEnchantmentAsync), source))
        {
            return Task.FromResult<EnchantmentModel?>(null);
        }

        return global::MultiEnchantmentMod.MultiEnchantmentSupport.CopyEnchantmentAsync(
            choiceContext,
            target,
            source,
            scopeOverride,
            preserveScopeProgress);
    }

    /// <summary>
    /// Moves <paramref name="enchantment"/> from <paramref name="source"/> to
    /// <paramref name="target"/>: copies it (preserving its live scope progress — remaining turns /
    /// activations) and then removes the original from <paramref name="source"/>. Returns the new
    /// instance on <paramref name="target"/>, or <c>null</c> when the move is rejected by a
    /// gameplay/scope guard (in which case the source is left untouched). When
    /// <paramref name="source"/> and <paramref name="target"/> are the same card this is a no-op and
    /// returns <paramref name="enchantment"/> unchanged. As with <see cref="Enchant"/>, a target that
    /// fails its <c>CanEnchant</c> rules surfaces as an <see cref="InvalidOperationException"/> rather
    /// than <c>null</c>; the source is left untouched in that case. Use for
    /// "将其附魔移动到另一张手牌" effects.
    /// </summary>
    public static EnchantmentModel? MoveEnchantment(
        CardModel source,
        CardModel target,
        EnchantmentModel enchantment,
        EnchantmentScope? scopeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(enchantment);
        if (!global::MultiEnchantmentMod.MultiEnchantmentSupport.IsGameplayEnchantment(enchantment))
        {
            return null;
        }

        // Same-card "move" would copy the enchantment onto itself (merging/doubling its amount for
        // MergeAmount behaviors) and then remove it — losing it entirely. It is already where it
        // belongs, so leave it in place.
        if (ReferenceEquals(source, target))
        {
            return enchantment;
        }

        EnchantmentModel? applied = CopyEnchantment(target, enchantment, scopeOverride, preserveScopeProgress: true);
        if (applied == null)
        {
            return null;
        }

        RemoveEnchantment(source, enchantment, RemovalReason.Manual);
        return applied;
    }

    internal static async Task<BeforeCardEnchantedContext> DispatchBeforeCardEnchanted(BeforeCardEnchantedContext context)
    {
        BeforeCardEnchantedContext scoped = context with { CascadeDepth = _beforeCardEnchantedDepth };
        _beforeCardEnchantedDepth++;
        try
        {
            foreach (BeforeCardEnchantedHandler handler in BeforeCardEnchantedHandlers.ToList())
            {
                try
                {
                    await handler(scoped);
                }
                catch (Exception ex)
                {
                    string targetName = handler.Target?.GetType().FullName ?? "<static>";
                    global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Error(
                        $"[MultiEnchantment] BeforeCardEnchanted handler {targetName} threw: {ex}");
                }

                if (scoped.Cancelled || scoped.ModifiedAmount <= 0)
                {
                    scoped.Cancelled = true;
                    break;
                }
            }
        }
        finally
        {
            _beforeCardEnchantedDepth--;
        }

        return scoped;
    }

    internal static async Task DispatchAfterCardEnchanted(AfterCardEnchantedContext context)
    {
        // Stamp the current nesting depth so cascade-style handlers can bail out instead of
        // recursing forever, then increment for any enchant the handlers themselves trigger.
        AfterCardEnchantedContext scoped = context with { CascadeDepth = _afterCardEnchantedDepth };
        _afterCardEnchantedDepth++;
        try
        {
            foreach (AfterCardEnchantedHandler handler in AfterCardEnchantedHandlers.ToList())
            {
                try
                {
                    await handler(scoped);
                }
                catch (Exception ex)
                {
                    string targetName = handler.Target?.GetType().FullName ?? "<static>";
                    global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Error(
                        $"[MultiEnchantment] AfterCardEnchanted handler {targetName} threw: {ex}");
                }
            }
        }
        finally
        {
            _afterCardEnchantedDepth--;
        }
    }

    private static readonly HashSet<string> SupportedFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        "BeforeCardEnchanted",
        "AfterCardEnchanted",
        "OnCardUpgraded",
        "OnCardDowngraded",
        "ModifyHandDraw",
        "ModifyEnergyCostInCombat",
        "ModifyCardPlayCount",
        "ModifyDynamicVar",
        "Marker",
        "IconState",
        "EventBus",
        "BatchQuery",
        "RightAligned",
        "CopyEnchantment",
        "MoveEnchantment",
        "ScopeOverride",
        "RemoveWhen",
        "MaxActivations",
        "StackOverflowPolicy",
    };

    /// <summary>
    /// Returns <c>true</c> when the running MultiEnchantmentMod version supports the named feature.
    /// Use this instead of <see cref="RequireApiVersion"/> when you only need one specific capability
    /// rather than a full version gate. Feature names are case-insensitive.
    /// </summary>
    /// <example><code>
    /// if (MultiEnchantmentApi.SupportsFeature("BeforeCardEnchanted"))
    /// {
    ///     MultiEnchantmentApi.BeforeCardEnchanted(MyHandler);
    /// }
    /// </code></example>
    public static bool SupportsFeature(string feature) =>
        SupportedFeatures.Contains(feature);

    /// <summary>
    /// Returns <c>true</c> when the runtime's API version is at least <paramref name="minimum"/>.
    /// Third-party mods should call this from their initializer to fail-fast on mismatched
    /// MultiEnchantmentMod versions. Logs an error when the check fails so the user has a
    /// breadcrumb in the game log explaining why a feature went silent.
    /// </summary>
    public static bool RequireApiVersion(int minimum)
    {
        if (CurrentVersion >= minimum)
        {
            return true;
        }

        global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Error(
            $"[StackApi] Caller requires MultiEnchantmentMod API v{minimum} but runtime is v{CurrentVersion}. " +
            "The dependent mod's enchantment registrations will not run; update MultiEnchantmentMod.");
        return false;
    }

    private sealed class BeforeCardEnchantedSubscription : IDisposable
    {
        private BeforeCardEnchantedHandler? _handler;

        public BeforeCardEnchantedSubscription(BeforeCardEnchantedHandler handler)
        {
            _handler = handler;
        }

        public void Dispose()
        {
            if (_handler is not { } handler)
            {
                return;
            }

            _handler = null;
            while (BeforeCardEnchantedHandlers.Remove(handler))
            {
            }
        }
    }

    private sealed class AfterCardEnchantedSubscription : IDisposable
    {
        private AfterCardEnchantedHandler? _handler;

        public AfterCardEnchantedSubscription(AfterCardEnchantedHandler handler)
        {
            _handler = handler;
        }

        public void Dispose()
        {
            if (_handler is not { } handler)
            {
                return;
            }

            _handler = null;
            while (AfterCardEnchantedHandlers.Remove(handler))
            {
            }
        }
    }

    /// <summary>
    /// Scans <paramref name="assembly"/> for v2 enchantment registrations (attribute-tagged
    /// <see cref="EnchantmentModel"/> subclasses and <see cref="EnchantmentDefinition{TEnchantment}"/>
    /// subclasses). Idempotent: re-scanning the same assembly does nothing. Returns the number of
    /// new registrations performed.
    /// </summary>
    public static int ScanAssembly(Assembly assembly) =>
        AssemblyScanner.ScanAssembly(assembly);

    /// <summary>
    /// Convenience wrapper that scans the caller's assembly. The recommended integration point
    /// for third-party mods: call this from <c>[ModInitializer]</c>. Resolution uses
    /// <see cref="Assembly.GetCallingAssembly"/>, which inspects the runtime stack frame — do
    /// not call through reflection / dispatch helpers; pass the assembly explicitly to
    /// <see cref="ScanAssembly"/> instead.
    /// </summary>
    public static int ScanCallingAssembly() =>
        AssemblyScanner.ScanAssembly(Assembly.GetCallingAssembly());

    /// <summary>
    /// Freezes the registry. After this call, <see cref="ScanAssembly"/> logs a warning and
    /// does nothing, and the lazy first-Resolve scan becomes a no-op. Use it once the game has
    /// entered active gameplay and no further mod loading is expected.
    /// </summary>
    public static void SealRegistry() =>
        AssemblyScanner.Seal();

    /// <summary>
    /// Notifies the framework that <paramref name="enchantment"/>'s
    /// <see cref="EnchantmentModel.Props"/> have been mutated outside the normal pipeline (e.g.
    /// author wrote <c>enchantment.Props.strings["xyz"] = "new"</c>). Triggers a full derived-state
    /// refresh: DynamicVars recalculation, keyword re-evaluation, and UI
    /// <c>EnchantmentChanged</c> signal.
    /// </summary>
    /// <remarks>
    /// Without this call, mutations to <see cref="EnchantmentModel.Props"/> are invisible to
    /// DynamicVars, card preview, and tooltip rendering until the next full-card refresh cycle
    /// (which may never happen for cosmetic-only fields). Call this immediately after writing
    /// to Props.
    /// </remarks>
    public static void NotifyPropsChanged(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        global::MultiEnchantmentMod.MultiEnchantmentSupport.RefreshDerivedStateFor(enchantment);
    }

    // --- Advanced query API (power-user / tools) ---------------------------------------------

    /// <summary>
    /// Returns the runtime scope state view for <paramref name="enchantment"/> (activation count,
    /// turns remaining, scope kind). Returns <c>null</c> when the enchantment has no scope state
    /// (e.g. permanent scope with no counters) or when <paramref name="enchantment"/> has no
    /// owning card.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static ScopeRuntimeStateView? GetScopeState(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        CardModel? card = enchantment.Card;
        if (card == null) return null;
        if (!global::MultiEnchantmentMod.MultiEnchantmentSupport.TryGetExistingScopeState(card, enchantment, out var state) || state == null)
            return null;
        return new ScopeRuntimeStateView(state.Scope, state.ActivationCount, state.TurnsRemaining, state.OverrideScope is not null);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="enchantment"/> is currently active (not disabled
    /// by a <see cref="IEnchantmentRegistration.WhenActive"/> predicate or scope gate). Useful
    /// from custom active predicates and debug overlays.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static bool IsActive(EnchantmentModel enchantment)
    {
        ArgumentNullException.ThrowIfNull(enchantment);
        CardModel? card = enchantment.Card;
        if (card == null) return false;
        return global::MultiEnchantmentMod.MultiEnchantmentScopeSupport.IsActive(card, enchantment);
    }

    /// <summary>
    /// Returns all enchantments on <paramref name="card"/>, optionally excluding
    /// <paramref name="excludingSelf"/>. Lighter-weight alternative to
    /// <c>Snapshots.ForCard</c> when you only need the sibling list, not full stack metadata.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static IReadOnlyList<EnchantmentModel> GetSiblings(CardModel? card, EnchantmentModel? excludingSelf = null)
    {
        if (card == null) return Array.Empty<EnchantmentModel>();
        IEnumerable<EnchantmentModel> all = global::MultiEnchantmentMod.MultiEnchantmentSupport.GetGameplayEnchantments(card);
        if (excludingSelf != null)
        {
            all = all.Where(e => !ReferenceEquals(e, excludingSelf));
        }
        return all.ToList();
    }

    // --- Batch query / mutation API ----------------------------------------------------------

    /// <summary>
    /// Returns every card in <paramref name="cards"/> that carries at least one enchantment
    /// assignable to <typeparamref name="TEnchantment"/>.
    /// </summary>
    public static IReadOnlyList<CardModel> GetCardsWithEnchantment<TEnchantment>(IEnumerable<CardModel> cards)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards.Where(HasEnchantment<TEnchantment>).ToList();
    }

    /// <summary>
    /// Returns every card in <paramref name="cards"/> that carries at least one enchantment
    /// assignable to <paramref name="enchantmentType"/>.
    /// </summary>
    public static IReadOnlyList<CardModel> GetCardsWithEnchantment(IEnumerable<CardModel> cards, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(enchantmentType);
        return cards.Where(card => HasEnchantment(card, enchantmentType)).ToList();
    }

    /// <summary>
    /// Returns every card in <paramref name="cards"/> that carries at least one gameplay
    /// enchantment, paired with the list of enchantments on that card.
    /// </summary>
    public static IReadOnlyList<(CardModel Card, IReadOnlyList<EnchantmentModel> Enchantments)> GetAllEnchantedCards(
        IEnumerable<CardModel> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        List<(CardModel, IReadOnlyList<EnchantmentModel>)> result = new();
        foreach (CardModel card in cards)
        {
            IReadOnlyList<EnchantmentModel> enchantments = GetEnchantments(card);
            if (enchantments.Count > 0)
            {
                result.Add((card, enchantments));
            }
        }
        return result;
    }

    /// <summary>
    /// Sum of <c>Amount</c> across every instance assignable to <typeparamref name="TEnchantment"/>
    /// on every card in <paramref name="cards"/>.
    /// </summary>
    public static int GetTotalAmountAcrossCards<TEnchantment>(IEnumerable<CardModel> cards)
        where TEnchantment : EnchantmentModel
    {
        ArgumentNullException.ThrowIfNull(cards);
        int total = 0;
        foreach (CardModel card in cards)
        {
            total += GetTotalAmount<TEnchantment>(card);
        }
        return total;
    }

    /// <summary>
    /// Sum of <c>Amount</c> across every instance assignable to <paramref name="enchantmentType"/>
    /// on every card in <paramref name="cards"/>.
    /// </summary>
    public static int GetTotalAmountAcrossCards(IEnumerable<CardModel> cards, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(enchantmentType);
        int total = 0;
        foreach (CardModel card in cards)
        {
            total += GetTotalAmount(card, enchantmentType);
        }
        return total;
    }

    /// <summary>
    /// Removes all instances assignable to <typeparamref name="TEnchantment"/> from every card in
    /// <paramref name="cards"/>. Returns the number of cards that had at least one instance removed.
    /// </summary>
    public static int RemoveEnchantmentFromAll<TEnchantment>(IEnumerable<CardModel> cards)
        where TEnchantment : EnchantmentModel =>
        RemoveEnchantmentFromAll(cards, typeof(TEnchantment));

    /// <summary>
    /// Removes all instances assignable to <paramref name="enchantmentType"/> from every card in
    /// <paramref name="cards"/>. Returns the number of cards that had at least one instance removed.
    /// </summary>
    public static int RemoveEnchantmentFromAll(IEnumerable<CardModel> cards, Type enchantmentType)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(enchantmentType);
        int affected = 0;
        foreach (CardModel card in cards)
        {
            bool removedAny = false;
            foreach (EnchantmentModel enchantment in GetEnchantments(card, includeMarkers: true)
                         .Where(e => enchantmentType.IsInstanceOfType(e))
                         .ToList())
            {
                removedAny |= RemoveEnchantment(card, enchantment);
            }
            if (removedAny)
            {
                affected++;
            }
        }
        return affected;
    }

    // --- Cross-enchantment event bus ---------------------------------------------------------

    /// <summary>
    /// Subscribes a synchronous handler to events of type <typeparamref name="TEvent"/>. The handler
    /// is invoked by <see cref="Publish{TEvent}"/>. Dispose the returned handle to unsubscribe.
    /// </summary>
    public static IDisposable Subscribe<TEvent>(Action<TEvent> handler) =>
        EnchantmentEventBus.Subscribe(handler);

    /// <summary>
    /// Subscribes an asynchronous handler to events of type <typeparamref name="TEvent"/>. The handler
    /// is invoked only by <see cref="PublishAsync{TEvent}"/>; <see cref="Publish{TEvent}"/> skips async
    /// handlers. Dispose the returned handle to unsubscribe.
    /// </summary>
    public static IDisposable Subscribe<TEvent>(Func<Task> handler) where TEvent : class =>
        EnchantmentEventBus.Subscribe<TEvent>(_ => handler());

    /// <summary>
    /// Subscribes an asynchronous handler to events of type <typeparamref name="TEvent"/>. The handler
    /// is invoked only by <see cref="PublishAsync{TEvent}"/>. Dispose the returned handle to unsubscribe.
    /// </summary>
    public static IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) =>
        EnchantmentEventBus.Subscribe(handler);

    /// <summary>
    /// Publishes an event to all synchronous subscribers. Async subscribers are skipped; use
    /// <see cref="PublishAsync{TEvent}"/> to invoke both sync and async handlers.
    /// </summary>
    public static void Publish<TEvent>(TEvent evt) =>
        EnchantmentEventBus.Publish(evt);

    /// <summary>
    /// Publishes an event to all subscribers (sync and async), awaiting async handlers in order.
    /// </summary>
    public static Task PublishAsync<TEvent>(TEvent evt) =>
        EnchantmentEventBus.PublishAsync(evt);

    // --- Advanced read-only snapshot API -----------------------------------------------------

    /// <summary>
    /// Power-user accessors that mirror <c>MultiEnchantmentStackApi.GetSnapshot</c>
    /// surface. Reserved for tools, debug overlays, and analyzer-driven content. Most consumers
    /// do not need these.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static class Snapshots
    {
        public static global::MultiEnchantmentMod.EnchantmentStackSnapshot Get(EnchantmentModel enchantment) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetSnapshot(enchantment);

        public static System.Collections.Generic.IReadOnlyList<global::MultiEnchantmentMod.EnchantmentStackSnapshot> ForCard(
            MegaCrit.Sts2.Core.Models.CardModel? card) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetSnapshots(card);

        public static global::MultiEnchantmentMod.HookExecutionMode ExecutionMode(
            Type enchantmentType,
            global::MultiEnchantmentMod.EnchantmentHookKind hookKind) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetExecutionMode(enchantmentType, hookKind);

        public static int HookExecutionCount(
            EnchantmentModel enchantment,
            global::MultiEnchantmentMod.EnchantmentHookKind hookKind) =>
            global::MultiEnchantmentMod.MultiEnchantmentStackApi.GetHookExecutionCount(enchantment, hookKind);
    }
}

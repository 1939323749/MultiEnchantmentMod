# MultiEnchantmentMod Public API Baseline

This file is the human-maintained snapshot of the **`MultiEnchantmentMod.Api`** namespace as
shipped today. It exists so that any PR touching public surface area is reviewable as a diff
against this baseline. **If you change the signature of any public type or member listed here —
or add/remove one — update this file in the same PR.**

> Scope: only types under `MultiEnchantmentMod.Api*` namespaces. Public types in the legacy
> `MultiEnchantmentMod` namespace (`EnchantmentStackSnapshot`, `HookExecutionMode`,
> `EnchantmentStackBehavior`, `EnchantmentStatusAggregation`, `EnchantmentHookKind`,
> `EnchantmentStackDefinition`, `EnchantmentExecutionPolicy`, `EnchantmentStackSlice`,
> `EnchantmentVisualSlice`, `MultiEnchantmentStackApi`) are part of the v2 contract but kept where they are for source
> compatibility; see the header of `MultiEnchantmentStackApi.cs` for the deferral rationale.

## Conventions
- All members are `public` unless noted.
- `IDisposable` returned by registration paths reverses the registration.
- Predicates / callbacks marked **author-supplied** are routed through
  `Api.Internal.SafeInvoker` at runtime; exceptions are logged with the enchantment type and
  assembly, then the call is skipped (or returns the documented fallback).

---

## Namespace: `MultiEnchantmentMod.Api`

### `static class MultiEnchantmentApi`
Top-level facade.

- `static int CurrentVersion { get; }`
- `static IEnchantmentRegistration Register<TEnchantment>() where TEnchantment : EnchantmentModel`
- `static IEnchantmentRegistration Register(Type enchantmentType)` — throws `ArgumentException` on contract violation (null / not `EnchantmentModel`-derived).
- `static bool RemoveEnchantment(CardModel card, EnchantmentModel enchantment, RemovalReason reason = RemovalReason.Manual)`
- `static EnchantmentModel? Enchant(CardModel card, EnchantmentModel enchantment, decimal amount = 1, EnchantmentScope? scopeOverride = null)` — applies an enchantment through the v2 pipeline with an optional persistable per-instance scope override; rejects predicate-bearing scopes by returning `null` and logging a warning.
- `static Task<EnchantmentModel?> EnchantAsync(PlayerChoiceContext? choiceContext, CardModel card, EnchantmentModel enchantment, decimal amount = 1, EnchantmentScope? scopeOverride = null)` — async fresh-application pipeline that forwards an optional choice context into command-capable post-application hooks and card-level notifications.
- `static IDisposable AfterCardEnchanted(AfterCardEnchantedHandler handler)` — subscribes to a card-level notification fired by async fresh-application paths after an enchantment has been successfully applied and is live on the card. **Only `EnchantAsync` / `CopyEnchantmentAsync` raise it** — synchronous `Enchant` / `CopyEnchantment` and vanilla enchant paths do not.
- `static bool SetScopeOverride(CardModel card, EnchantmentModel enchantment, EnchantmentScope? newScope)` — changes or clears the per-instance scope override on an attached enchantment; returns `false` when rejected or not attached.
- `static IDisposable RegisterExtraIconDisplayProvider(ExtraIconDisplayProvider provider)` - registers display-only extra icons for card UI refreshes, including library / preview cards that have no live enchantment instance.
- `static IDisposable RegisterExtraIcon<TEnchantment>(Func<CardModel, bool> appliesTo, EnchantmentPresentationStyle? presentationStyle = null, ExtraIconDisplayPredicate? shouldDisplay = null) where TEnchantment : ExtraIconEnchantmentModel, new()` - convenience wrapper for static marker icons keyed by a card predicate. **Delete** a registration by disposing the returned `IDisposable`.
- `static IDisposable RegisterExtraIcon<TEnchantment>(Func<CardModel, bool> appliesTo, Texture2D? icon, EnchantmentPresentationStyle? presentationStyle = null, ExtraIconDisplayPredicate? shouldDisplay = null) where TEnchantment : ExtraIconEnchantmentModel, new()` - overload that supplies an explicit icon texture, the way to use custom art (`EnchantmentModel.Icon` is non-virtual / not overridable).
- `static void RefreshExtraIcons(CardModel? card)` - re-runs display providers and redraws extra-icon badges for one card now (so provider edits / disposals show up on an already-rendered card such as a compendium entry instead of waiting for the next visual pass). No-op for null.
- `static void RefreshExtraIcons()` - same, for every currently-rendered card; use after changing a global condition many providers read.
- `static bool HasEnchantment<TEnchantment>(CardModel? card) where TEnchantment : EnchantmentModel`
- `static bool HasEnchantment(CardModel? card, Type enchantmentType)`
- `static bool HasAnyEnchantment(CardModel? card)` - `true` when the card carries any gameplay enchantment, excluding `ExtraIconEnchantmentModel` marker icons by default.
- `static bool HasAnyEnchantment(CardModel? card, bool includeExtraIcons)` - pass `true` to count lightweight marker icons too.
- `static int GetEnchantmentCount(CardModel? card)` - total gameplay enchantment **instances** on the card, excluding `ExtraIconEnchantmentModel` marker icons by default.
- `static int GetEnchantmentCount(CardModel? card, bool includeExtraIcons)` - pass `true` to count lightweight marker icons too.
- `static EnchantmentModel? GetMostRecentlyAppliedEnchantment(CardModel? card)` — returns the current live instance most recently applied or merged onto the card, or `null` when none exist.
- `static EnchantmentModel? GetMostRecentlyAppliedEnchantmentThisTurn(CardModel? card)` — returns the enchantment most recently applied during the current player turn, or `null` when nothing was applied since the turn started. Resets at the start of every player turn; unlike the unscoped variant it does not fall back to pre-existing enchantments. Transient (never persisted).
- `static EnchantmentModel? CopyEnchantment(CardModel target, EnchantmentModel source, EnchantmentScope? scopeOverride = null, bool preserveScopeProgress = false)` — clones a live enchantment instance and reapplies it through the fresh-application pipeline. Resets runtime scope counters by default; pass `preserveScopeProgress: true` to carry the source's remaining turns/activations over.
- `static Task<EnchantmentModel?> CopyEnchantmentAsync(PlayerChoiceContext? choiceContext, CardModel target, EnchantmentModel source, EnchantmentScope? scopeOverride = null, bool preserveScopeProgress = false)` — async copy/reapply variant that forwards choice context into post-application notifications.
- `static EnchantmentModel? MoveEnchantment(CardModel source, CardModel target, EnchantmentModel enchantment, EnchantmentScope? scopeOverride = null)` — copies `enchantment` to `target` preserving scope progress, then removes it from `source`. Returns `null` (leaving the source untouched) when the copy is rejected. For "将其附魔移动到另一张手牌".
- `static bool RequireApiVersion(int minimum)` — logs error + returns `false` on mismatch.
- `static int ScanAssembly(Assembly assembly)`
- `static int ScanCallingAssembly()`
- `static void SealRegistry()`
- `static void NotifyPropsChanged(EnchantmentModel enchantment)` — refresh derived UI/dynamic-var state after mutating fields outside the application path.
- `[EditorBrowsable(Advanced)] static ScopeRuntimeStateView? GetScopeState(EnchantmentModel)` — current scope state snapshot, or `null` for no-scope.
- `[EditorBrowsable(Advanced)] static bool IsActive(EnchantmentModel)` — current `IsActive` evaluation (ConditionalActive + scope limits).
- `[EditorBrowsable(Advanced)] static IReadOnlyList<EnchantmentModel> GetSiblings(CardModel?, EnchantmentModel? excludingSelf = null)` — same-card neighbors.
- `[EditorBrowsable(Advanced)] static class Snapshots`
  - `static EnchantmentStackSnapshot Get(EnchantmentModel)`
  - `static IReadOnlyList<EnchantmentStackSnapshot> ForCard(CardModel?)`
  - `static HookExecutionMode ExecutionMode(Type, EnchantmentHookKind)`
  - `static int HookExecutionCount(EnchantmentModel, EnchantmentHookKind)`

### `static class MultiEnchantmentApiVersion`
- `const int Current = 2`

### `interface IEnchantmentDefinition`
Non-generic facade for assembly scanning.
- `Type EnchantmentType { get; }`
- `IDisposable Register()`

### `abstract class EnchantmentDefinition<TEnchantment> : IEnchantmentDefinition where TEnchantment : EnchantmentModel`
Tier B definition base class. Subclass and override the virtual hooks below; `Register()` is implemented for you. Override surface mirrors `IEnchantmentRegistration` — including `OnAnyCardPlayed/Drawn/Exhausted/Discarded` (broadcast variants of the per-card hooks) and `OnSiblingApplied/OnSiblingRemoved` (same-card neighbor events). Override detection at scan time keys off `MethodInfo.DeclaringType` so unmodified base methods stay uninstalled.

### `interface IEnchantmentRegistration`
Tier C fluent builder. Methods chain and return `this`. **Author-supplied** delegates are `SafeInvoker`-wrapped at runtime.

- `Type EnchantmentType { get; }`
- `Stack(StackBehavior behavior, StatusAggregation status)`
- `Stack(StackDefinition definition)` — sets the full record (including `MaxInstances` / `OnOverflow`). Default-implemented; pre-existing implementations fall back to the two-arg overload (cap / overflow silently dropped on those).
- `Execution(Action<ExecutionPolicyBuilder> configure)`
- `OnMergedDelta(Action<EnchantmentModel, int> action)`
- `OnMergedRefresh(Action<EnchantmentModel> action)`
- `OnRestored(Action<CardModel, EnchantmentModel> handler)`
- `OnCardPlayed`, `OnCardDrawn`, `OnCardExhausted`, `OnCardDiscarded`, `OnCardEnteredCombat` — `Action<CardModel, EnchantmentModel>` each.
- `OnAnyCardPlayed`, `OnAnyCardDrawn`, `OnAnyCardExhausted`, `OnAnyCardDiscarded` — `Action<CardModel /*event card*/, CardModel /*selfCard*/, EnchantmentModel>` each. **Opt-in broadcast** counterpart of the per-card hooks; only attached when the author overrides them.
- `OnSiblingApplied(Action<CardModel, EnchantmentModel /*self*/, EnchantmentModel /*newSibling*/>)` — fires after a different enchantment is attached to the same card.
- `OnSiblingRemoved(Action<CardModel, EnchantmentModel /*self*/, EnchantmentModel /*removedSibling*/, RemovalReason>)` — fires before a sibling is detached, only if the OnRemoved veto chain accepted the removal.
- `OnAfterDamageReceived(Action<CardModel, EnchantmentModel, DamageReceivedContext>)`
- `OnSideTurnStart`, `OnBeforeSideTurnStart` — `Action<CardModel, EnchantmentModel, CombatSide>`
- `OnBeforeAttack`, `OnAfterAttack` — `Action<CardModel, EnchantmentModel, AttackCommand>`
- `OnCardChangedPiles(Action<CardModel, EnchantmentModel, PileType, AbstractModel?>)`
- `OnCardRetained(Action<CardModel, EnchantmentModel>)`
- `OnBeforeBlockGained`, `OnBlockGained` — `Action<CardModel, EnchantmentModel, BlockGainContext>`
- `OnShouldDie(Func<CardModel, EnchantmentModel, Creature, bool>)` — return `false` to veto death.
- `WithScope(EnchantmentScope)`
- `LingerForTurns(int turns)`
- `MaxActivations(int n, ActivationTrigger? t = null)`
- `WhenActive(Func<CardModel, EnchantmentModel, bool> predicate)`
- `WhenActiveStatus(Func<CardModel, EnchantmentModel, bool> predicate)` — gates gameplay callbacks and drives active/disabled badge status without replacing the lifetime scope.
- `RemoveWhen(Func<CardModel, EnchantmentModel, bool> predicate, params ActivationTrigger[] checkOn)`
- `OnApplied(Action<CardModel, EnchantmentModel>)`
- `OnRemoved(Func<CardModel, EnchantmentModel, RemovalReason, bool>)` — return `false` to veto removal.
- `OnCombatStart`, `OnCombatEnd`, `OnTurnStart`, `OnTurnEnd` — `Action<CardModel, EnchantmentModel>` each.
- `TrackKeyword(CardKeyword keyword, Func<EnchantmentStackSnapshot, int> amountFn)`
- `FormatExtraText(PresentationTextFormatter formatter)`
- `PresentationStyle(EnchantmentPresentationStyle style)` - controls badge backing, extra-text BBCode wrapping, icon scale, and visual display priority.
- `VisualSlices(Func<EnchantmentStackSnapshot, IReadOnlyList<int>?> compute)`
- `VisualSlicesWithStatus(Func<EnchantmentStackSnapshot, IReadOnlyList<EnchantmentVisualSlice>?> compute)`
- `HistoryDisplay(HistoryDisplayMode mode)`
- `HistoryDisplay(HistoryDisplayMode mode, string groupHeader)`
- `HistoryText(HistoryTextFormatter formatter)`
- `ModifyDynamicVar(string varKey, Func<EnchantmentStackSnapshot, decimal, decimal> contribution)`
- `ModifyEnergyCostInCombat(EnergyCostContribution contribution)`
- `ModifyCardPlayCount(CardPlayCountContribution contribution)`
- `OnPlayStacked(StackedOnPlayHandler)`, `BeforeCardPlayedStacked`, `AfterCardPlayedStacked`, `AfterSiblingAppliedStacked`, `AfterCardDrawnStacked`, `AfterAnyCardDrawnStacked`, `BeforeFlushStacked`, `AfterDamageGivenStacked` — stack-aware async hooks invoked once per enchantment type with an `EnchantmentStackSnapshot`.
- `IDisposable Commit()`

#### Reacting to "this card just got enchanted": which hook?

Two surfaces fire after an enchantment is applied. Pick by what the handler needs to do:

| | `AfterCardEnchanted` (`MultiEnchantmentApi`) | `AfterSiblingAppliedStacked` (per-registration hook) |
|---|---|---|
| Granularity | Card-level / global subscription — good for keyword & marker systems (e.g. "激发: when enchanted, auto-play this card") | Per-enchantment-type, with an `EnchantmentStackSnapshot` of the listening enchantment |
| Fires on sync `Enchant` / `CopyEnchantment` | No | Yes (dispatched by blocking — `.GetAwaiter().GetResult()`) |
| Fires on async `EnchantAsync` / `CopyEnchantmentAsync` | Yes (awaited) | Yes (awaited) |
| Safe to run game commands / auto-play | Yes, **only via the async enchant paths** | No — keep to pure state updates; the sync path blocks on it |

Rule of thumb: imperative / command-issuing logic → `AfterCardEnchanted` + `EnchantAsync`. Pure state bookkeeping per enchantment → `AfterSiblingAppliedStacked`.

### `static class EnchantmentRegistrationExtensions`
30+ strongly-typed lambda overloads for `IEnchantmentRegistration` callbacks, generic on the concrete `TEnchantment : EnchantmentModel` so authors don't need to cast inside delegates. Members mirror the interface (`OnApplied<T>`, `OnCardPlayed<T>`, …).

### `abstract record EnchantmentScope`
Sealed-derivative scope shapes:
- `static EnchantmentScope Permanent { get; }`
- `static EnchantmentScope UntilCombatEnds { get; }`
- `static EnchantmentScope UntilTurnEnds { get; }`
- `static EnchantmentScope LingerForTurns(int turns)`
- `static EnchantmentScope MaxActivations(int n, ActivationTrigger? t = null)`
- `static EnchantmentScope ConditionalActive(Func<CardModel, EnchantmentModel, bool> predicate)`
- `static EnchantmentScope RemoveWhen(Func<CardModel, EnchantmentModel, bool> predicate, IEnumerable<ActivationTrigger> checkOn)`
- Nested sealed records: `PermanentScope`, `UntilCombatEndsScope`, `UntilTurnEndsScope`, `LingerForTurnsScope(int Turns)`, `MaxActivationsScope(int Max, ActivationTrigger Trigger)`, `ConditionalActiveScope(Func<…, bool> Predicate)`, `RemoveWhenScope(Func<…, bool> Predicate, IReadOnlyList<ActivationTrigger> CheckOn)`.

### `sealed record ActivationTrigger(string Name)`
- Static instances: `OnPlay`, `AfterCardPlayed`, `AfterCardDrawn`, `AfterCardExhausted`, `AfterCardDiscarded`, `AfterPlayerTurnStart`, `AfterPlayerTurnEnd`, `AfterDamageReceived`.
- `static ActivationTrigger Custom(string identifier)` — cached; reference-equal on repeated identifier.

### `enum ScopeKind`
`Permanent`, `UntilCombatEnds`, `UntilTurnEnds`, `LingerForTurns`, `MaxActivations`, `RemoveWhen`.

### `enum RemovalReason`
`Manual`, `CardCleared`, `CombatEnded`, `TurnEnded`, `TurnLimitReached`, `ActivationLimitReached`, `Replaced`, `ConditionMet`, `OverflowEvicted`.

### `enum StackBehavior`
`DisallowDuplicate`, `MergeAmount`, `DuplicateInstance`, `ExistenceStack`.

### `enum StatusAggregation`
`NotApplicable`, `SharedAcrossStack`, `PerInstanceOwned`, `AnyInstanceCountsAsOne`.

### `enum StackOverflowPolicy`
`Reject` (default), `ReplaceOldest` (FIFO eviction), `ReplaceNewest` (LIFO eviction). Controls behavior when `StackDefinition.MaxInstances` would be exceeded.

### `sealed record EnchantmentPresentationStyle`
Card-UI presentation settings.
- `bool ShowBadgeBacking { get; init; } = true` - set `false` to render only the icon and optional amount label.
- `bool PreserveExtraTextBbCode { get; init; } = false` - set `true` to keep custom extra text BBCode as-is.
- `float IconScale { get; init; } = 1f` - scales the rendered icon relative to the vanilla icon node; invalid values are treated as `1f`.
- `Vector2 IconOffset { get; init; } = Vector2.Zero` - shifts only the rendered icon node.
- `Color? IconTint { get; init; }` - optional tint for active icons.
- `Color? DisabledIconTint { get; init; }` - optional tint for disabled icons; defaults to gray when omitted.
- `Texture2D? BadgeBackingTexture { get; init; }` - optional replacement texture for the badge backing.
- `bool HideWhenDisabled { get; init; } = false` - hides disabled visual entries instead of rendering a dimmed icon.
- `int DisplayPriority { get; init; } = 0` - higher values render before lower-priority enchantment badges; ties keep the existing application order.

### `abstract class ExtraIconEnchantmentModel : EnchantmentModel`
Base class for marker-style enchantments that should behave like lightweight extra icons.
Defaults:
- `HasExtraCardText = false`
- `ShowAmount = false`
- `HistoryDisplay = Hidden`
- `PresentationStyle = ExtraIconPresentation.Default`
Extra-icon markers are excluded from gameplay enchantment reads by default: count/has-any helpers,
siblings, snapshots, lifecycle hooks, numeric/dynamic-var contributions, compatible transform copy,
and copy/move helpers all treat them as UI markers unless an API explicitly exposes
`includeExtraIcons`.
Author-facing guides: `docs/extra-icon-wiki.md`, `docs/extra-icon-wiki.zh.md`.

### `static class ExtraIconPresentation`
- `const int DefaultDisplayPriority = 1000`
- `static EnchantmentPresentationStyle Default { get; }` - `ShowBadgeBacking = false`, `HideWhenDisabled = true`, `DisplayPriority = 1000`.

### `sealed record ExtraIconDisplay`
Display-only extra icon descriptor returned by `ExtraIconDisplayProvider`.
- `required Type EnchantmentType { get; init; }` - the type that keys the marker (dedup / suppression / visual id). Should be an `ExtraIconEnchantmentModel`; a one-time warning is logged for plain gameplay types.
- `Texture2D? Icon { get; init; }` - explicit texture to draw, overriding the resolved model icon. The only way to use arbitrary art, since `EnchantmentModel.Icon` is non-virtual.
- `EnchantmentModel? Enchantment { get; init; }` - optional pre-built model to read the icon from; when both this and `Icon` are null the framework reads `EnchantmentType`'s canonical model icon from `ModelDb` (it never constructs the model).
- `EnchantmentPresentationStyle? PresentationStyle { get; init; }`
- `ExtraIconDisplayPredicate? ShouldDisplay { get; init; }`
- `bool ShowAmount { get; init; } = false` - draw an amount label (the only way a marker shows a number, since `ExtraIconEnchantmentModel` hard-disables its own `ShowAmount`).
- `int Amount { get; init; } = 1` - the number drawn when `ShowAmount` is true.
- `bool ShowWithLiveEnchantment { get; init; } = false` - render even when a live enchantment of the same type already occupies a slot (default suppresses the marker so the live badge wins).

### `sealed record ExtraIconDisplayContext(CardModel Card, bool HasLiveEnchantment, bool IsCombatCard, bool IsPreviewCard)`
Context passed to a display-only extra icon predicate.

### `delegate ExtraIconDisplayProvider`
Signature: `IEnumerable<ExtraIconDisplay> ExtraIconDisplayProvider(CardModel card)`.

### `delegate ExtraIconDisplayPredicate`
Signature: `bool ExtraIconDisplayPredicate(ExtraIconDisplayContext context)`.

### `sealed record EnchantmentVisualSlice(int Amount, EnchantmentStatus Status)`
Author-facing visual badge descriptor for `VisualSlicesWithStatus` / `GetVisualSlices`.
- `Type? IconEnchantmentType { get; init; }` — optional icon source enchantment type. The renderer first reuses a live enchantment of that type on the same card, then tries a fresh default instance of that enchantment type before falling back to the current enchantment icon.
- `Texture2D? IconTexture { get; init; }` — optional direct icon texture. Wins over `IconEnchantmentType` when both are set.
- `static EnchantmentVisualSlice Active(int amount)`
- `static EnchantmentVisualSlice Disabled(int amount)`
- `EnchantmentVisualSlice WithIcon<TEnchantment>() where TEnchantment : EnchantmentModel`
- `EnchantmentVisualSlice WithIcon(Type enchantmentType)`
- `EnchantmentVisualSlice WithIcon(Texture2D texture)`

Icon overrides are additive: slices without an override keep using the current enchantment icon. The integer-only `VisualSlices(...)` path cannot override per-badge icons; use `VisualSlicesWithStatus(...)` or `GetVisualSlices(...)` when a badge needs its own icon.

### `sealed record StackDefinition(StackBehavior Behavior, StatusAggregation Status)`
- `static StackDefinition Default { get; }`
- `int? MaxInstances { get; init; }` — optional cap; only enforced for `DuplicateInstance` / `ExistenceStack`. **Added during stabilization, default `null` preserves prior behavior.**
- `StackOverflowPolicy OnOverflow { get; init; }` — defaults to `Reject` (preserves prior behavior). Set to `ReplaceOldest` / `ReplaceNewest` to switch from rejection to FIFO/LIFO eviction; evicted instances see `RemovalReason.OverflowEvicted`.

### `sealed record ScopeRuntimeStateView(EnchantmentScope Scope, int ActivationCount, int TurnsRemaining, bool HasOverride = false)`
- `bool HasOverride` — `true` when this concrete instance uses an apply-time or retroactive scope override rather than only the registry default.
- `bool IsExpired { get; }` — `true` when scope is `LingerForTurnsScope` and `TurnsRemaining <= 0`.
- `bool IsLimitReached { get; }` — `true` when scope is `MaxActivationsScope` and `ActivationCount >= Max`.

Read-only snapshot of an enchantment's runtime scope state. Surfaced through `EnchantmentStackSnapshot.ScopeStates` and `MultiEnchantmentApi.GetScopeState(EnchantmentModel)`.

### `enum KeywordEvalMode`
`PerInstance`, `PerTotalAmount`, `Constant`, `Custom`.

### `enum HistoryDisplayMode`
`Auto`, `InRewards`, `Hidden`, `InActions`, `CustomGroup`.

### `delegate HistoryTextFormatter`
Defined in `HistoryDisplayMode.cs`. Signature:
`string? HistoryTextFormatter(string cardTitle, string enchantmentTitle)`.

### `sealed class ExecutionPolicyBuilder`
- `All(HookExecutionMode)`, `OnEnchant(HookExecutionMode)`, `OnPlay`, `AfterCardPlayed`, `AfterCardDrawn`, `AfterPlayerTurnStart`, `BeforePlayPhaseStart`, `BeforeFlush` — all chain.
- `EnchantmentExecutionPolicy Build()`

### `sealed record DamageReceivedContext(Creature Target, DamageResult Result, Creature? Dealer, CardModel? Source)`
Payload to `OnAfterDamageReceived`.

### `sealed record BlockGainContext(Creature Creature, decimal Amount, CardModel? Source)`
Payload to `OnBeforeBlockGained` / `OnBlockGained`.

### `sealed record DynamicVarContribution(string VarKey, Func<EnchantmentStackSnapshot, decimal, decimal> Contribution)`
Returned by the registry; authors build them implicitly via `IEnchantmentRegistration.ModifyDynamicVar` or the `[ModifyDynamicVar]` attribute.

### Stack-aware hook context records
- `sealed record StackedOnPlayContext(EnchantmentStackSnapshot Snapshot, PlayerChoiceContext ChoiceContext, CardPlay? CardPlay)`
- `sealed record StackedBeforeCardPlayedContext(EnchantmentStackSnapshot Snapshot, CardPlay CardPlay)`
- `sealed record StackedAfterCardPlayedContext(EnchantmentStackSnapshot Snapshot, PlayerChoiceContext ChoiceContext, CardPlay CardPlay)`
- `sealed record StackedAfterSiblingAppliedContext(EnchantmentStackSnapshot Snapshot, PlayerChoiceContext? ChoiceContext, CardModel Card, EnchantmentModel NewSibling)` — `ChoiceContext` is null for synchronous `Enchant(...)` paths; prefer `EnchantAsync(...)` when handlers need commands.
- `sealed record StackedAfterCardDrawnContext(EnchantmentStackSnapshot Snapshot, PlayerChoiceContext ChoiceContext, CardModel DrawnCard, bool FromHandDraw)`
- `sealed record StackedBeforeFlushContext(EnchantmentStackSnapshot Snapshot, PlayerChoiceContext? ChoiceContext, Player Player)` — `ChoiceContext` is currently null in the vanilla bridge; use this hook for synchronous cleanup / state reset only.
- `sealed record StackedAfterDamageGivenContext(EnchantmentStackSnapshot Snapshot, PlayerChoiceContext ChoiceContext, Creature? Dealer, DamageResult Result, ValueProp Props, Creature Target, CardModel? CardSource)`

### Stack-aware delegates
- `delegate Task StackedOnPlayHandler(StackedOnPlayContext context)`
- `delegate Task StackedBeforeCardPlayedHandler(StackedBeforeCardPlayedContext context)`
- `delegate Task StackedAfterCardPlayedHandler(StackedAfterCardPlayedContext context)`
- `delegate Task StackedAfterSiblingAppliedHandler(StackedAfterSiblingAppliedContext context)`
- `delegate Task StackedAfterCardDrawnHandler(StackedAfterCardDrawnContext context)`
- `delegate Task StackedAfterAnyCardDrawnHandler(StackedAfterCardDrawnContext context)`
- `delegate Task StackedBeforeFlushHandler(StackedBeforeFlushContext context)`
- `delegate Task StackedAfterDamageGivenHandler(StackedAfterDamageGivenContext context)`
- `delegate decimal EnergyCostContribution(EnchantmentStackSnapshot snapshot, decimal currentCost)`
- `delegate int CardPlayCountContribution(EnchantmentStackSnapshot snapshot, int currentPlayCount)`

### Card-level enchantment notification
- `sealed record AfterCardEnchantedContext(PlayerChoiceContext? ChoiceContext, CardModel Card, EnchantmentModel AppliedEnchantment, EnchantmentModel RequestedEnchantment, int AppliedAmount, EnchantmentScope? ScopeOverride, int CascadeDepth = 0)` — emitted after a successful async fresh application. `AppliedEnchantment` is the live instance on the card; for `MergeAmount`, it is the merged anchor. `CascadeDepth` is `0` at top level and `> 0` when the application was triggered from inside another handler — cascade cards should early-out when `CascadeDepth > 0`.
- `delegate Task AfterCardEnchantedHandler(AfterCardEnchantedContext context)`

### `delegate PresentationTextFormatter`
Defined in `EnchantmentDefinition.cs`. Signature:
`bool PresentationTextFormatter(EnchantmentStackSnapshot snapshot, string defaultText, out string formattedText)`

---

## Namespace: `MultiEnchantmentMod.Api` (Attributes)

All under `Api/Attributes/`.

- `[AttributeUsage(Class)] sealed class EnchantmentAttribute : Attribute`
  - `StackBehavior Stack { get; init; }`
  - `StatusAggregation Status { get; init; }`
  - `Type? Companion { get; init; }`
  - `ScopeKind Scope { get; init; }`
  - `int MaxActivations { get; init; }`
  - `int LingerTurns { get; init; }`
  - `ActivationTrigger Activation { get; init; }` — public on the type, but not assignable in C# attribute syntax because `ActivationTrigger` is a record rather than an enum/constant attribute type. Use fluent `.MaxActivations(..., trigger)` or an `EnchantmentDefinition` override when you need a non-default trigger.
  - `HistoryDisplayMode HistoryDisplay { get; init; }`
  - `string? HistoryGroupHeader { get; init; }`
- `[AttributeUsage(Class)] sealed class EnchantmentDefinitionAttribute : Attribute` — optional marker with analyzer-only `Stack` / `Status` hints.
- `[AttributeUsage(Class)] sealed class EnchantmentExecutionAttribute : Attribute`
  - `HookExecutionMode All { get; init; }` plus one per hook kind.
- `[AttributeUsage(Class, AllowMultiple=true)] sealed class EnchantmentKeywordAttribute : Attribute`
  - `CardKeyword Keyword`, `KeywordEvalMode Mode`, `int Constant`.
- `[AttributeUsage(Class)] sealed class EnchantmentPresentationAttribute : Attribute`
  - `bool HasExtraText { get; init; }`, `bool HasPresentationStyle { get; init; }`, `bool HasVisualSliceOverride { get; init; }`.
- `[AttributeUsage(Method, AllowMultiple=true)] sealed class ModifyDynamicVarAttribute : Attribute`
  - `string VarKey` — ctor now non-throwing; empty-string keys are caught at scan time (logged + skipped) and by analyzer rule **MEM009** at compile time.
- `[AttributeUsage(Method)] sealed class ModifyEnergyCostAttribute : Attribute`
  - Required signature: `decimal Method(EnchantmentStackSnapshot snapshot, decimal currentCost)`.
- `[AttributeUsage(Method)] sealed class ModifyCardPlayCountAttribute : Attribute`
  - Required signature: `int Method(EnchantmentStackSnapshot snapshot, int currentPlayCount)`.
- `[AttributeUsage(Assembly)] sealed class EnchantmentApiCompatibilityAttribute : Attribute`
  - `int MinVersion`, `int MaxVersion`.

---

## Non-public, intentionally

Everything under `MultiEnchantmentMod.Api.Internal` is `internal`. Registry types
(`EnchantmentEntry`, `EnchantmentRegistry`, `EnchantmentRegistration<T>`), scanners
(`AssemblyScanner`, `ModifyDynamicVarScanner`), runtime helpers (`SafeInvoker`,
`LegacyEnumMappings`) are **not** part of the public contract and may change between releases
without notice. Do not reference them by reflection.

---

## Change log

- _2026-05-31_: Generic enchantment-pack gap fills. Added `HasAnyEnchantment(card)` and `GetEnchantmentCount(card)` for type-agnostic "has any / how many" checks. Added cascade safety to `AfterCardEnchanted`: `AfterCardEnchantedContext.CascadeDepth` (`0` top-level, `> 0` when triggered from inside another handler) so re-enchant cascade cards can early-out. Added `MoveEnchantment(...)` and `CopyEnchantment(..., preserveScopeProgress)` for move-with-lifetime semantics. (Theme-specific "verb" vocabulary such as inject/infuse/awaken is intentionally left to downstream packs, not modelled in this API.)
- _2026-05-30_: Documentation-only public API baseline refresh. Added missing `HistoryDisplayMode` / `HistoryTextFormatter`, `HistoryDisplay(...)` / `HistoryText(...)`, `WhenActiveStatus(...)`, and `[Enchantment]` history properties that were already present in code. Updated game compatibility wording to v0.106.x / local v0.106.1 signature reality (`ICombatState` plus `participants` on side-turn and turn-end hooks).
- _2026-05-25_: Added stack-aware async hook surface (`OnPlayStacked`, `BeforeCardPlayedStacked`, `AfterCardPlayedStacked`, `AfterCardDrawnStacked`, `AfterAnyCardDrawnStacked`, `BeforeFlushStacked`, `AfterDamageGivenStacked`) for side effects that must aggregate prompts, random targets, animations, command execution, or damage results once per enchantment type. Added `ModifyEnergyCostAttribute`, `ModifyCardPlayCountAttribute`, fluent `ModifyEnergyCostInCombat` / `ModifyCardPlayCount`, and analyzer rule MEM013 for numeric contribution signatures.
- _2026-05-31_: Added `MultiEnchantmentApi.EnchantAsync(...)`, `GetMostRecentlyAppliedEnchantment(...)`, `GetMostRecentlyAppliedEnchantmentThisTurn(...)`, `CopyEnchantment(...)`, `CopyEnchantmentAsync(...)`, and `AfterCardEnchanted(...)`. Added stacked post-application hook surface `AfterSiblingAppliedStacked` plus `StackedAfterSiblingAppliedContext` / `StackedAfterSiblingAppliedHandler` for command-capable “card was just re-enchanted” reactions. Documented that `AfterCardEnchanted` fires only on the async enchant paths and that `AfterSiblingAppliedStacked` blocks on the sync path, plus a decision table for choosing between them.
- _2026-05-23_: **No public-API surface change**, but several robustness / tooling improvements ship in this build:
  - **Game v0.106.x compatibility**: Internal Harmony patches updated to track vanilla `Hook.*` signatures that gained an `IEnumerable<Creature> participants` / `IReadOnlyList<Creature> participants` parameter (`Hook.BeforeTurnEnd`, `Hook.AfterTurnEnd`, `Hook.BeforeSideTurnStart`, `Hook.AfterSideTurnStart`) and now take `ICombatState` at the public static `Hook` layer. Vanilla also dropped the `ValueProp` parameter from `EnchantmentModel.EnchantBlockAdditive` / `EnchantBlockMultiplicative`; the mod's block pipeline now matches the 1-arg signature. Authors who override these block methods on their own enchantment classes **must** adjust their signatures to match — see `MIGRATION_V3.md` for the migration shim.
  - **Defensive iteration**: Five additional `foreach` sites that iterate live mutable collections (`state.ExtraEnchantments`, `player.PlayerCombatState.AllCards`) and call into user-defined lifecycle handlers were hardened with `.ToList()` snapshots. This fixes a class of `InvalidOperationException: Collection was modified during enumeration` crashes when a handler calls `MultiEnchantmentApi.RemoveEnchantment` or `CardCmd.AddCardToHand` from inside `OnTurnStart` / `OnTurnEnd` / `OnCardChangedPiles` / `RecalculateValues` overrides. **Net effect for authors**: it is now safe to call mutating mod APIs from any lifecycle handler without queuing the call manually.
  - **Analyzer code-fix providers**: MEM007 (missing `[assembly: EnchantmentApiCompatibility]`) and MEM009 (wrong `[ModifyDynamicVar]` signature) now ship with IDE quick-fixes. See `docs/integration.md` § *Auto-fix support* for the diagnostic table.
  - **Nullable-reference cleanup**: 17 residual `CS8602` / `CS8604` warnings in the framework's own code eliminated. No behavior change; downstream mods that enable `<TreatWarningsAsErrors>` will not see new warnings introduced by the framework.
- _2026-05-22_: Added `MultiEnchantmentApi.Enchant(..., EnchantmentScope? scopeOverride)` and `SetScopeOverride` for predicate-free per-instance scope overrides. Added `ScopeRuntimeStateView.HasOverride` and persisted override metadata in scope state.
- _2026-05-22_: Added `MultiEnchantmentApi.NotifyPropsChanged`, `GetScopeState`, `IsActive`, `GetSiblings`. Added `IEnchantmentRegistration.Stack(StackDefinition)` overload, four `OnAnyCard*` broadcast hooks, two `OnSibling*` neighbor hooks. Added `EnchantmentDefinition<T>.OnAnyCard*` and `OnSibling*` virtuals. Added `ScopeRuntimeStateView`, `StackOverflowPolicy`, `StackDefinition.OnOverflow`. Added `RemovalReason.OverflowEvicted`. Added `EnchantmentStackSnapshot.ScopeStates` and `StateOf(EnchantmentModel)`.
- _2026-05-21_: Initial baseline. Added `StackDefinition.MaxInstances`. Removed throw from `ModifyDynamicVarAttribute(string)` constructor. Tightened error reporting in `AssemblyScanner` to include the offending assembly name.

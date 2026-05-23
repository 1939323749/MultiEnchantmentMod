# MultiEnchantmentMod Public API Baseline

This file is the human-maintained snapshot of the **`MultiEnchantmentMod.Api`** namespace as
shipped today. It exists so that any PR touching public surface area is reviewable as a diff
against this baseline. **If you change the signature of any public type or member listed here —
or add/remove one — update this file in the same PR.**

> Scope: only types under `MultiEnchantmentMod.Api*` namespaces. Public types in the legacy
> `MultiEnchantmentMod` namespace (`EnchantmentStackSnapshot`, `HookExecutionMode`,
> `EnchantmentStackBehavior`, `EnchantmentStatusAggregation`, `EnchantmentHookKind`,
> `EnchantmentStackDefinition`, `EnchantmentExecutionPolicy`, `EnchantmentStackSlice`,
> `MultiEnchantmentStackApi`) are part of the v2 contract but kept where they are for source
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
- `static bool SetScopeOverride(CardModel card, EnchantmentModel enchantment, EnchantmentScope? newScope)` — changes or clears the per-instance scope override on an attached enchantment; returns `false` when rejected or not attached.
- `static bool HasEnchantment<TEnchantment>(CardModel? card) where TEnchantment : EnchantmentModel`
- `static bool HasEnchantment(CardModel? card, Type enchantmentType)`
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
- `Stack(StackDefinition definition)` — sets the full record (including `MaxInstances` / `OnOverflow`). Default-implemented; pre-existing adapters fall back to the two-arg overload (cap / overflow silently dropped on those).
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
- `RemoveWhen(Func<CardModel, EnchantmentModel, bool> predicate, params ActivationTrigger[] checkOn)`
- `OnApplied(Action<CardModel, EnchantmentModel>)`
- `OnRemoved(Func<CardModel, EnchantmentModel, RemovalReason, bool>)` — return `false` to veto removal.
- `OnCombatStart`, `OnCombatEnd`, `OnTurnStart`, `OnTurnEnd` — `Action<CardModel, EnchantmentModel>` each.
- `TrackKeyword(CardKeyword keyword, Func<EnchantmentStackSnapshot, int> amountFn)`
- `FormatExtraText(PresentationTextFormatter formatter)`
- `VisualSlices(Func<EnchantmentStackSnapshot, IReadOnlyList<int>?> compute)`
- `ModifyDynamicVar(string varKey, Func<EnchantmentStackSnapshot, decimal, decimal> contribution)`
- `IDisposable Commit()`

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

### `sealed class ExecutionPolicyBuilder`
- `All(HookExecutionMode)`, `OnEnchant(HookExecutionMode)`, `OnPlay`, `AfterCardPlayed`, `AfterCardDrawn`, `AfterPlayerTurnStart`, `BeforePlayPhaseStart`, `BeforeFlush` — all chain.
- `EnchantmentExecutionPolicy Build()`

### `sealed record DamageReceivedContext(Creature Target, DamageResult Result, Creature? Dealer, CardModel? Source)`
Payload to `OnAfterDamageReceived`.

### `sealed record BlockGainContext(Creature Creature, decimal Amount, CardModel? Source)`
Payload to `OnBeforeBlockGained` / `OnBlockGained`.

### `sealed record DynamicVarContribution(string VarKey, Func<EnchantmentStackSnapshot, decimal, decimal> Contribution)`
Returned by the registry; authors build them implicitly via `IEnchantmentRegistration.ModifyDynamicVar` or the `[ModifyDynamicVar]` attribute.

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
  - `string Activation { get; init; }`
- `[AttributeUsage(Class)] sealed class EnchantmentDefinitionAttribute : Attribute` — marker.
- `[AttributeUsage(Class)] sealed class EnchantmentExecutionAttribute : Attribute`
  - `HookExecutionMode All { get; init; }` plus one per hook kind.
- `[AttributeUsage(Method, AllowMultiple=true)] sealed class EnchantmentKeywordAttribute : Attribute`
  - `CardKeyword Keyword`, `KeywordEvalMode Mode`, `int Constant`.
- `[AttributeUsage(Class)] sealed class EnchantmentPresentationAttribute : Attribute`
  - `bool HasExtraText { get; init; }`, `bool HasVisualSliceOverride { get; init; }`.
- `[AttributeUsage(Method, AllowMultiple=true)] sealed class ModifyDynamicVarAttribute : Attribute`
  - `string VarKey` — ctor now non-throwing; empty-string keys are caught at scan time (logged + skipped) and by analyzer rule **MEM009** at compile time.
- `[AttributeUsage(Assembly)] sealed class EnchantmentApiCompatibilityAttribute : Attribute`
  - `int MinVersion`, `int MaxVersion`.

---

## Non-public, intentionally

Everything under `MultiEnchantmentMod.Api.Internal` is `internal`. Adapter providers
(`AdapterDefinitionProvider<T>`, `AdapterLifecycleProvider<T>`, etc.), registry types
(`EnchantmentEntry`, `EnchantmentRegistry`, `EnchantmentRegistration<T>`), scanners
(`AssemblyScanner`, `ModifyDynamicVarScanner`), runtime helpers (`SafeInvoker`,
`LegacyEnumMappings`) are **not** part of the public contract and may change between releases
without notice. Do not reference them by reflection.

---

## Change log

- _2026-05-23_: **No public-API surface change**, but several robustness / tooling improvements ship in this build:
  - **Game v0.106.0 compatibility**: Internal Harmony patches updated to track three vanilla `Hook.*` signatures that gained an `IEnumerable<Creature> participants` / `IReadOnlyList<Creature> participants` parameter (`Hook.AfterTurnEnd`, `Hook.BeforeSideTurnStart`, `Hook.AfterSideTurnStart`). Vanilla also dropped the `ValueProp` parameter from `EnchantmentModel.EnchantBlockAdditive` / `EnchantBlockMultiplicative`; the mod's block pipeline now matches the 1-arg signature. Authors who override these block methods on their own enchantment classes **must** adjust their signatures to match — see `MIGRATION_V3.md` for the migration shim.
  - **Defensive iteration**: Five additional `foreach` sites that iterate live mutable collections (`state.ExtraEnchantments`, `player.PlayerCombatState.AllCards`) and call into user-defined lifecycle handlers were hardened with `.ToList()` snapshots. This fixes a class of `InvalidOperationException: Collection was modified during enumeration` crashes when a handler calls `MultiEnchantmentApi.RemoveEnchantment` or `CardCmd.AddCardToHand` from inside `OnTurnStart` / `OnTurnEnd` / `OnCardChangedPiles` / `RecalculateValues` overrides. **Net effect for authors**: it is now safe to call mutating mod APIs from any lifecycle handler without queuing the call manually.
  - **Analyzer code-fix providers**: MEM007 (missing `[assembly: EnchantmentApiCompatibility]`) and MEM009 (wrong `[ModifyDynamicVar]` signature) now ship with IDE quick-fixes. See `docs/integration.md` § *Auto-fix support* for the diagnostic table.
  - **Nullable-reference cleanup**: 17 residual `CS8602` / `CS8604` warnings in the framework's own code eliminated. No behavior change; downstream mods that enable `<TreatWarningsAsErrors>` will not see new warnings introduced by the framework.
- _2026-05-22_: Added `MultiEnchantmentApi.Enchant(..., EnchantmentScope? scopeOverride)` and `SetScopeOverride` for predicate-free per-instance scope overrides. Added `ScopeRuntimeStateView.HasOverride` and persisted override metadata in scope state.
- _2026-05-22_: Added `MultiEnchantmentApi.NotifyPropsChanged`, `GetScopeState`, `IsActive`, `GetSiblings`. Added `IEnchantmentRegistration.Stack(StackDefinition)` overload, four `OnAnyCard*` broadcast hooks, two `OnSibling*` neighbor hooks. Added `EnchantmentDefinition<T>.OnAnyCard*` and `OnSibling*` virtuals. Added `ScopeRuntimeStateView`, `StackOverflowPolicy`, `StackDefinition.OnOverflow`. Added `RemovalReason.OverflowEvicted`. Added `EnchantmentStackSnapshot.ScopeStates` and `StateOf(EnchantmentModel)`.
- _2026-05-21_: Initial baseline. Added `StackDefinition.MaxInstances`. Removed throw from `ModifyDynamicVarAttribute(string)` constructor. Tightened error reporting in `AssemblyScanner` to include the offending assembly name.

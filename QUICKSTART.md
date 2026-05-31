# MultiEnchantmentMod Quickstart

A 5-minute path from "I want my Slay the Spire 2 mod to add a new enchantment" to "the
enchantment shows up on a card and stacks correctly."

This is the smallest path. For the full reference, see [docs/v2-api-wiki.md](docs/v2-api-wiki.md)
and [docs/v2-lifecycle-wiki.md](docs/v2-lifecycle-wiki.md).

---

## Prerequisites

1. A Slay the Spire 2 mod project (the standard `Godot.NET.Sdk` template).
2. MultiEnchantmentMod installed in the game's `mods/` folder. Your mod will reference its
   `.dll` at build time and depend on it at load time — see
   [docs/integration.md](docs/integration.md) for the recommended csproj pattern.

## 5 steps

### 1. Reference the framework

Add the manifest dependency in your mod's `.json`:

```json
{
  "id": "MyEnchantments",
  "name": "My Enchantments",
  "version": "1.0.0",
  "dependencies": [
    "MultiEnchantmentMod"
  ]
}
```

In your `.csproj`, reference `MultiEnchantmentMod.dll` — see
[docs/integration.md](docs/integration.md) for the recommended pattern. Tag the assembly:

```csharp
// AssemblyInfo.cs (or any .cs file)
using MultiEnchantmentMod.Api;
[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]
```

### 2. Declare the enchantment

```csharp
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MyMod;

[Enchantment(Stack = StackBehavior.MergeAmount,
             Status = StatusAggregation.SharedAcrossStack)]
public sealed class GlacialBlessing : EnchantmentModel
{
    public override bool ShowAmount => true;

    public override void RecalculateValues()
    {
        // Each merge adds 3 block.
        DynamicVars.Block.BaseValue = Amount * 3;
    }
}
```

That's it. The `[Enchantment]` attribute IS the registration. **No `Register<T>` call needed**
for the simple case.

### 3. Hook the scanner in your `[ModInitializer]`

```csharp
[ModInitializer(nameof(Initialize))]
public partial class MyMod : Node
{
    public static void Initialize()
    {
        // Fail-fast if the user installed an older MultiEnchantmentMod.
        if (!MultiEnchantmentApi.RequireApiVersion(MultiEnchantmentApiVersion.Current))
        {
            return;
        }

        MultiEnchantmentApi.ScanCallingAssembly();
    }
}
```

**The single most common failure mode**: forgetting `ScanCallingAssembly()`. Without it, your
`[Enchantment]`-tagged classes default to `DisallowDuplicate` + no v2 lifecycle. If you see your
enchantment behave like a vanilla single-instance enchantment, this is the first thing to check.

### 4. Add localization (optional but expected)

In `MyMod/localization/eng/enchantments.json`:

```json
{
  "GLACIAL_BLESSING.title": "Glacial Blessing",
  "GLACIAL_BLESSING.description": "Gain $Block at the end of your turn.",
  "GLACIAL_BLESSING.extraCardText": "Stacks."
}
```

The vanilla loc system reads this automatically — no API call needed. Key convention is
`<ENCHANTMENT_ID>.<field>` matching your `ModelId`.

### 5. Build + install + look at the log

```bash
dotnet publish MyMod.csproj -c Release
```

Launch the game and search `godot.log` for one of these markers:

- `[StackApi] Scanned <N>` — the assembly scanner ran. If `<N>` is 0, your `[Enchantment]`
  attribute didn't match anything; check the namespace and that `EnchantmentModel` is the base.
- `[StackApi] Failed to instantiate <Type> (assembly=<Asm>): …` — your enchantment class has
  a problem (missing parameterless ctor, exception during static init). The log includes the
  offending assembly so you can pin it on the right mod.
- `[MultiEnchantment] <Type> (assembly=<Asm>) threw in <Hook>: …` — your enchantment registered
  but one of its lifecycle callbacks throws at runtime. `SafeInvoker` catches and skips so
  other enchantments keep working.

---

## Next steps

- **Per-stack effect**: override `EnchantmentDefinition<T>.OnMergedDelta` (Tier B). See
  [Samples/04_CustomMergeDelta.cs](MultiEnchantmentMod.Samples/Samples/04_CustomMergeDelta.cs).
- **Per-turn lifetime / removal**: use the `Scope` attribute property
  (`Scope = ScopeKind.UntilTurnEnds`, `LingerTurns = 2`, `MaxActivations = 3`).
  See [Samples/09_UntilTurnEndsScope.cs](MultiEnchantmentMod.Samples/Samples/09_UntilTurnEndsScope.cs).
- **Dynamic var math**: `[ModifyDynamicVar("damage")]` on a method with signature
  `decimal Method(EnchantmentStackSnapshot, decimal)`. See
  [Samples/14_DynamicVarComposition.cs](MultiEnchantmentMod.Samples/Samples/14_DynamicVarComposition.cs).
- **Runtime / conditional registration**: build everything via the fluent
  `MultiEnchantmentApi.Register<T>().Stack(...).OnApplied(...).Commit()` chain (Tier C). See
  [Samples/07_DynamicRuntimeRegistration.cs](MultiEnchantmentMod.Samples/Samples/07_DynamicRuntimeRegistration.cs).
- **Hook execution policy**: if your enchantment overrides old-style `EnchantmentModel` hooks
  like `OnPlay`, configure `.Execution(p => ...)` or `[EnchantmentExecution]` when stack count
  should not equal call count. See
  [docs/v2-api-wiki.md](docs/v2-api-wiki.md#hook-执行策略).
- **Broadcast card events** (any card played / drawn / exhausted / discarded): override
  `OnAnyCardPlayed` etc., or use the fluent equivalent. Opt-in only. See
  [Samples/19_OnAnyCardPlayedBroadcast.cs](MultiEnchantmentMod.Samples/Samples/19_OnAnyCardPlayedBroadcast.cs).
- **Same-card neighbor combos**: `OnSiblingApplied` / `OnSiblingRemoved` fire when other
  enchantments land on / leave the same card. See
  [Samples/20_SiblingAwareCombo.cs](MultiEnchantmentMod.Samples/Samples/20_SiblingAwareCombo.cs).
- **Dim the badge when inactive**: use `.WhenActiveStatus(...)` or override
  `EnchantmentDefinition<T>.ShouldBeActive(...)`. Use plain `.WhenActive(...)` when you only
  need gameplay gating without changing the visual status.
- **Show scope state in tooltips**: snapshots now expose `ScopeStates`; pull
  `ActivationCount` / `TurnsRemaining` from the view in `FormatExtraText`. See
  [Samples/21_ScopeStateInPresentation.cs](MultiEnchantmentMod.Samples/Samples/21_ScopeStateInPresentation.cs).
- **Understand visual slices**: `VisualSlices` controls how stack badges are displayed; it is
  not a second damage/amount system. See the snapshot section in
  [docs/v2-api-wiki.md](docs/v2-api-wiki.md#snapshot-只读-api).
- **Refresh derived UI state after a non-application callback**: call
  `MultiEnchantmentApi.NotifyPropsChanged(self)`. See
  [Samples/22_PropsChangeRefresh.cs](MultiEnchantmentMod.Samples/Samples/22_PropsChangeRefresh.cs).
- **FIFO/LIFO instance cap**: `Stack(new StackDefinition(...) { MaxInstances = 5, OnOverflow = StackOverflowPolicy.ReplaceOldest })`.
  See [Samples/23_StackOverflowReplace.cs](MultiEnchantmentMod.Samples/Samples/23_StackOverflowReplace.cs).
- **Per-application scope override**: `MultiEnchantmentApi.Enchant(card, enchantment, 1, EnchantmentScope.UntilCombatEnds)`
  or later `SetScopeOverride(card, enchantment, EnchantmentScope.UntilTurnEnds)`. See
  [Samples/24_PerInstanceScope.cs](MultiEnchantmentMod.Samples/Samples/24_PerInstanceScope.cs).

## Common gotchas

| Symptom | Likely cause |
|---|---|
| Enchantment registers but stacks like DisallowDuplicate | Forgot `ScanCallingAssembly()` — fall back to default StackDefinition |
| Enchantment never appears in-game | `EnchantmentModel` not registered with vanilla `ModelDb` — that's a separate step from MultiEnchantmentMod |
| `MEM004` / `MEM009` / `MEM011` analyzer errors | See [docs/v2-api-wiki.md](docs/v2-api-wiki.md) §Analyzer Rules; each ID has a fix recipe |
| `[ModifyDynamicVar]` silently does nothing | Wrong method signature; MEM009 catches at compile time |
| `MergeAmount` `OnPlay` effect is much too large | Your `OnPlay` probably reads `Amount` and also runs `MergedTotal` times; set `OnPlay = HookExecutionMode.PerLiveInstance` |
| Inactive enchantment still looks visually active | `.WhenActive(...)` gates gameplay only; use `.WhenActiveStatus(...)` / `ShouldBeActive(...)` to sync `Disabled` status |
| `MaxActivations` counter resets on save/load | Fixed in current version; see release notes if running an older build |
| Cap on duplicates needed | Set `StackDefinition.MaxInstances` via fluent registration — only applies to `DuplicateInstance`/`ExistenceStack` |

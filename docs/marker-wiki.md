# Marker Manual

Use this page when you want to add a small card badge for a downstream mod.

An marker is for UI information. It should not change damage, block, card text, cost, hooks, or
other gameplay behavior. If the card should behave differently, use a normal `EnchantmentModel`
instead.

For the full API list, see [`public-api.md`](public-api.md). For project setup, see
[`integration.md`](integration.md).

## 1. Pick the right path

Use this table first.

| Need | Use |
|---|---|
| One simple badge decided by a card predicate | `RegisterMarker<TMarker>` (legacy name `RegisterMarker`) |
| Badge amount/icon/style changes per card | `RegisterMarkerDisplayProvider` (legacy name `RegisterMarkerDisplayProvider`) |
| One provider returns several badges | `RegisterMarkerDisplayProvider` |
| Marker has saved per-card data | Stored marker (`MarkerEnchantmentModel`) |
| Card behavior changes | Normal `EnchantmentModel` |
| Card behavior changes but **no badge wanted** (text only) | Invisible enchantment: `[Enchantment(Invisible = true)]` |

> Terminology: since v2.4.1 "extra icon" is unified as **marker**. The old `*ExtraIcon*` names
> (`RegisterExtraIcon`, `RefreshExtraIcons`, `IsExtraIconShown`, `ExtraIconEnchantmentModel`, ...)
> were renamed in place — see `MIGRATION_V3.md` for the full rename table. Code referencing the
> old names just needs the rename + a recompile; save files are unaffected.
>
> Invisible enchantments are NOT markers — they are FULL gameplay enchantments (hooks, counting,
> save/load, multiplayer all normal) that render no badge, never occupy the vanilla primary slot,
> and skip the enchant shimmer. They cannot hide: changed numbers still render in the modified
> color, and hover tips still list them (the card text is public anyway).

Recommendation: start with display-only registration. It is cheaper to reason about, and it does not
make gameplay checks like `HasAnyEnchantment(card)` return true by accident.

### How the three differ (normal enchantment / stored marker / display-only marker)

All three can "show something" on a card, but they are **fundamentally different**. The root of the
difference is one line: internally the mod treats every `MarkerEnchantmentModel` as non-gameplay
via `IsGameplayEnchantment(e) = e is not MarkerEnchantmentModel`.

| Aspect | Normal enchantment `EnchantmentModel` | Stored marker (`MarkerEnchantmentModel` instance) | Display-only marker (provider / `RegisterMarker`) |
|---|---|---|---|
| A real enchantment instance | ✅ Yes | ✅ Yes | ❌ No — recomputed from a predicate at render time |
| How it is created | `Enchant(card, model)` | `Enchant(card, an MarkerEnchantmentModel)` | `RegisterMarker` / `RegisterMarkerDisplayProvider` / `IconState` |
| Saved + carried on card clone | ✅ | ✅ | ❌ |
| Found by `GetEnchantment<T>`/`HasEnchantment<T>` | ✅ | ✅ | ❌ |
| Found by `GetMarkers`/`GetMarker<T>` | ❌ (it is not a marker) | ✅ | ❌ |
| Runs combat hooks / damage / block / DynamicVar / energy pipelines | ✅ | ❌ | ❌ |
| Fires `OnApplied`/`OnPlay`/`AfterCardEnchanted` lifecycle | ✅ | ❌ | ❌ (no instance at all) |
| Counted by `HasAnyEnchantment`/`GetEnchantmentCount` | ✅ by default | ❌ not by default (need `includeMarkers: true`) | ❌ |
| Enters application order / battle history | ✅ | ❌ (history defaults to `Hidden`) | ❌ |
| Can read/write `Amount`/`Props` as data | ✅ | ✅ | ❌ (no instance; change the state the predicate reads) |
| Default visuals | Badge backing, can show amount + extra card text | No backing, no amount/extra text, hidden when disabled, `DisplayPriority=1000` | Same stored-marker defaults (`MarkerPresentation.Default`) |
| Typical use | Actually change card behavior | A persistent, save-backed "flag/counter" you can query by type, with no effect of its own | A purely decorative badge shown by condition |

Quick decision:

- Changes **card behavior** → normal `EnchantmentModel`.
- No behavior change, but you need "a real marker on the card that is saved, can be retrieved with
  `GetMarker`, and can hold `Amount`/`Props`" → stored `MarkerEnchantmentModel`.
- No behavior change and **no need to persist**, just "show an icon when a condition holds" →
  display-only marker (lightest; prefer this).

Key point: a stored marker and a normal enchantment **are both real instances, both saved, both
queryable by type**; the only difference is the marker is flagged non-gameplay, so it **runs no
combat logic/hooks, is not counted as an enchantment, never enters history, and by default draws
just a small backing-less icon**.

## 2. Add the API guard

In your mod assembly, declare compatibility once:

```csharp
using MultiEnchantmentMod.Api;

[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]
```

During initialization, check the version before registering icons:

```csharp
public static void Initialize()
{
    if (!MultiEnchantmentApi.RequireApiVersion(MultiEnchantmentApiVersion.Current))
    {
        return;
    }

    BloodMarkedIcons.Install();
}
```

Note: reference `MultiEnchantmentMod.dll` as a prerequisite. Do not copy it into your own output
folder.

## 3. Create a simple static marker

Create an empty marker type:

```csharp
using MultiEnchantmentMod.Api;

public sealed class BloodMarkedIcon : MarkerEnchantmentModel
{
}
```

Then register it:

```csharp
using System;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

public static class BloodMarkedIcons
{
    private static Texture2D? _icon;
    private static IDisposable? _registration;

    public static void Install()
    {
        _icon ??= GD.Load<Texture2D>("res://images/markers/blood_marked.png");

        _registration ??= MultiEnchantmentApi.RegisterMarker<BloodMarkedIcon>(
            appliesTo: card => card.Id.Entry.StartsWith("Vampire", StringComparison.Ordinal),
            options: new MarkerRegistrationOptions
            {
                Icon = _icon,
                PresentationStyle = MarkerPresentation.Default with
                {
                    IconScale = 1.2f,
                },
            });
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}
```

Replace these parts:

- `BloodMarkedIcon`: your marker type.
- `res://images/markers/blood_marked.png`: your imported texture path.
- `appliesTo`: the condition for showing the badge.

Note: `BloodMarkedIcon` can be empty. The type is the marker key. The UI uses it for sorting,
deduplication, same-type suppression, and query APIs.

Validate:

- Build your mod.
- Open a card that matches `appliesTo`.
- The badge should appear in the card icon row.
- If it does not appear, check the predicate and the icon path first.

## 4. Set fixed options

Use `MarkerRegistrationOptions` when the static marker needs common settings.

```csharp
options: new MarkerRegistrationOptions
{
    Icon = _icon,
    PresentationStyle = MarkerPresentation.Default with
    {
        DisplayPriority = MarkerPresentation.DefaultDisplayPriority + 20,
    },
    ShowAmount = true,
    Amount = 3,
    ShowWithLiveEnchantment = false,
}
```

Field notes:

- `Icon`: explicit texture. This is the safest way to provide art.
- `PresentationStyle`: icon size, tint, badge backing, disabled behavior, and priority.
- `ShowAmount`: set `true` before `Amount` is drawn.
- `Amount`: fixed number for every matching card.
- `ShowWithLiveEnchantment`: set `true` only when this marker should coexist with a live
  enchantment of the same exact type.

If `Amount` must depend on the card, do not use this static path. Use a provider.

## 5. Create a dynamic provider

Use a provider when the output depends on card state.

```csharp
using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

public sealed class BloodChargeIcon : MarkerEnchantmentModel
{
}

public static class BloodChargeIcons
{
    private static readonly Texture2D ChargeIcon =
        GD.Load<Texture2D>("res://images/markers/blood_charge.png");

    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.RegisterMarkerDisplayProvider(GetIcons);
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }

    private static IEnumerable<MarkerDisplay> GetIcons(CardModel card)
    {
        int charges = BloodState.GetCharges(card);
        if (charges <= 0)
        {
            yield break;
        }

        yield return new MarkerDisplay
        {
            EnchantmentType = typeof(BloodChargeIcon),
            Icon = ChargeIcon,
            ShowAmount = true,
            Amount = charges,
            PresentationStyle = MarkerPresentation.Default,
            ShouldDisplay = context => context.IsCombatCard || context.IsPreviewCard,
        };
    }
}
```

Replace `BloodState.GetCharges(card)` with your own state lookup.

`MarkerDisplay` fields:

- `EnchantmentType`: required marker key. Prefer an `MarkerEnchantmentModel` subclass.
- `Icon`: texture to draw.
- `Enchantment`: optional model source for icon/status/hover tips.
- `PresentationStyle`: per-display style.
- `ShouldDisplay`: final visibility predicate.
- `ShowAmount` / `Amount`: number on the badge.
- `ShowWithLiveEnchantment`: allow same-type coexistence.

`MarkerDisplayContext` gives:

- `Card`: the `CardModel` being refreshed.
- `HasLiveEnchantment`: whether the exact marker type already exists on this card.
- `IsCombatCard`: true for combat cards.
- `IsPreviewCard`: true for enchantment preview cards.

Validate:

- Change the state read by `GetIcons`.
- Call `MultiEnchantmentApi.RefreshMarkers(card)`.
- The icon should appear, disappear, or update its number.

## 6. Refresh after state changes

Display-only icons are recomputed from provider state. There is no "edit this registration" call.

Use this after changing one card:

```csharp
MultiEnchantmentApi.RefreshMarkers(card);
```

Use this after changing global state:

```csharp
MultiEnchantmentApi.RefreshMarkers();
```

Note: stored marker instances already refresh when you remove them or call `NotifyPropsChanged`.
Provider markers need a refresh when the UI should update immediately.

## 7. Mirror model state as an icon

Common case: a real card, ability, relic, or normal enchantment owns the gameplay state. You only
want an marker to show that state on the card.

Treat `IconState<TMarker>` as a UI projection. It is not the gameplay owner.

Recommended pattern:

1. Create one `IconState<TMarker>` (one instance per marker type).
2. Store the real state on your real model.
3. After that model changes, sync the display value with `Set`/`Show` or `Remove`.

`IconState<TMarker>` is a small wrapper around the provider pattern. It stores temporary per-card
UI state, draws the marker, and refreshes the card after each mutation. Calling `Register()` is
optional — the first mutation auto-registers the provider.

It offers two ways to project a marker:

- **Amount-gated** (`Set`, `Add`): the marker shows only while the amount is positive; an amount of
  zero or less removes it. Best when the icon mirrors a count that disappears at zero.
- **Explicit presence** (`Show`): the marker stays shown until `Remove`/`Clear`, the amount is just a
  label that may be `0`, and an `IconStateOverride` can vary that card's icon / hover tip /
  presentation / amount label. Best for per-card art or a counter that should display `0`.

`Has(card)` is a presence check (true even for a `Show`n marker at amount 0); `Get(card)` returns the
numeric amount.

Create the marker and icon projection:

```csharp
using System;
using Godot;
using MultiEnchantmentMod.Api;

public sealed class BloodChargeIcon : MarkerEnchantmentModel
{
}

public static class BloodChargeIcons
{
    public static readonly IconState<BloodChargeIcon> State = new(
        icon: GD.Load<Texture2D>("res://images/markers/blood_charge.png"),
        presentationStyle: MarkerPresentation.Default,
        showAmount: true);

    public static void Install()
    {
        // Optional: the first Set/Add/Show would auto-register anyway.
        State.Register();
    }

    public static void Uninstall()
    {
        // Dispose is terminal: it unregisters AND clears all projections.
        State.Dispose();
    }
}
```

Then sync from the real model.

This example uses a normal enchantment as the gameplay owner. The enchantment is attached to the
card; the icon only mirrors `enchantment.Amount`.

Note: this uses the mod lifecycle wrapper, not a vanilla game override. The wrapper signature is
`OnCardDrawn(CardModel card, TEnchantment enchantment)`.

```csharp
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

public sealed class BloodCharge : EnchantmentModel
{
}

public sealed class BloodChargeDefinition : EnchantmentDefinition<BloodCharge>
{
    protected override void OnApplied(CardModel card, BloodCharge enchantment)
    {
        BloodChargeIcons.State.Set(card, (int)enchantment.Amount);
    }

    protected override void OnCardDrawn(CardModel card, BloodCharge enchantment)
    {
        enchantment.Amount += 1;

        // BloodCharge owns the state. BloodChargeIcon only shows it.
        BloodChargeIcons.State.Set(card, (int)enchantment.Amount);
        MultiEnchantmentApi.NotifyPropsChanged(enchantment);
    }

    protected override bool OnRemoved(
        CardModel card,
        BloodCharge enchantment,
        RemovalReason reason)
    {
        _ = enchantment;
        _ = reason;
        BloodChargeIcons.State.Remove(card);
        return true;
    }
}
```

If you are overriding the game's vanilla hook directly, keep the game signature:

```csharp
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public override Task AfterCardDrawn(
    PlayerChoiceContext choiceContext,
    CardModel card,
    bool fromHandDraw)
{
    if (!ReferenceEquals(card, this))
    {
        return Task.CompletedTask;
    }

    int displayAmount = GetDisplayAmountForThisCard();
    BloodChargeIcons.State.Set(card, displayAmount);
    return Task.CompletedTask;
}
```

### Per-card icon / tooltip via `Show`

To vary art or hover text per card while sharing one `IconState`, project with `Show` and an
`IconStateOverride`. Fields left `null` fall back to the constructor values.

```csharp
// Same marker type, different icon + tooltip per card, and a counter that may read "0".
BloodChargeIcons.State.Show(cardA, amount: 0, overrides: new IconStateOverride
{
    Icon = GD.Load<Texture2D>("res://images/markers/blood_charge_empty.png"),
    ShowAmount = true,
});

BloodChargeIcons.State.Show(cardB, amount: 3, overrides: new IconStateOverride
{
    Icon = GD.Load<Texture2D>("res://images/markers/blood_charge_full.png"),
    Enchantment = someEnchantmentForThisCardsHoverTip, // drives this card's hover tip
});
```

CRUD mapping:

| Operation | Call |
|---|---|
| Register provider (optional; first mutation auto-registers) | `BloodChargeIcons.State.Register()` |
| Show current model state (amount-gated) | `BloodChargeIcons.State.Set(card, displayAmount)` |
| Show with per-card art / allow "0" | `BloodChargeIcons.State.Show(card, amount, overrides)` |
| Increase UI-only projection | `BloodChargeIcons.State.Add(card, amount)` |
| Hide projection | `BloodChargeIcons.State.Remove(card)` |
| Clear all cards (refreshes only tracked cards) | `BloodChargeIcons.State.Clear()` |
| Refresh only tracked cards | `BloodChargeIcons.State.RefreshTracked()` |
| List tracked cards | `BloodChargeIcons.State.GetTrackedCards()` |
| Unregister + clear (terminal) | `BloodChargeIcons.State.Dispose()` |
| Check amount / presence | `BloodChargeIcons.State.Get(card)` / `Has(card)` |
| Check final UI row | `MultiEnchantmentApi.GetShownMarkerDetails(card)` |

Use `IconState<TMarker>` for temporary UI projection. If the marker itself must be saved card state,
use a stored `MarkerEnchantmentModel` instead. If the marker changes gameplay, use a normal
`EnchantmentModel`.

Validate:

- Draw the card.
- `BloodCharge.Amount` changes.
- `BloodChargeIcons.State.Set(card, amount)` runs.
- The badge mirrors the enchantment amount.
- `GetShownMarkerDetails(card)` includes `BloodChargeIcon`.

## 8. Style the badge

Start from `MarkerPresentation.Default`.

```csharp
PresentationStyle = MarkerPresentation.Default with
{
    ShowBadgeBacking = false,
    IconScale = 1.25f,
    IconOffset = new Vector2(0, -3),
    IconTint = Colors.White,
    DisabledIconTint = new Color(0.5f, 0.5f, 0.5f, 0.75f),
    HideWhenDisabled = true,
    DisplayPriority = MarkerPresentation.DefaultDisplayPriority + 10,
}
```

Default marker style:

- `ShowBadgeBacking = false`
- `HideWhenDisabled = true`
- `DisplayPriority = 1000`

Ordering rule:

- Higher `DisplayPriority` renders earlier.
- Disabled markers are hidden when `HideWhenDisabled = true`.
- Two displays with the same exact `EnchantmentType` collapse unless the later one sets
  `ShowWithLiveEnchantment = true`.

## 9. Provide an icon correctly

Do not override `EnchantmentModel.Icon`. It is non-virtual.

Use one of these methods:

| Method | When to use |
|---|---|
| `Icon = GD.Load<Texture2D>("res://...png")` | Recommended for custom art |
| `icon: ModelDb.Enchantment<Sharp>().Icon` | Borrow an existing icon |
| `MarkerDisplay.Enchantment = someModel` | Need icon/status/hover tips from a model |
| Convention icon path | Your marker is registered as a canonical model |

Note: if no texture resolves, the marker is skipped and logged once. There is no missing-icon
placeholder.

## 10. Add hover tips

Card hover tips come from `Enchantment.HoverTips`. Provider markers can contribute hover tips only
when they have a model source.

Use one of these:

- Pass `MarkerDisplay.Enchantment`.
- Make `EnchantmentType` resolve to a canonical model that defines `ExtraHoverTips`.

Do not expect a raw `Icon` texture to provide hover text. It can draw the badge, but it has no text
source.

## 11. Use stored markers only when needed

Use a stored `MarkerEnchantmentModel` only when the marker needs real per-card state.

It is still not gameplay:

- no `ModifyCard`
- no lifecycle hooks such as `OnApplied`
- no `AfterCardEnchanted`
- no damage/block/dynamic-var contribution
- no gameplay application order

Stored markers:

- are returned by `GetMarkers(card)`;
- can save/load with the same card object;
- can be carried by ordinary card clone;
- do not transfer through compatible transform copy;
- do not become permanent deck enchantments just because they were created in combat.

If the marker should affect gameplay, make it a normal `EnchantmentModel`.

## 12. Query stored state

Use these when you want card-owned enchantment instances.

```csharp
bool hasGameplay = MultiEnchantmentApi.HasAnyEnchantment(card);

bool hasGameplayOrStoredMarkers =
    MultiEnchantmentApi.HasAnyEnchantment(card, includeMarkers: true);

IReadOnlyList<EnchantmentModel> gameplay =
    MultiEnchantmentApi.GetEnchantments(card);

IReadOnlyList<EnchantmentModel> gameplayAndStoredMarkers =
    MultiEnchantmentApi.GetEnchantments(card, includeMarkers: true);

IReadOnlyList<MarkerEnchantmentModel> storedMarkers =
    MultiEnchantmentApi.GetMarkers(card);
```

Note: `includeMarkers: true` includes stored `MarkerEnchantmentModel` instances. It does not
include display-only provider markers.

Typed marker lookup:

```csharp
BloodStoredMarker? marker = MultiEnchantmentApi.GetMarker<BloodStoredMarker>(card);
bool hasMarker = MultiEnchantmentApi.HasEnchantment<BloodStoredMarker>(card);
```

### Friendly stored-marker CRUD

No manual instance creation, no manual `NotifyPropsChanged` — these helpers resolve a mutable
instance from `ModelDb`, notify changes, and refresh the icon row automatically:

```csharp
var m   = MultiEnchantmentApi.GetOrAddMarker<SampleChargeCounter>(card);      // read-or-create
MultiEnchantmentApi.SetMarker<SampleChargeCounter>(card, amount: 3);          // create-or-set
int now = MultiEnchantmentApi.AddMarkerAmount<SampleChargeCounter>(card, +1); // counter
MultiEnchantmentApi.ModifyMarker<SampleChargeCounter>(card, x => x.Amount *= 2); // mutate existing
MultiEnchantmentApi.RemoveMarker<SampleChargeCounter>(card);                  // remove
```

A marker can also modify itself when other code holds the instance:

```csharp
marker.AddAmount(1);     // Amount += 1, auto refresh
marker.SetAmount(0);     // exact value, auto refresh
marker.NotifyChanged();  // after mutating Props directly
```

Note: the marker type must be `ModelDb`-registered (already required for save/load), and the
target card must be mutable.

## 13. Query visible icons

Use these when you want the final icon row.

```csharp
bool visible = MultiEnchantmentApi.IsMarkerShown<BloodMarkedIcon>(card);

IReadOnlyList<Type> visibleTypes =
    MultiEnchantmentApi.GetShownMarkers(card);

IReadOnlyList<ShownMarker> visibleIcons =
    MultiEnchantmentApi.GetShownMarkerDetails(card);

foreach (ShownMarker icon in visibleIcons)
{
    if (icon.ShowAmount)
    {
        GD.Print($"{icon.EnchantmentType.Name}: {icon.DisplayAmount}");
    }

    if (icon.IsStoredMarker)
    {
        MarkerEnchantmentModel stored = icon.StoredMarker!;
    }
}
```

These queries run after:

- provider evaluation;
- `ShouldDisplay`;
- same-type suppression;
- disabled-marker hiding;
- icon resolution;
- `DisplayPriority` ordering.

## 14. Keep providers cheap

Provider code runs often. Keep it small.

Do:

- cache textures;
- read card/local state;
- use one provider that yields several icons;
- dispose registrations on unload.

Avoid:

- `GD.Load` inside the provider loop;
- scanning the full deck every refresh;
- allocating large temporary collections;
- relying on the provider exception circuit breaker.

## 15. Troubleshoot

| Symptom | Check |
|---|---|
| Icon never appears | `appliesTo`, `ShouldDisplay`, icon path, same-type suppression |
| Icon appears only after reopening UI | call `RefreshMarkers(card)` |
| Amount is missing | set `ShowAmount = true` |
| Hover tip is missing | provide `MarkerDisplay.Enchantment` or canonical model hover tips |
| `HasAnyEnchantment(card)` is false | expected for display-only markers; use visible-icon queries |
| Two markers collapsed | use different marker types or `ShowWithLiveEnchantment = true` |
| Provider stops running | check logs for repeated exceptions |

Success check:

- Matching cards show the badge.
- Non-matching cards do not show it.
- `GetShownMarkerDetails(card)` reports the same icons you see in the UI.
- Gameplay checks still ignore display-only markers.

# Marker 实操手册

用这篇文档给下游 mod 添加卡牌小徽章。

Marker 只表示 UI 信息。它不应该改伤害、格挡、卡牌文本、费用、hook 或其他 gameplay 行为。如果你的效果会改变卡牌行为，请用普通 `EnchantmentModel`。

完整 API 看 [`public-api.md`](public-api.md)。项目引用方式看 [`integration.md`](integration.md)。

## 1. 先选方案

先看这张表。

| 需求 | 推荐用法 |
|---|---|
| 一个简单徽章，由卡牌条件决定 | `RegisterMarker<TMarker>`（旧名 `RegisterMarker`） |
| 图标、数量、样式要按牌变化 | `RegisterMarkerDisplayProvider`（旧名 `RegisterMarkerDisplayProvider`） |
| 一个 provider 返回多个徽章 | `RegisterMarkerDisplayProvider` |
| marker 需要保存每张牌自己的数据 | 存储型 marker（`MarkerEnchantmentModel`） |
| 卡牌行为会改变 | 普通 `EnchantmentModel` |
| 卡牌行为会改变，但**不想显示徽章**（只留卡面文字） | 隐形附魔：`[Enchantment(Invisible = true)]` 的普通附魔 |

> 术语说明：自 v2.4.1 起 "extra icon" 统一为 **marker**。旧 `*ExtraIcon*` 名称（`RegisterExtraIcon`、`RefreshExtraIcons`、`IsExtraIconShown`、`ExtraIconEnchantmentModel` 等）已原地改名——完整对照见 `MIGRATION_V3.md`。引用旧名的代码改名重编译即可；存档不受影响。
>
> 隐形附魔不属于 marker 体系——它是**完整玩法附魔**（钩子、计数、存档、联机全部正常），只是不画徽章、不占原版主槽、应用时无闪光动画。它无法隐藏的：数值变化仍以修改色显示、hover 提示仍会列出它（卡面文字本来就是公开的）。

建议：能用显示层 marker 就先用显示层 marker。它不会让 `HasAnyEnchantment(card)` 这种 gameplay 判断被装饰徽章干扰。

### 三者本质区别（正常附魔 / 存储型 marker / 显示层 marker）

三种东西在卡上都能“显示点什么”，但**本质完全不同**。区别的根源是一句话：mod 内部用 `IsGameplayEnchantment(e) = e is not MarkerEnchantmentModel` 把所有 `MarkerEnchantmentModel` 划成“非 gameplay”。

| 维度 | 正常附魔 `EnchantmentModel` | 存储型 marker（`MarkerEnchantmentModel` 实例） | 显示层 marker（provider / `RegisterMarker`） |
|---|---|---|---|
| 是不是真实附魔实例 | ✅ 是 | ✅ 是 | ❌ 不是，渲染时按谓词重算 |
| 怎么产生 | `Enchant(card, model)` | `Enchant(card, 某个 MarkerEnchantmentModel)` | `RegisterMarker` / `RegisterMarkerDisplayProvider` / `IconState` |
| 进存档、随卡 clone | ✅ | ✅ | ❌ |
| `GetEnchantment<T>`/`HasEnchantment<T>` 查得到 | ✅ | ✅ | ❌ |
| `GetMarkers`/`GetMarker<T>` 查得到 | ❌（它不是 marker） | ✅ | ❌ |
| 参与战斗钩子 / 伤害 / 格挡 / DynamicVar / 能量管线 | ✅ | ❌ | ❌ |
| 触发 `OnApplied`/`OnPlay`/`AfterCardEnchanted` 等 lifecycle | ✅ | ❌ | ❌（根本没有实例） |
| 计入 `HasAnyEnchantment`/`GetEnchantmentCount` | ✅ 默认计入 | ❌ 默认不计（需 `includeMarkers: true`） | ❌ |
| 进 application order / 战斗历史 | ✅ | ❌（历史默认 `Hidden`） | ❌ |
| 能读写 `Amount`/`Props` 当数据载体 | ✅ | ✅ | ❌（无实例，改谓词读取的状态） |
| 默认视觉 | 有背景板、可显示数字与卡面额外文字 | 无背景板、不显示数字与卡文、disabled 隐藏、`DisplayPriority=1000` | 同存储型默认（`MarkerPresentation.Default`） |
| 典型用途 | 真正改变卡牌行为 | 持久、要存档、可按类型查回的“标记位/计数器”，但本身不产生效果 | 纯按条件显示的装饰角标 |

一句话决策：

- 会**改变卡牌行为** → 正常 `EnchantmentModel`。
- **不改行为**，但要“在卡上打一个会存档、能 `GetMarker` 查回、可读写 `Amount`/`Props` 的真实标记” → 存储型 `MarkerEnchantmentModel`。
- **不改行为**，也**不用存档**，只是“满足条件就画个图标” → 显示层 marker（最轻量，首选）。

要点：存储型 marker 和正常附魔**都是真实实例、都进存档、都能按类型查到**；唯一差别是 marker 被标记成非 gameplay，所以**不触发任何战斗逻辑/钩子、不计入附魔数、不进历史，默认只画一个无背景板的小图标**。

## 2. 添加 API 版本检查

在你的程序集里声明兼容版本：

```csharp
using MultiEnchantmentMod.Api;

[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]
```

初始化时先检查版本，再注册图标：

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

注意：把 `MultiEnchantmentMod.dll` 当作前置依赖引用。不要复制到你自己的输出目录。

## 3. 创建静态 Marker

先创建一个空 marker 类型：

```csharp
using MultiEnchantmentMod.Api;

public sealed class BloodMarkedIcon : MarkerEnchantmentModel
{
}
```

再注册它：

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

替换这几处：

- `BloodMarkedIcon`：你的 marker 类型。
- `res://images/markers/blood_marked.png`：你的纹理路径。
- `appliesTo`：哪些牌显示这个徽章。

注意：`BloodMarkedIcon` 可以是空类。这个类型是 marker 的 key。UI 会用它做排序、去重、同类型压制和查询。

验证：

- 编译你的 mod。
- 打开一张满足 `appliesTo` 的牌。
- 卡牌图标行里应该出现徽章。
- 如果没出现，先查 predicate 和图标路径。

## 4. 设置固定选项

静态 marker 需要常用开关时，使用 `MarkerRegistrationOptions`。

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

字段说明：

- `Icon`：显式纹理。自定义图标推荐这样传。
- `PresentationStyle`：图标大小、偏移、染色、底板、disabled 行为和排序。
- `ShowAmount`：必须设成 `true`，`Amount` 才会显示。
- `Amount`：固定数字，所有匹配卡牌一样。
- `ShowWithLiveEnchantment`：只有确实要和同类型 live 附魔共存时才设 `true`。

如果 `Amount` 要按牌变化，不要走静态路径。用 provider。

## 5. 创建动态 Provider

图标输出依赖卡牌状态时，用 provider。

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

把 `BloodState.GetCharges(card)` 换成你自己的状态读取。

`MarkerDisplay` 常用字段：

- `EnchantmentType`：必填。推荐用 `MarkerEnchantmentModel` 子类。
- `Icon`：要画的纹理。
- `Enchantment`：可选模型来源，用来提供图标、状态、hover tips。
- `PresentationStyle`：这个显示项的样式。
- `ShouldDisplay`：最后一层显示判断。
- `ShowAmount` / `Amount`：徽章上的数字。
- `ShowWithLiveEnchantment`：允许和同类型 live 附魔共存。

`MarkerDisplayContext` 提供：

- `Card`：正在刷新的 `CardModel`。
- `HasLiveEnchantment`：这张牌是否已经有同精确类型的 live 附魔。
- `IsCombatCard`：是否是战斗卡牌。
- `IsPreviewCard`：是否是附魔预览牌。

验证：

- 改变 `GetIcons` 读取的状态。
- 调用 `MultiEnchantmentApi.RefreshMarkers(card)`。
- 图标应该出现、消失，或更新数字。

## 6. 状态变化后刷新 UI

显示层图标是根据 provider 状态重新计算的。没有“修改某个注册”的 API。

只刷新一张牌：

```csharp
MultiEnchantmentApi.RefreshMarkers(card);
```

刷新所有已知卡牌 UI：

```csharp
MultiEnchantmentApi.RefreshMarkers();
```

注意：存储型 marker 在移除或 `NotifyPropsChanged` 时会自己刷新。provider marker 如果要立刻更新 UI，需要你主动刷新。

## 7. 把 Model 状态映射成图标

常见场景：真实状态属于某张 card、某个 ability、relic 或普通 enchantment。你只是想在卡牌上用 marker 把这个状态显示出来。

把 `IconState<TMarker>` 当成 UI 投影。它不是 gameplay 状态的主人。

推荐模式：

1. 创建一个 `IconState<TMarker>`（每个 marker 类型一个实例）。
2. 真实状态仍然存在真实 model 上。
3. model 状态变化后，用 `Set`/`Show` 或 `Remove` 同步显示值。

`IconState<TMarker>` 是对 provider 模式的小封装。它保存临时每卡 UI 状态，负责显示 marker，并在每次修改后刷新这张牌。`Register()` 是可选的——第一次修改会自动注册 provider。

它提供两种投影方式：

- **按量门控**（`Set`、`Add`）：只有 amount 为正时才显示，amount ≤ 0 即移除。适合"归零就消失"的计数图标。
- **显式存在**（`Show`）：显示后一直保留，直到 `Remove`/`Clear`；amount 只是个标签、可以是 `0`；并可用 `IconStateOverride` 为这张卡单独定制图标 / hover / 表现 / 数字标签。适合每卡不同的美术，或需要显示 `0` 的计数器。

`Has(card)` 是存在性判断（`Show` 出来的 amount 0 标记也返回 true）；`Get(card)` 返回数值。

创建 marker 和图标投影：

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
        // 可选：第一次 Set/Add/Show 也会自动注册。
        State.Register();
    }

    public static void Uninstall()
    {
        // Dispose 是终态：会同时取消注册并清空所有投影。
        State.Dispose();
    }
}
```

然后从真实 model 同步它。

这个例子里，普通附魔 `BloodCharge` 才是 gameplay 状态的主人。它组合在卡牌上；图标只显示 `enchantment.Amount`。

注意：这里用的是本 mod 的 lifecycle wrapper，不是游戏原生 override。wrapper 签名是
`OnCardDrawn(CardModel card, TEnchantment enchantment)`。

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

        // BloodCharge 拥有状态。BloodChargeIcon 只负责显示。
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

如果你直接 override 游戏原生 hook，请保持游戏里的真实签名：

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

### 用 `Show` 实现每卡图标 / tooltip

想在共用一个 `IconState` 的前提下，为不同卡牌定制美术或 hover 文本，就用 `Show` 配 `IconStateOverride`。留 `null` 的字段会回退到构造函数里的值。

```csharp
// 同一 marker 类型，每卡不同图标 + tooltip，且计数器可以显示 "0"。
BloodChargeIcons.State.Show(cardA, amount: 0, overrides: new IconStateOverride
{
    Icon = GD.Load<Texture2D>("res://images/markers/blood_charge_empty.png"),
    ShowAmount = true,
});

BloodChargeIcons.State.Show(cardB, amount: 3, overrides: new IconStateOverride
{
    Icon = GD.Load<Texture2D>("res://images/markers/blood_charge_full.png"),
    Enchantment = someEnchantmentForThisCardsHoverTip, // 决定这张卡的 hover 文本
});
```

CRUD 对照：

| 操作 | 调用 |
|---|---|
| 注册 provider（可选，首次修改会自动注册） | `BloodChargeIcons.State.Register()` |
| 显示当前 model 状态（按量门控） | `BloodChargeIcons.State.Set(card, displayAmount)` |
| 每卡定制美术 / 允许显示 "0" | `BloodChargeIcons.State.Show(card, amount, overrides)` |
| 增加 UI 投影值 | `BloodChargeIcons.State.Add(card, amount)` |
| 隐藏投影 | `BloodChargeIcons.State.Remove(card)` |
| 清空所有卡（只刷新被追踪的卡） | `BloodChargeIcons.State.Clear()` |
| 只刷新被追踪的卡 | `BloodChargeIcons.State.RefreshTracked()` |
| 列出被追踪的卡 | `BloodChargeIcons.State.GetTrackedCards()` |
| 取消注册 + 清空（终态） | `BloodChargeIcons.State.Dispose()` |
| 查看数值 / 存在性 | `BloodChargeIcons.State.Get(card)` / `Has(card)` |
| 查看最终 UI 行 | `MultiEnchantmentApi.GetShownMarkerDetails(card)` |

临时 UI 投影推荐用 `IconState<TMarker>`。如果 marker 本身必须作为卡牌状态 save/load，请改用存储型 `MarkerEnchantmentModel`。如果 marker 会改变 gameplay，请用普通 `EnchantmentModel`。

验证：

- 抽到这张牌。
- `BloodCharge.Amount` 变化。
- `BloodChargeIcons.State.Set(card, amount)` 被调用。
- 徽章显示附魔上的数量。
- `GetShownMarkerDetails(card)` 里能看到 `BloodChargeIcon`。

## 8. 设置样式和排序

从 `MarkerPresentation.Default` 开始改。

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

默认 marker 样式：

- `ShowBadgeBacking = false`
- `HideWhenDisabled = true`
- `DisplayPriority = 1000`

排序规则：

- `DisplayPriority` 越高越靠前。
- `HideWhenDisabled = true` 时，disabled marker 不显示。
- 两个显示项使用同一个精确 `EnchantmentType` 时会合并；除非后一个设置 `ShowWithLiveEnchantment = true`。

## 9. 正确提供图标

不要 override `EnchantmentModel.Icon`。它不是 virtual。

使用下面任意一种方式：

| 方式 | 适用场景 |
|---|---|
| `Icon = GD.Load<Texture2D>("res://...png")` | 推荐。自定义图标最直接 |
| `icon: ModelDb.Enchantment<Sharp>().Icon` | 借用已有附魔图标 |
| `MarkerDisplay.Enchantment = someModel` | 需要从模型读取图标、状态、hover tips |
| 约定图标路径 | marker 已作为 canonical model 注册 |

注意：如果没有解析到纹理，这个 marker 会被跳过，并只记录一次日志。不会显示缺失图标占位符。

## 10. 添加 Hover 提示

卡牌 hover tips 来自 `Enchantment.HoverTips`。provider marker 只有在有模型来源时，才能贡献 hover tips。

用下面任意一种：

- 传 `MarkerDisplay.Enchantment`。
- 让 `EnchantmentType` 解析到定义了 `ExtraHoverTips` 的 canonical model。

不要指望裸 `Icon` 纹理提供 hover 文本。它只能画图标，没有文本来源。

## 11. 只在需要时使用存储型 Marker

只有 marker 需要真实的每卡数据时，才用存储型 `MarkerEnchantmentModel`。

它仍然不是 gameplay：

- 不调用 `ModifyCard`
- 不触发 `OnApplied` 等 lifecycle hook
- 不触发 `AfterCardEnchanted`
- 不参与伤害/格挡/dynamic-var 管线
- 不进入 gameplay 应用顺序

存储型 marker：

- 会被 `GetMarkers(card)` 返回；
- 会随同一个卡牌对象 save/load；
- 会随普通卡牌 clone 携带；
- 不会通过 compatible transform copy 转移；
- 不会因为在战斗中创建，就变成永久牌组附魔。

如果这个东西应该影响 gameplay，请改成普通 `EnchantmentModel`。

## 12. 查询卡牌存储状态

想查卡牌实际带着哪些附魔实例，用这些 API。

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

注意：`includeMarkers: true` 包含存储型 `MarkerEnchantmentModel` 实例。不包含显示层 provider marker。

按类型查存储型 marker：

```csharp
BloodStoredMarker? marker = MultiEnchantmentApi.GetMarker<BloodStoredMarker>(card);
bool hasMarker = MultiEnchantmentApi.HasEnchantment<BloodStoredMarker>(card);
```

### 存储型 marker 的友好增删改查

不必手动创建实例、调用 `Enchant`、记得 `NotifyPropsChanged`——下面的 CRUD 一步到位（自动从
`ModelDb` 解析可变实例、自动通知变更并刷新图标行）：

```csharp
// 读取或创建（缺失时以 amount 创建）
SampleChargeCounter? m = MultiEnchantmentApi.GetOrAddMarker<SampleChargeCounter>(card);

// 设为精确值（缺失时创建）
MultiEnchantmentApi.SetMarker<SampleChargeCounter>(card, amount: 3);

// 计数 +1 / -1（缺失时以 delta 创建；归零不会自动移除）
int now = MultiEnchantmentApi.AddMarkerAmount<SampleChargeCounter>(card, +1);

// 任意修改（仅作用于已存在的 marker，自动通知刷新）
MultiEnchantmentApi.ModifyMarker<SampleChargeCounter>(card, m => m.Amount *= 2);

// 移除
MultiEnchantmentApi.RemoveMarker<SampleChargeCounter>(card);
```

marker 实例也能**自己修改自己**——当别处代码（遗物、能力、附魔钩子）持有实例时：

```csharp
foreach (var marker in MultiEnchantmentApi.GetMarkers(card))
{
    marker.AddAmount(1);      // Amount += 1 并自动刷新
    // marker.SetAmount(0);   // 设为精确值并自动刷新
    // 直接改 Props 后调 marker.NotifyChanged();
}
```

注意：marker 类型需要在 `ModelDb` 注册（save/load 本来就要求这一点）；目标卡必须可变（mutable）。

## 13. 查询当前可见图标

想查最终图标行，用这些 API。

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

这些查询已经经过：

- provider 计算；
- `ShouldDisplay`；
- 同类型压制；
- disabled marker 隐藏；
- 图标解析；
- `DisplayPriority` 排序。

## 14. 让 Provider 便宜一点

provider 运行很频繁。写小一点。

建议：

- 缓存纹理；
- 读卡牌状态或本地状态；
- 用一个 provider 返回多个图标；
- unload 时 dispose 注册。

避免：

- 在 provider 循环里 `GD.Load`；
- 每次刷新都扫描整副牌组；
- 分配很大的临时集合；
- 依赖 provider 异常保护当正常流程。

## 15. 排错

| 现象 | 检查 |
|---|---|
| 图标完全不出现 | `appliesTo`、`ShouldDisplay`、图标路径、同类型压制 |
| 重新打开界面后才出现 | 调用 `RefreshMarkers(card)` |
| 数字不显示 | 设置 `ShowAmount = true` |
| 没有 hover 提示 | 提供 `MarkerDisplay.Enchantment` 或 canonical model hover tips |
| `HasAnyEnchantment(card)` 是 false | 显示层 marker 的预期行为；改用可见图标查询 |
| 两个 marker 合成一个 | 用不同 marker 类型，或设置 `ShowWithLiveEnchantment = true` |
| provider 不再运行 | 看日志里是否有连续异常 |

成功标准：

- 匹配卡牌显示徽章。
- 不匹配卡牌不显示徽章。
- `GetShownMarkerDetails(card)` 返回的内容和 UI 一致。
- gameplay 判断仍然忽略显示层 marker。

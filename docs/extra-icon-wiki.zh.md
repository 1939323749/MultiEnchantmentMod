# Extra Icon 下游作者中文 Wiki

这篇文档面向想把 `MultiEnchantmentMod` 作为前置、只借用卡牌 UI 图标能力的下游作者。这里刻意跳过普通附魔的设计细节。适用场景包括：状态标记、关键词徽章、卡牌家族图标、牌库/预览界面的提示图标，且这些图标不应该被当成 gameplay 附魔。

英文版见 [`docs/extra-icon-wiki.md`](extra-icon-wiki.md)。完整类型参考见 [`docs/public-api.md`](public-api.md)。

## 目录

- [Extra Icon 是什么](#extra-icon-是什么)
- [两种形态速览](#两种形态速览)
- [快速上手：静态图标 Marker](#快速上手静态图标-marker)
- [怎么选](#怎么选)
- [前置依赖与注册生命周期](#前置依赖与注册生命周期)
- [显示样式](#显示样式)
- [`ExtraIconDisplay` 字段（provider 路径）](#extraicondisplay-字段provider-路径)
- [动态 provider 与 `ExtraIconDisplayContext`](#动态-provider-与-extraicondisplaycontext)
- [图标解析契约](#图标解析契约)
- [删除与更新图标](#删除与更新图标)
- [Hover 提示：解释你的图标](#hover-提示解释你的图标)
- [存储型（live）marker 实例](#存储型live-marker-实例)
- [“是否有附魔”的判断](#是否有附魔的判断)
- [性能与最佳实践](#性能与最佳实践)
- [排错](#排错)

## Extra Icon 是什么

Extra icon 是由 multi-enchantment UI 层渲染的卡牌徽章。它可以复用 `EnchantmentModel` 的图标和显示样式，但默认不计入 gameplay 附魔。

Extra icon 默认会从以下行为中排除：

- `MultiEnchantmentApi.HasAnyEnchantment(card)` 和 `GetEnchantmentCount(card)`。
- `GetSiblings(card)` 这类 sibling 读取。
- gameplay 附魔的 stack snapshot。
- 伤害、格挡、重放次数、费用、动态变量贡献。
- `OnPlay`、`OnCombatStart`、`OnApplied`、sibling callbacks 等生命周期。
- compatible transform copy，以及 public copy/move helpers。
- 战斗记录，除非你把它建模成真正的 gameplay 附魔。

它**会**参与：卡牌 UI 渲染、视觉 active-status 扫描（所以 `HideWhenDisabled` 能用）、按类型查询（`HasEnchantment<TMarker>` / `GetEnchantmentCount(card, typeof(TMarker))` 会算上 marker，因为查询的类型本身就是 marker 类型），以及——对存储型 marker——存档/读档。

只有在你明确想把 UI marker 也算进去时，才传 `includeExtraIcons: true`：

```csharp
bool hasGameplayEnchantments = MultiEnchantmentApi.HasAnyEnchantment(card);
bool hasGameplayOrMarkers = MultiEnchantmentApi.HasAnyEnchantment(card, includeExtraIcons: true);

int gameplayCount = MultiEnchantmentApi.GetEnchantmentCount(card);
int gameplayOrMarkerCount = MultiEnchantmentApi.GetEnchantmentCount(card, includeExtraIcons: true);
```

## 两种形态速览

`ExtraIconEnchantmentModel` 有两种用法。大多数 mod 只需要第一种。

| | **display-only provider marker** | **存储型 marker 实例** |
|---|---|---|
| 怎么出现在卡上 | 你注册的 provider 在 UI 时合成 | 你把 `ExtraIconEnchantmentModel` `Enchant` 到卡上 |
| 是否存在于卡的附魔列表 | 否 | 是（在 extra slot） |
| 是否进存档 | 否——由 provider 重新生成 | 是 |
| 是否需要 predicate 决定显隐 | 是（`appliesTo` / `ShouldDisplay`） | 否——存在直到被移除 |
| 在牌库/奖励/预览这类无运行时状态的卡上能用吗 | 能 | 只有你主动放上去时 |
| 典型用途 | 关键词/家族/稀有度徽章、“这张牌很特别”的标记 | 按卡保存的装饰性状态 |

拿不准时，用 display-only provider。

## 快速上手：静态图标 Marker

最小模式是：

1. 创建一个继承 `ExtraIconEnchantmentModel` 的 marker 类。
2. 给它提供 UI 要显示的图标。
3. 注册一个 predicate，决定哪些卡显示这个图标。

```csharp
using System;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

// marker 类型只是一个 key，不需要类体。EnchantmentModel.Icon **不可重写**（非 virtual），
// 所以用下面的 icon 参数来提供图片。
public sealed class BloodMarkedIcon : ExtraIconEnchantmentModel
{
}

public static class BloodMarkedIcons
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.RegisterExtraIcon<BloodMarkedIcon>(
            appliesTo: card => card.Id.Entry.StartsWith("Vampire", StringComparison.Ordinal),
            presentationStyle: ExtraIconPresentation.Default with
            {
                IconScale = 1.25f,
            },
            icon: GD.Load<CompressedTexture2D>("res://images/enchantments/blood_marked.png"));
    }

    public static void Uninstall()
    {
        _registration?.Dispose();
        _registration = null;
    }
}
```

`RegisterExtraIcon<T>` 是便捷入口：`appliesTo` 加可选的 `presentationStyle`、`shouldDisplay`、`icon`。如果要显示数量 label、与 live 附魔共存、或按卡变化图标，请用 [provider API](#动态-provider-与-extraicondisplaycontext)。

## 怎么选

| 用… | 何时 |
|---|---|
| `RegisterExtraIcon<T>` | 每张卡一个静态图标，由简单 predicate 决定。 |
| `RegisterExtraIconDisplayProvider` | 一个 provider 可能返回多个图标、图标/样式/数量依赖卡牌，或你需要 `ShowAmount` / `ShowWithLiveEnchantment`。 |
| 存储型 `ExtraIconEnchantmentModel` 实例 | marker 有按卡保存的状态、应随卡清除/恢复，但不能影响 gameplay 计算。 |
| 普通 `EnchantmentModel`（+ `PresentationStyle`） | 卡牌文本/数值/费用/打出次数会变、效果要在打出/抽牌/战斗/回合/移除时触发，或其它 mod 应把它当真实附魔。 |

## 前置依赖与注册生命周期

把 `MultiEnchantmentMod.dll` 当成运行时前置引用，不要复制进你自己的 mod 输出目录。完整 `.csproj` 写法见 [`docs/integration.md`](integration.md)。每个程序集声明一次 API 兼容版本：

```csharp
using MultiEnchantmentMod.Api;

[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]
```

注册前先检查 API 版本：

```csharp
public static void Initialize()
{
    if (!MultiEnchantmentApi.RequireApiVersion(MultiEnchantmentApiVersion.Current))
    {
        return;
    }

    MyExtraIcons.Install();
}
```

生命周期规则：

- **允许晚注册。** 与附魔注册不同，display provider 属于纯 UI 注册表，**不会**被 `SealRegistry()` 冻结。懒注册（比如第一次打开牌库时）也没问题，图标会在下一次视觉刷新时出现。
- **务必 Dispose。** 保存返回的 `IDisposable`，并在 mod 卸载 / 功能关闭时 Dispose，否则 provider 会一直对每张卡运行。例子里的 `??=` 守卫可防止重入式 `Install()` 重复注册。
- **坏 provider 会被自动停用。** 连续若干次抛异常后，框架会停用该 provider（以 error 级别记录一次），让它不再对每张卡刷新都跑；之后只要有一次成功调用计数就重置，因此偶发失败会被容忍。

## 显示样式

marker 复用 `EnchantmentPresentationStyle`。从 `ExtraIconPresentation.Default` 出发，用 `with` 表达式微调：

```csharp
presentationStyle: ExtraIconPresentation.Default with
{
    ShowBadgeBacking = false,
    IconScale = 1.35f,
    IconOffset = new Vector2(0, -3),
    IconTint = Colors.White,
    DisabledIconTint = new Color(0.5f, 0.5f, 0.5f, 0.75f),
    HideWhenDisabled = true,
    DisplayPriority = ExtraIconPresentation.DefaultDisplayPriority + 50,
}
```

`ExtraIconPresentation.Default` 即 `ShowBadgeBacking = false`、`HideWhenDisabled = true`、`DisplayPriority = 1000`（= `ExtraIconPresentation.DefaultDisplayPriority`）。

| 字段 | 默认（原始样式 / `ExtraIconPresentation.Default`） | 作用 |
|---|---|---|
| `ShowBadgeBacking` | `true` / `false` | 是否在图标后画 vanilla 徽章底图。 |
| `BadgeBackingTexture` | `null` | 覆盖底图纹理（仅在 `ShowBadgeBacking` 时）。 |
| `IconScale` | `1f` | 只缩放 `%Enchantment/Icon` 节点。`0`、负数、`NaN`、无穷 → `1f`。 |
| `IconOffset` | `Vector2.Zero` | 图标节点的像素偏移。 |
| `IconTint` | `null`（白） | 施加到图标的 `SelfModulate`。 |
| `DisabledIconTint` | `null` | 状态为 Disabled 时的着色。有底图时 vanilla 去饱和 shader 已经把它变暗了，所以除非想要特定禁用色，留 null 即可。 |
| `HideWhenDisabled` | `false` / `true` | 禁用时整条隐藏，而非变暗。 |
| `DisplayPriority` | `0` / `1000` | 排序键，**值越高越靠前**。见下。 |
| `PreserveExtraTextBbCode` | `false` | 只与 gameplay 附魔的卡牌文本有关；marker 设了 `HasExtraCardText = false`，忽略即可。 |

`IconScale` 只缩放渲染出来的图标节点——不改 PNG 源图、徽章间距或数量 label。

### DisplayPriority 与主槽位

视觉条目按 `DisplayPriority` 从高到低渲染，且第一条占据卡牌的**主**（vanilla）徽章槽位。由于 `ExtraIconPresentation.Default` 用 `1000`、gameplay 附魔默认 `0`，**默认 marker 会渲染在普通附魔徽章之前并占据主槽位**。这对需要醒目的 marker 是有意为之。

如果你反而想让 marker 排在卡牌真实附魔徽章**之后**，给它一个低于 gameplay 默认值的优先级：

```csharp
ExtraIconPresentation.Default with { DisplayPriority = -1 }
```

相同优先级保持应用 / provider 顺序。

## `ExtraIconDisplay` 字段（provider 路径）

provider 产出 `ExtraIconDisplay` 记录：

- `EnchantmentType`（必填）：作为 marker 的 key（去重 / 抑制 / 视觉 id）。应当是 `ExtraIconEnchantmentModel` 子类；如果传入普通 gameplay 附魔类型，框架会记录一次性警告。
- `Icon`：要画的显式 `Texture2D`，覆盖其它一切。这是使用任意美术的途径（见[图标解析契约](#图标解析契约)）。
- `Enchantment`：可选的预构造 model，用来读图标。当它和 `Icon` 都省略时，框架会查 `EnchantmentType` 的 canonical model（经 `ModelDb`）并读它的图标。
- `PresentationStyle`：单条 display 的样式覆盖，缺省回退到该类型注册的样式。
- `ShouldDisplay`：每次刷新调用的 predicate（见下方 context）。
- `ShowAmount` + `Amount`：在图标上画数字。`ExtraIconEnchantmentModel` 硬关闭了自己的 `ShowAmount`，所以这是 display-only marker 显示数字的唯一途径（默认：`false` / `1`）。
- `ShowWithLiveEnchantment`：默认情况下，当卡上已经有同类型的 live 附魔（或另一个 marker）时，marker 会被抑制（让真实徽章占据槽位）。置为 `true` 则无论如何都渲染——例如要与 live 徽章共存的装饰性叠加图标。

数量示例——一个从卡上读取计数的层数图标：

```csharp
yield return new ExtraIconDisplay
{
    EnchantmentType = typeof(ChargeMarker),
    Icon = GD.Load<CompressedTexture2D>("res://images/markers/charge.png"),
    ShowAmount = true,
    Amount = GetChargeCount(card),
    PresentationStyle = ExtraIconPresentation.Default,
};
```

## 动态 provider 与 `ExtraIconDisplayContext`

一个 provider 可能返回多个图标，或图标实例/样式/数量依赖卡牌时，使用 `RegisterExtraIconDisplayProvider`。

```csharp
using System.Collections.Generic;
using MultiEnchantmentMod.Api;

public static class DynamicMarkerIcons
{
    private static IDisposable? _registration;

    public static void Install()
    {
        _registration ??= MultiEnchantmentApi.RegisterExtraIconDisplayProvider(GetIcons);
    }

    private static IEnumerable<ExtraIconDisplay> GetIcons(CardModel card)
    {
        if (!ShouldShowBloodMarker(card))
        {
            yield break;
        }

        yield return new ExtraIconDisplay
        {
            EnchantmentType = typeof(BloodMarkedIcon),
            Icon = GD.Load<CompressedTexture2D>("res://images/enchantments/blood_marked.png"),
            PresentationStyle = ExtraIconPresentation.Default with
            {
                IconScale = card.IsEnchantmentPreview ? 1.15f : 1.3f,
            },
            ShouldDisplay = context =>
                context.IsPreviewCard ||
                context.IsCombatCard ||
                !context.HasLiveEnchantment,
        };
    }
}
```

`ExtraIconDisplayContext` 提供：

- `Card`：正在刷新的 UI 卡牌。这是完整的 `CardModel`，predicate 可以直接读它需要的任何字段——`card.Pile`（牌库/手牌/抽牌堆/弃牌堆）、`card.Keywords`、`card.Type` 等。context 不需要镜像每个卡牌字段。
- `HasLiveEnchantment`：这张卡上是否已经有同类型 live 附魔实例。
- `IsCombatCard`：这张卡当前是否有 combat state。做“只在战斗中显示”的 marker 用它：`ShouldDisplay = ctx => ctx.IsCombatCard`。牌库 / 百科 / 奖励卡都不是 combat card，这样就能把战斗标记挡在那些界面之外。
- `IsPreviewCard`：这张卡是否是附魔预览卡。

注册之后，provider 会对**每张卡**的**每次**视觉刷新都运行，所以 predicate 要保持轻量（见[性能](#性能与最佳实践)）。

## 图标解析契约

**`EnchantmentModel.Icon` 不可重写**——它是非 virtual 的，从一个由 model id 推导的约定路径解析纹理。所以 `public override Texture2D Icon => …` **无法编译**，而一个在约定路径上没有纹理的 marker，其 `Icon` 为 null。框架按以下顺序选图标：

1. `ExtraIconDisplay.Icon`（或 `RegisterExtraIcon` 的 `icon:` 参数）——显式纹理。这是使用任意美术的途径，例如 `GD.Load<CompressedTexture2D>("res://…png")`，或借用别的附魔图标 `ModelDb.Enchantment<SomeEnchantment>().Icon`。
2. `ExtraIconDisplay.Enchantment` 的 `Icon`。
3. `EnchantmentType` 的 canonical model 图标，从 `ModelDb` 取（框架**绝不** new 一个 model——`new` 会抛 `DuplicateModelException`）。只有当你在该类型的约定图标路径上放了纹理，这一步才非 null。

如果都得不到非 null 纹理，marker 会被**跳过**（记一次日志）——**没有占位符 / “missing icon” 保底**，这是有意的（否则会在真实卡上画出破图方块）。如果你想定制附魔自身的 `Icon`（而不是提供显示纹理），请在它的图标路径放文件，或 Harmony patch `EnchantmentModel.get_Icon`（进阶）。

## 删除与更新图标

两种形态都支持删除和修改。没有“原地编辑某个注册”的调用——display-only 图标是 predicate 驱动的，你通过改变 provider 返回的内容来改行为。

**display-only provider 图标**

- *删除整个注册：* Dispose `RegisterExtraIcon` / `RegisterExtraIconDisplayProvider` 返回的 `IDisposable`。
- *按卡显示 / 隐藏 / 改样式：* 从 provider 里返回（或不返回）一个 `ExtraIconDisplay`，或用 `ShouldDisplay` 控制。provider 每次刷新都会重跑，所以**修改图标就是改变它读取的状态**（换图标 / 样式 / 数量）。
- *让改动立刻生效：* 已经在屏幕上的卡——尤其是百科这种静态卡——不会自己重绘。改完 provider 状态或 Dispose 之后，调用 `RefreshExtraIcons(card)`；或 `RefreshExtraIcons()` 刷新所有已渲染的卡。

```csharp
// 不再显示该 marker，并更新任何已经在屏幕上的卡
_registration?.Dispose();
_registration = null;
MultiEnchantmentApi.RefreshExtraIcons();          // 或 RefreshExtraIcons(具体的卡)
```

**存储型 marker 实例**

- *删除：* `MultiEnchantmentApi.RemoveEnchantment(card, marker)`——marker 会跳过 gameplay 的 `OnRemoved` 否决，所以总能移除。
- *修改：* 改 marker 的 `Props` / `Amount`，然后 `MultiEnchantmentApi.NotifyPropsChanged(marker)` 刷新派生状态并重绘。存储型 marker 的删除/修改会**自动**刷新卡牌——它们**不需要** `RefreshExtraIcons`。

## Hover 提示：解释你的图标

原版在**卡级**展示附魔悬停文本（悬停卡牌会列出 `Enchantment.HoverTips`）——并没有钉在单个徽章上的 tooltip。marker 用的是同一套机制。`EnchantmentModel` 的重写点是**受保护的** `ExtraHoverTips`，公开的 `HoverTips` 会把它（连同关键词提示）聚合进来。在你的 marker 类型上重写 `ExtraHoverTips`，marker 正在该卡显示时，这些提示就会出现在卡牌悬停里。

```csharp
using System.Collections.Generic;
using MegaCrit.Sts2.Core.HoverTips;

public sealed class BloodMarkedIcon : ExtraIconEnchantmentModel
{
    public override Texture2D Icon =>
        GD.Load<Texture2D>("res://images/enchantments/blood_marked.png");

    // 受保护的重写点（不是 HoverTips）。公开的 HoverTips 会包含你在这里返回的内容。
    // 往数组里填你的提示，例如 HoverTipFactory.FromKeyword(CardKeyword.X)。
    protected override IEnumerable<IHoverTip> ExtraHoverTips => new IHoverTip[]
    {
        /* 你的关键词 / 解释性 IHoverTip */
    };
}
```

存储型 marker 和 display-only provider marker 都适用。被同类型 live 附魔抑制掉的 marker 不会贡献提示（那个附魔已经展示自己的提示了），所以不会重复。

## 存储型（live）marker 实例

大多数 marker icon 都应该用 display-only provider，尤其是你希望它出现在牌库、奖励、tooltip 或预览卡上时。

如果你确实把 `ExtraIconEnchantmentModel` 实例挂到卡上，框架会强制把它放进 extra slot，而不是 `card.Enchantment` 主槽。它仍然会渲染，并且可以用 active-status predicate 控制变灰/隐藏（`HideWhenDisabled` 能生效，因为视觉 active-status 扫描包含 marker）。

但它仍然不会表现成 gameplay 附魔：

- 不调用 `ModifyCard`；
- 不触发 `OnApplied` / sibling lifecycle hooks；
- 不触发 `AfterCardEnchanted`；
- 不参与伤害、格挡、动态变量等管线；
- 永远不进入 gameplay 应用/重放顺序（`ApplicationOrder`）；marker 完全按 `DisplayPriority` 排序。

**存储型 marker 会跟随什么**——规则是“跟随同一个卡牌对象，而不是卡牌身份的转变”：

| 操作 | marker 是否跟随 | 原因 |
|---|---|---|
| 存档 / 读档 | 是 | 同一张卡，按卡持久化的状态 |
| 卡牌复制 / 克隆 | 是 | 同一张逻辑卡 |
| 兼容**转化**复制（`TryCopyCompatibleEnchantments`） | 否 | 卡牌变成了*另一张*卡，只转移 gameplay 附魔 |
| 牌组版本同步 | 否 | 战斗中加的 marker 不是永久牌组附魔 |

如果你需要 marker 在转化后保留、或持久化进牌组，请把它建模成普通附魔。

## “是否有附魔”的判断

下游卡牌和遗物做 gameplay 判断时，应该使用默认 helper：

```csharp
if (MultiEnchantmentApi.HasAnyEnchantment(card))
{
    // 这里只代表 gameplay 附魔，extra icon 不算。
}
```

只有 UI、debug 或统计 badge-like 内容时才传 `includeExtraIcons: true`：

```csharp
int badgeLikeThings = MultiEnchantmentApi.GetEnchantmentCount(card, includeExtraIcons: true);
```

这样可以避免 marker 徽章意外触发“若这张牌有附魔”“所有手牌都有附魔才能打出”“每有一张有附魔的牌获得额外伤害”等 gameplay 条件。

注意按类型查询的 helper 是个例外：`HasEnchantment<TMarker>(card)` 和 `GetEnchantmentCount(card, typeof(TMarker))` **会**报告 marker 类型，因为专门去问一个 marker 类型显然就是想把 marker 算进来。

## 性能与最佳实践

- 一旦注册了任何 provider，每张卡在每次 `UpdateVisuals` 和每次卡牌悬停时都会跑所有 provider。predicate 要无分配、轻量；不要在里面加载纹理、扫描整副牌组或分配对象。没有任何 provider 注册时，整条路径是廉价的空操作（`HasProviders` 短路），未使用的 mod 零开销。
- 纹理只解析一次（缓存字段，或 `GD.Load` 一个已导入的资源），不要每次刷新都加载。
- 优先用一个产出多个 `ExtraIconDisplay` 的 provider，而不是多个独立 provider。
- 卸载时 Dispose 注册；把熔断当安全网，而不是正常的流程控制。

## 排错

| 现象 | 可能原因 / 修法 |
|---|---|
| 图标完全不出现 | 多半是没解析到纹理——给 `RegisterExtraIcon` 传 `icon:` 或设 `ExtraIconDisplay.Icon`（`EnchantmentModel.Icon` **不能 override**，它是非 virtual 的）。详见[图标解析契约](#图标解析契约)和那条一次性失败日志；并确认 predicate 返回 true、provider 已注册（且没被停用——见下）。 |
| 图标出现在牌库/奖励/百科，但你只想要战斗中显示 | 用 `ShouldDisplay = ctx => ctx.IsCombatCard` 限制。 |
| marker 占了主徽章槽，把真实附魔挤开 | 符合预期：默认 `DisplayPriority` 是 `1000`。用 `DisplayPriority = -1`（低于 gameplay 默认 `0`）让它排在 gameplay 徽章之后。 |
| 卡上已有同类型 live 附魔时 marker 不显示 | 设计如此——设 `ShowWithLiveEnchantment = true` 让它共存。 |
| 数量 label 不显示 | `RegisterExtraIcon<T>` 设不了；改用 provider 路径，设 `ShowAmount = true` 和 `Amount`。 |
| hover tooltip 不出现 | 在 marker 类型上重写 `HoverTips`；只有 marker 实际显示、且没被同类型 live 附魔抑制时才出现。 |
| 改了 provider 状态（或 Dispose 了）但屏幕上的卡还是旧图标 | 那张卡还没走视觉刷新——调用 `RefreshExtraIcons(card)` 或 `RefreshExtraIcons()`。百科这类静态界面不会自己刷新。 |
| provider 悄悄不跑了 | 它连续抛异常太多次被自动停用了——查日志里那条 error 并修掉异常。 |
| 卡牌转化后 marker 消失 | 符合预期；转化复制只携带 gameplay 附魔（见跟随表）。 |
| 明明显示了 marker，`HasAnyEnchantment` 却返回 false | 设计如此——marker 不计入 gameplay 计数；UI 计数请传 `includeExtraIcons: true`。 |

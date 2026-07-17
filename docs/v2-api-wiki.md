# MultiEnchantmentMod v2 API 使用指南

本文面向想让自己的附魔支持 MultiEnchantmentMod v2 的下游 mod 作者。

v2 的核心目标是：让一张卡可以携带多个附魔，并且让同一种附魔在重复应用时有明确、可声明的行为。

## 快速开始

在你的 mod 初始化入口里检查 API 版本，并扫描自己的程序集：

```csharp
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MultiEnchantmentMod.Api;

[ModInitializer(nameof(Initialize))]
public partial class MyModRegistration : Node
{
    public static void Initialize()
    {
        if (!MultiEnchantmentApi.RequireApiVersion(2))
        {
            return;
        }

        MultiEnchantmentApi.ScanCallingAssembly();
    }
}
```

如果你不是直接在 initializer 里调用扫描，而是通过 helper 方法转发，请改用：

```csharp
MultiEnchantmentApi.ScanAssembly(typeof(MyModRegistration).Assembly);
```

`ScanCallingAssembly()` 依赖运行时调用栈，只适合直接从你的 mod 初始化方法里调用。

## 基础概念

### StackBehavior：重复附魔时怎么处理

每个附魔类型需要声明一个堆叠行为：

| 行为 | 说明 | 适合场景 |
| --- | --- | --- |
| `DisallowDuplicate` | 已有同类型附魔时拒绝再次附魔 | 默认选择；同一张卡上不允许重复 |
| `MergeAmount` | 合并到第一个实例的 `Amount`，UI 仍可显示每次应用的切片 | 数值型叠加，例如每层 +2 格挡、每层 +5 伤害 |
| `DuplicateInstance` | 每次应用都创建独立 `EnchantmentModel` 实例 | 每个实例有独立状态，例如各自禁用/恢复 |
| `ExistenceStack` | 每次应用创建实例，但只有首次应用执行改卡副作用 | 光环/存在型效果，只关心是否至少存在一个激活实例 |

如果你不显式注册第三方附魔，默认是 `DisallowDuplicate`。v2 会对两类未注册的第三方附魔做 auto-detect，并注册为 `MergeAmount + SharedAcrossStack`：

1. 覆写 `IsStackable => true` 的类型（取该类型在 `ModelDb` 中的 canonical 实例读值）——对应原版 `CardCmd.Enchant` 的同类型 `Amount +=` 堆叠。若作者同时覆写了 `CanEnchant`，每次合并仍会先执行该覆写（原版就是每次施加都查一遍，堆叠上限通常写在这里）。
2. 覆写 `EnchantDamage*` / `EnchantBlock*` 之一的类型。

带 `[SavedProperty]` 成员的类型不参与 auto-detect（保持 `DisallowDuplicate`，Amount 语义由源 mod 自己掌控）。显式注册在两个方向上都优先：先注册则 auto-detect 直接跳过；后注册则替换掉 auto-detect 装入的默认行为。如果不希望被 auto-detect，请显式注册自己的行为。

### StatusAggregation：多实例状态怎么汇总

推荐配对：

| StackBehavior | 推荐 StatusAggregation |
| --- | --- |
| `MergeAmount` | `SharedAcrossStack` |
| `DuplicateInstance` | `PerInstanceOwned` |
| `ExistenceStack` | `AnyInstanceCountsAsOne` |
| `DisallowDuplicate` | `NotApplicable` 或任意值 |

### 从简单到进阶：功能地图

如果你第一次接入，可以按下面路线读，不必一口气啃完整篇：

| 你想做什么 | 先看哪里 | 典型 API |
| --- | --- | --- |
| 让同种附魔可以叠层 | Tier A / Tier B | `[Enchantment(Stack = StackBehavior.MergeAmount)]`、`OnMergedDelta` |
| 让同一张卡上多个实例各自有状态 | StackBehavior / StatusAggregation | `DuplicateInstance`、`PerInstanceOwned` |
| 控制旧式 hook 在多层时跑几次 | Hook 执行策略 | `[EnchantmentExecution]`、`.Execution(p => ...)` |
| 改伤害、格挡、Times 等动态变量 | 动态变量 | `[ModifyDynamicVar]`、`.ModifyDynamicVar(...)` |
| 改战斗费用或打出次数/段数 | 数值贡献通道 | `[ModifyEnergyCost]`、`[ModifyCardPlayCount]` |
| 合并选择、随机、动画、命令效果 | Stack-aware async hook | `OnPlayStacked`、`AfterAnyCardDrawnStacked` |
| 临时附魔、持续 N 回合、触发 N 次后移除 | 生命周期与临时附魔 | `WithScope`、`LingerForTurns`、`MaxActivations` |
| 条件满足时才生效或让徽章变暗 | IsActive / WhenActiveStatus | `.WhenActive(...)`、`.WhenActiveStatus(...)` |
| 监听任意卡打出或同卡附魔联动 | Vanilla Hook 桥接 / 增量钩子 | `OnAnyCardPlayed`、`OnSiblingApplied` |
| 防止无限重复叠实例 | 实例上限 | `StackDefinition.MaxInstances`、`StackOverflowPolicy` |
| 看懂层数、真实实例、视觉切片的区别 | Snapshot 只读 API | `GameplaySlices`、`VisualSlices`、`LiveInstances` |
| 让 UI tooltip 显示剩余次数等状态 | Snapshot 增强 | `snapshot.StateOf(...)`、`ScopeRuntimeStateView` |

推荐实践：简单数值叠加先用 `MergeAmount`；需要每个附魔各自记状态时再用 `DuplicateInstance`；只是“至少存在一个就生效”的光环效果用 `ExistenceStack`。

### 实例上限与溢出策略

`DuplicateInstance` 和 `ExistenceStack` 会随着重复应用创建更多真实实例。如果某个遗物、hook 或联动效果反复给同一张卡附同一种附魔，可能会意外堆出很多实例。`StackDefinition.MaxInstances` 用来给这类附魔加上限。

最保守的写法是达到上限后拒绝新实例：

```csharp
MultiEnchantmentApi.Register<ShieldCharge>()
    .Stack(new StackDefinition(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
    {
        MaxInstances = 3,
        OnOverflow = StackOverflowPolicy.Reject,
    })
    .Commit();
```

也可以把旧实例替换掉。下面例子表示“最多保留 3 个，新的来了就挤掉最旧的”：

```csharp
MultiEnchantmentApi.Register<ShieldCharge>()
    .Stack(new StackDefinition(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
    {
        MaxInstances = 3,
        OnOverflow = StackOverflowPolicy.ReplaceOldest,
    })
    .OnRemoved<ShieldCharge>((card, enchantment, reason) =>
    {
        if (reason == RemovalReason.OverflowEvicted)
        {
            // 被上限策略挤掉时的收尾逻辑。
        }

        return true;
    })
    .Commit();
```

`ReplaceNewest` 则会移除最近加入的旧实例，再挂上新实例。`MaxInstances` 只对 `DuplicateInstance` / `ExistenceStack` 有意义；`MergeAmount` 只有一个锚点实例，层数靠 `Amount` 和 slice 表示。

## 注册方式

v2 提供三种注册层级。简单附魔用 Tier A，需要自定义逻辑时用 Tier B，需要运行时 lambda/predicate 时用 Tier C。

### Tier A：只用属性

适合只需要声明堆叠行为、状态聚合、简单关键词、简单动态变量贡献的附魔。

```csharp
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class FrostShard : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
        new[] { new BlockVar(2m, ValueProp.Move) };

    public override void RecalculateValues()
    {
        DynamicVars.Block.BaseValue = Amount * 2;
    }
}
```

然后在初始化时调用 `MultiEnchantmentApi.ScanCallingAssembly()` 即可。

### Tier B：属性 + EnchantmentDefinition

适合需要处理合并副作用、生命周期、关键词计算、动态变量贡献、展示文本等逻辑的附魔。

`MergeAmount` 有一个重要规则：第一次附魔会走原本的 `OnEnchant()`，后续合并不会再次调用 `OnEnchant()`；后续每次合并会调用 `OnMergedDelta()`。

```csharp
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class CostReducer : EnchantmentModel
{
    protected override void OnEnchant()
    {
        Card.EnergyCost.UpgradeBy(-1);
    }
}

public sealed class CostReducerDefinition : EnchantmentDefinition<CostReducer>
{
    protected override void OnMergedDelta(CostReducer enchantment, int addedAmount)
    {
        for (int i = 0; i < addedAmount; i++)
        {
            enchantment.Card.EnergyCost.UpgradeBy(-1);
        }
    }
}
```

`EnchantmentDefinition<T>` 必须有无参构造函数，扫描器会自动发现它。

### Tier C：fluent builder 手动注册

适合需要运行时闭包、predicate、handler 或者不方便用 attribute 表达的场景。

```csharp
using MultiEnchantmentMod.Api;

MultiEnchantmentApi.Register<MyEnchantment>()
    .Stack(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
    .WhenActive((card, enchantment) => card.Pile != null)
    .OnApplied((card, enchantment) =>
    {
        // 应用时逻辑
    })
    .Commit();
```

常用 fluent 方法：

**堆叠与展示：**

- `.Stack(behavior, status)` — 声明堆叠和状态聚合。
- `.Stack(new StackDefinition(...))` — 声明完整堆叠规则，包含实例上限 `MaxInstances` 和溢出策略 `OnOverflow`。
- `.Execution(p => ...)` — 覆盖 hook 执行次数。
- `.OnMergedDelta(...)` / `.OnMergedRefresh(...)` — 处理 `MergeAmount` 的合并和刷新。
- `.TrackKeyword(keyword, snapshot => amount)` — 动态添加或移除关键词。
- `.ModifyDynamicVar(key, (snapshot, current) => next)` — 修改卡牌动态变量。
- `.FormatExtraText(...)` / `.VisualSlices(...)` — 控制额外文本和 UI 切片。
- `.HistoryDisplay(...)` / `.HistoryText(...)` — 控制战斗记录里怎么显示这条附魔。

**作用域与激活条件：**

- `.WithScope(scope)` — 设置任意作用域。
- `.LingerForTurns(n)` — 持续 n 回合后移除。
- `.MaxActivations(n, trigger)` — 触发 n 次后移除。
- `.WhenActive(predicate)` — 条件满足时才活跃（不控制移除）。
- `.WhenActiveStatus(predicate)` — 条件满足时才活跃，并同步 `Status.Normal` / `Status.Disabled`，适合需要让 UI 徽章变暗的附魔。
- `.RemoveWhen(predicate, triggers...)` — 指定 trigger 上 predicate 为 true 时移除。

**生命周期阶段回调：**

- `.OnApplied(...)` — 新附魔成功附到卡上后。
- `.OnRemoved(...)` — 附魔即将移除时（返回 `false` 可 veto）。
- `.OnRestored(...)` — 存档/packet 反序列化恢复后。
- `.OnCombatStart(...)` / `.OnCombatEnd(...)` — 战斗开始/结束。
- `.OnTurnStart(...)` / `.OnTurnEnd(...)` — 玩家回合开始/结束。

**Vanilla hook 桥接回调：**

- `.OnCardPlayed(...)` / `.OnCardDrawn(...)` / `.OnCardExhausted(...)` / `.OnCardDiscarded(...)` — 卡牌事件。
- `.OnCardEnteredCombat(...)` / `.OnCardChangedPiles(...)` / `.OnCardRetained(...)` — 卡牌堆位事件。
- `.OnSideTurnStart(...)` / `.OnBeforeSideTurnStart(...)` — 任一方回合开始（含敌方）。
- `.OnBeforeAttack(...)` / `.OnAfterAttack(...)` — 攻击结算前/后。
- `.OnAfterDamageReceived(...)` — 拥有方受伤后。
- `.OnBeforeBlockGained(...)` / `.OnBlockGained(...)` — 拥有方获挡前/后。
- `.OnShouldDie(...)` — 阻止死亡（返回 `false` 阻止）。
- `.OnAnyCardPlayed(...)` / `.OnAnyCardDrawn(...)` / `.OnAnyCardExhausted(...)` / `.OnAnyCardDiscarded(...)` — 广播版卡牌事件，任意卡触发时都会通知。
- `.OnSiblingApplied(...)` / `.OnSiblingRemoved(...)` — 同一张卡上的其他附魔进入或离开时通知。

所有 fluent 方法都有 `<TEnchantment>` 泛型扩展版本，避免手动 cast：

```csharp
.OnCardPlayed<MyEnchant>((card, enchantment) =>
{
    // enchantment 已经是 MyEnchant 类型
})
```

## 判断一张卡是否已有某附魔

v2 提供公开便捷方法，会检查主附魔和所有额外附魔槽：

```csharp
if (MultiEnchantmentApi.HasEnchantment<MyEnchantment>(card))
{
    return false;
}
```

也可以用非泛型版本：

```csharp
if (MultiEnchantmentApi.HasEnchantment(card, typeof(MyEnchantment)))
{
    return false;
}
```

如果你只是想让同一种附魔不能重复附到同一张卡上，优先使用：

```csharp
[Enchantment(Stack = StackBehavior.DisallowDuplicate, Status = StatusAggregation.NotApplicable)]
public sealed class MyEnchantment : EnchantmentModel
{
}
```

这样 `CanEnchant` 会在已有同类型附魔时返回 `false`。

## Snapshot 只读 API

对于调试工具、复杂 UI 或需要读取堆叠细节的 mod，可以使用高级 snapshot API。先把几个词拆开看，会容易很多：

| 名称 | 它回答的问题 | 例子 |
| --- | --- | --- |
| `TotalAmount` / `ActiveTotalAmount` | 总共有多少层 | 先附 2 层、再附 3 层，总层数是 5 |
| `LiveInstances` / `ActiveInstanceCount` | 真实存在几个 `EnchantmentModel` 对象 | `MergeAmount` 通常是 1 个；`DuplicateInstance` 每次应用都会多一个 |
| `GameplaySlices` | 游戏逻辑记住了几次应用、每次多少层 | 先附 2 层、再附 3 层，就是 `[2, 3]` |
| `VisualSlices` | UI 准备显示成几个切片/徽章 | 默认跟 `GameplaySlices` 一样，也可以用 `.VisualSlices(...)` 改 |

一个具体例子：

```text
同一附魔先应用 amount=2，后应用 amount=3

MergeAmount:
  LiveInstances      = 1        // 一个锚点实例
  ActiveTotalAmount  = 5        // 总共 5 层
  GameplaySlices     = [2, 3]   // 逻辑记住两次应用
  VisualSlices       = [2, 3]   // 默认 UI 也显示两片

DuplicateInstance:
  LiveInstances      = 2        // 两个真实实例
  ActiveTotalAmount  = 5
  GameplaySlices     = [2, 3]
  VisualSlices       = [2, 3]
```

**视觉切片不是另一套伤害/层数计算。** 它主要是展示层：比如你可以把 `[1, 1, 1, 1, 1]` 显示成一个 `5` 的徽章，或者把 `[5]` 拆成 `[2, 3]` 两个徽章。它不会改变 `TotalAmount`，也不会改变 `MergeAmount` 的合并事实。

```csharp
IReadOnlyList<EnchantmentStackSnapshot> snapshots =
    MultiEnchantmentApi.Snapshots.ForCard(card);

foreach (EnchantmentStackSnapshot snapshot in snapshots)
{
    Type type = snapshot.EnchantmentType;
    int activeInstances = snapshot.ActiveInstanceCount;
    int activeAmount = snapshot.ActiveTotalAmount;
}
```

常用字段：

- `Card`：所属卡。
- `EnchantmentType`：附魔类型。
- `AnchorInstance`：锚点实例，通常是该类型的第一个实例。
- `Definition`：该类型的堆叠定义。
- `TotalAmount`：总层数。
- `GameplaySlices`：游戏逻辑切片。
- `VisualSlices`：UI 展示切片。
- `LiveInstances`：当前真实存在的附魔实例。
- `ActiveInstanceCount`：未禁用实例数。
- `ActiveTotalAmount`：未禁用切片的总层数。

### 什么时候需要自定义 VisualSlices

大多数附魔不需要自定义 `VisualSlices`。只有当默认 UI 切片不符合你想表达的视觉语义时才需要。

`VisualSlices` 是通用的徽章切片能力，不只服务于 `ShowAmount = true` 的附魔。`ShowAmount` 只决定徽章里要不要画数字：为 `true` 时每片显示自己的数值，为 `false` 时仍然可以显示多个徽章，只是不显示数字。

例子：一个 `MergeAmount` 附魔被应用了很多次，默认会显示很多小切片。如果你只想显示一个总层数徽章：

```csharp
MultiEnchantmentApi.Register<CompactBlessing>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .VisualSlices(snapshot => new[] { snapshot.ActiveTotalAmount })
    .Commit();
```

注意：自定义 visual slices 必须满足三个条件，否则框架会回退默认值：

- 不能返回空列表。
- 每个切片数量必须大于 0。
- `ShowAmount = true` 时，所有切片数量相加必须等于 `snapshot.TotalAmount`，因为这些数字会直接显示给玩家。
- `ShowAmount = false` 时，切片数量只用来占位和计算切片数，不会显示数字，因此不要求总和等于 `snapshot.TotalAmount`。

`PerVisualSlice` 执行模式会读取 `VisualSlices` 的数量，所以如果你自定义了视觉切片，又给旧式 hook 使用 `PerVisualSlice`，hook 调用次数也会跟着 UI 切片数变化。这是高级用法；普通效果优先用 `MergedTotal`、`PerLiveInstance` 或 `FirstActiveInstanceOnly`。

### 典型附魔示例：视觉切片

假设 `ChargeUpEnchantment` 是 `MergeAmount`，每次附魔代表“下回合获得 1 能量”。同一张卡先获得 1 层、再获得 1 层：

```text
GameplaySlices = [1, 1]
VisualSlices   = [1, 1]   // 默认：UI 显示两次来源
ActiveTotalAmount = 2
```

若希望避免 UI 上出现多个细碎徽章，可以把它合成一个总层数徽章：

```csharp
MultiEnchantmentApi.Register<ChargeUpEnchantment>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .VisualSlices(snapshot => new[] { snapshot.ActiveTotalAmount })
    .Commit();
```

这样只是显示从 `[1, 1]` 变成 `[2]`，不代表“执行两次变一次”或“能量少给了”。真正的效果应该读 `snapshot.ActiveTotalAmount`，或者交给数值贡献/stacked hook 处理。

如果每个视觉徽章还需要自己的亮/暗状态或图标，用 `VisualSlicesWithStatus`（Definition 写法中覆写 `GetVisualSlices`）。例如一个附魔永远显示两个徽章，奇数回合第一个亮，偶数回合第二个亮；第一个徽章还借用另一个附魔类型的图标：

```csharp
MultiEnchantmentApi.Register<AlternatingBadges>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .VisualSlicesWithStatus(snapshot =>
    {
        bool oddRound = (snapshot.Card?.CombatState?.RoundNumber ?? 1) % 2 == 1;
        return new[]
        {
            oddRound
                ? EnchantmentVisualSlice.Active(1).WithIcon<SampleNonlinearEnchantment>()
                : EnchantmentVisualSlice.Disabled(1).WithIcon<SampleNonlinearEnchantment>(),
            oddRound
                ? EnchantmentVisualSlice.Disabled(1)
                : EnchantmentVisualSlice.Active(1),
        };
    })
    .Commit();
```

`VisualSlicesWithStatus` 的校验规则和旧的 `.VisualSlices(snapshot => int[])` 一样：不能返回空列表，每片 `Amount` 必须大于 0；如果 `ShowAmount = true`，所有 `Amount` 相加还必须等于 `snapshot.TotalAmount`。不满足时框架会回退到默认视觉切片，避免 UI 显示的总层数和堆叠快照互相矛盾。`ShowAmount = false` 时，`Amount` 只提供切片占位，不会显示数字。

每片徽章默认使用当前附魔图标。只有 `VisualSlicesWithStatus` / `GetVisualSlices` 返回的 `EnchantmentVisualSlice` 支持逐徽章图标覆盖：

- `EnchantmentVisualSlice.Active(1).WithIcon<SomeEnchantment>()`：使用同卡上该附魔类型的实例图标；如果没有实例，则尝试读取该类型的默认附魔图标；失败时安全回退当前附魔图标。
- `EnchantmentVisualSlice.Active(1).WithIcon(typeof(SomeEnchantment))`：非泛型版本。
- `EnchantmentVisualSlice.Active(1).WithIcon(texture)`：直接指定 `Texture2D`，优先级高于类型覆盖。

`.VisualSlices(snapshot => int[])` 只控制数量和切片数，始终使用当前附魔图标。

再看 `SlipperyFirstPlayEnchantment`、`GainBufferPowerEnchantment` 这种“每场战斗第一次打出才触发”的附魔。它们通常不应该用 `PerVisualSlice`，因为 UI 切成几片不是游戏设计的一部分；玩家只关心“这个效果存在，并且第一次打出时结算一次”。这类效果更适合 `FirstActiveInstanceOnly` 或下面的 `OnPlayStacked`。

## 动态变量：ModifyDynamicVar

`ModifyDynamicVar` 用来修改卡牌动态变量，例如 `damage`、`block`、`Times` 或第三方自定义 key。

### Attribute 写法

```csharp
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class PlusFiveDamage : EnchantmentModel
{
    [ModifyDynamicVar("damage")]
    public decimal AddFive(EnchantmentStackSnapshot snapshot, decimal current)
    {
        return current + 5m;
    }
}
```

方法签名必须是：

```csharp
decimal MethodName(EnchantmentStackSnapshot snapshot, decimal currentValue)
```

### Definition 写法

```csharp
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class Doubler : EnchantmentModel
{
}

public sealed class DoublerDefinition : EnchantmentDefinition<Doubler>
{
    [ModifyDynamicVar("damage")]
    public decimal Double(EnchantmentStackSnapshot snapshot, decimal current)
    {
        return current * 2m;
    }
}
```

### Fluent 写法

```csharp
MultiEnchantmentApi.Register<PlusFiveDamage>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .ModifyDynamicVar("damage", (snapshot, current) => current + 5m)
    .Commit();
```

注意事项：

- key 大小写不敏感，`"damage"` 和 `"Damage"` 都可以。
- 多个附魔修改同一个 key 时，按“卡牌应用顺序 × 同一附魔内注册顺序”组合。
- `MergeAmount` 下按 active gameplay slice 调用。通常写“单层效果”即可，例如 `current + 5m`，不要再手动乘总层数。
- 不要在同一个附魔里同时使用 `ModifyDynamicVar("damage", ...)` 和 `EnchantDamageAdditive` / `EnchantDamageMultiplicative`；两条通道会叠加，造成重复计算。

### 调用次数怎么理解

`ModifyDynamicVar` 不走上面的 `HookExecutionMode`。它有自己的组合规则：

| StackBehavior | 调用方式 | 例子 |
| --- | --- | --- |
| `MergeAmount` | 每个 active gameplay slice 调一次 | 3 次 `+5` 会依次变成 `+15` |
| `DuplicateInstance` | 每种附魔类型调一次 | 想按实例数放大时，用 `snapshot.ActiveInstanceCount` |
| `ExistenceStack` | 每种附魔类型调一次 | 只要存在就贡献一次 |
| `DisallowDuplicate` | 最多一次 | 普通单实例附魔 |

入门写法通常是“只写一层效果”：

```csharp
// 三层时：10 -> 15 -> 20 -> 25
.ModifyDynamicVar("damage", (snapshot, current) => current + 5m)
```

如果你希望 `DuplicateInstance` 每个实例都加 2，因为它默认只调用一次，需要自己读实例数：

```csharp
MultiEnchantmentApi.Register<PerInstanceDamage>()
    .Stack(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
    .ModifyDynamicVar("damage", (snapshot, current) =>
        current + snapshot.ActiveInstanceCount * 2m)
    .Commit();
```

组合顺序是有意义的：`+5` 再 `x2` 与 `x2` 再 `+5` 结果不同。框架按附魔在卡上的应用顺序、以及同一个附魔内部的注册顺序依次折叠。

## 战斗费用和打出次数贡献

有些效果不是 `damage` / `block` 这样的动态变量，也不适合放进 `OnPlay()` 里反复执行。典型场景包括：

注意：

- 不要在同一个附魔里同时使用 `ModifyEnergyCostInCombat` / `ModifyCardPlayCount` 和旧的 `Hook.ModifyEnergyCostInCombat` / `Hook.ModifyCardPlayCount` 覆写；两条通道会叠加，容易重复计算。
- 这两个新通道都是折叠式贡献，不是“替换整个结果”。写法上优先返回“当前值的下一步”，而不是自己从零重算一遍。

| 附魔 | 设计语义 | 推荐通道 |
| --- | --- | --- |
| `ShieldPlatingEnchantment` / `SwordArtEnchantment` | 耗能 +1 | `[ModifyEnergyCost]` 或 `.ModifyEnergyCostInCombat(...)` |
| `EagerPerAttackEnergyEnchantment` | 耗能 +1，打出前按手牌攻击数回能 | 费用用 `[ModifyEnergyCost]`；回能用 `BeforeCardPlayedStacked` |
| `ExtraHitEnchantment` | 力量攻击的伤害段数 +1 | `[ModifyCardPlayCount]` 或 `.ModifyCardPlayCount(...)` |

### 修改战斗费用

`RecalculateValues()` 适合修改卡牌的基础展示值，但战斗中还有腐化、遗物、能力等其他来源会一起修改费用。想让多附魔稳定组合，优先使用费用贡献：

```csharp
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod;
using MultiEnchantmentMod.Api;

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class ShieldPlatingEnchantment : EnchantmentModel
{
    [ModifyEnergyCost]
    public decimal IncreaseCost(EnchantmentStackSnapshot snapshot, decimal currentCost)
    {
        return currentCost + snapshot.ActiveTotalAmount;
    }
}
```

如果 `盾化` 叠了 2 层，费用贡献会把当前战斗费用 `+2`。它收到的是“到目前为止的运行中费用”，所以能和其他费用修改按顺序折叠。

### 修改打出次数/段数

`ExtraHitEnchantment` 这类“多打一段”不要靠在 `OnPlay()` 里手动重放整张牌；那会把抽牌、选择、消耗、随机目标等副作用也一起重放。更安全的是只修改打出次数：

```csharp
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class ExtraHitEnchantment : EnchantmentModel
{
    [ModifyCardPlayCount]
    public int AddHits(EnchantmentStackSnapshot snapshot, int currentPlayCount)
    {
        return currentPlayCount + snapshot.ActiveTotalAmount;
    }
}
```

签名必须分别是：

```csharp
decimal Method(EnchantmentStackSnapshot snapshot, decimal currentCost)
int Method(EnchantmentStackSnapshot snapshot, int currentPlayCount)
```

写错时 analyzer 会报 `MEM013`。

## 关键词贡献

可以用 `[EnchantmentKeyword]` 声明附魔在激活时贡献的关键词：

```csharp
using MegaCrit.Sts2.Core.Entities.Cards;
using MultiEnchantmentMod.Api;

[Enchantment(Stack = StackBehavior.ExistenceStack, Status = StatusAggregation.AnyInstanceCountsAsOne)]
[EnchantmentKeyword(CardKeyword.Exhaust)]
public sealed class ExhaustAura : EnchantmentModel
{
}
```

如果需要按层数或实例数计算，可以设置 `KeywordEvalMode`，或在 `EnchantmentDefinition<T>` 中覆写 `TrackedKeywords` / `KeywordSourceAmount`。

fluent 写法：

```csharp
MultiEnchantmentApi.Register<MyEnchantment>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .TrackKeyword(CardKeyword.Exhaust, snapshot => snapshot.ActiveTotalAmount)
    .Commit();
```

贡献值大于 0 时关键词存在，0 或负数时关键词不存在。

## 生命周期和临时附魔

v2 支持声明附魔作用域，控制附魔"存在多久"和"什么时候活跃"。

### 作用域一览

| 作用域 | 含义 | Attribute 用法 | Fluent 用法 |
| --- | --- | --- | --- |
| `Permanent` | 永久存在 | `Scope = ScopeKind.Permanent`（默认） | `.WithScope(EnchantmentScope.Permanent)` |
| `UntilCombatEnds` | 战斗结束移除 | `Scope = ScopeKind.UntilCombatEnds` | `.WithScope(EnchantmentScope.UntilCombatEnds)` |
| `UntilTurnEnds` | 回合结束移除 | `Scope = ScopeKind.UntilTurnEnds` | `.WithScope(EnchantmentScope.UntilTurnEnds)` |
| `LingerForTurns(n)` | 持续 n 回合后移除 | `Scope = ScopeKind.LingerForTurns, LingerTurns = 2` | `.LingerForTurns(2)` |
| `MaxActivations(n, trigger)` | 触发 n 次后移除 | `Scope = ScopeKind.MaxActivations, MaxActivations = 3` | `.MaxActivations(3, ActivationTrigger.OnPlay)` |
| `ConditionalActive(predicate)` | 满足条件才活跃，不移除 | 不支持（需 fluent） | `.WhenActive((card, e) => ...)` |
| `RemoveWhen(predicate, triggers)` | 条件满足时移除 | 不支持（需 fluent） | `.RemoveWhen((card, e) => ..., triggers)` |

Attribute 写法（简单场景）：

```csharp
[Enchantment(
    Stack = StackBehavior.DisallowDuplicate,
    Status = StatusAggregation.NotApplicable,
    Scope = ScopeKind.UntilCombatEnds)]
public sealed class CombatOnlyEnchant : EnchantmentModel
{
}
```

Fluent 写法（需要 predicate 或扩展 trigger 时）：

```csharp
MultiEnchantmentApi.Register<MyEnchantment>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .LingerForTurns(2)
    .OnRemoved((card, enchantment, reason) => true)
    .Commit();
```

`RemoveWhen` 示例 — HP 低于 50% 时移除：

```csharp
MultiEnchantmentApi.Register<Overconfidence>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .RemoveWhen(
        (card, enchantment) => card.Owner?.Hp < card.Owner?.MaxHp / 2,
        ActivationTrigger.AfterDamageReceived)
    .Commit();
```

### ActivationTrigger

`ActivationTrigger` 是可扩展的 sealed record（非闭合枚举），用于 `MaxActivations` 和 `RemoveWhen` 的触发计数。

内置触发器：

| Trigger | 含义 | 作用域 |
| --- | --- | --- |
| `OnPlay` | 附魔所在卡打出时 | 仅所在卡 |
| `AfterCardPlayed` | 卡牌打出后 | 仅所在卡 |
| `AfterCardDrawn` | 卡牌被抽到后 | 仅所在卡 |
| `AfterCardExhausted` | 卡牌被消耗后 | 仅所在卡 |
| `AfterCardDiscarded` | 卡牌被弃掉后 | 仅所在卡 |
| `AfterPlayerTurnStart` | 玩家回合开始后 | 所有卡 |
| `AfterPlayerTurnEnd` | 玩家回合结束后 | 所有卡 |
| `AfterDamageReceived` | 拥有方受伤后 | 拥有方所有卡 |

自定义触发器：

```csharp
// 定义
var myTrigger = ActivationTrigger.Custom("mymod:OnRelicTriggered");

// 注册
.MaxActivations(3, myTrigger)

// 在你的 patch 里手动触发计数
MultiEnchantmentScopeSupport.NoteActivation(enchantment, myTrigger);
```

Custom trigger 会被缓存 — 同一个 `identifier` 每次返回同一个实例，可安全用于紧凑循环。

### 生命周期回调

附魔在生命周期的不同阶段可以注册回调。可通过 `EnchantmentDefinition<T>` 覆写，也可用 fluent builder：

| 回调 | 触发时机 | 返回值 |
| --- | --- | --- |
| `OnApplied` | 新附魔成功附到卡上后 | void |
| `OnRemoved` | 即将被移除时 | `bool`（`false` = veto） |
| `OnRestored` | 存档/packet 反序列化恢复后 | void |
| `OnCombatStart` / `OnCombatEnd` | 战斗开始/结束 | void |
| `OnTurnStart` / `OnTurnEnd` | 玩家回合开始/结束 | void |

注意：`OnApplied` 不在存档恢复时触发。如需在读档后重建运行时缓存，改用 `OnRestored`。

`OnRemoved` 返回 `false` 可以阻止本次移除（除 `CardCleared` 外）。

更多详情见 `docs/v2-lifecycle-wiki.md`。

### IsActive 守门

所有生命周期回调和 vanilla hook 桥接都受 `IsActive` 守门保护。当附魔因 scope / `WhenActive` 条件处于失活状态时：

- 不参与伤害/格挡/动态变量计算
- 不参与 listener 路径（`Hook.AfterCardPlayed` 等）
- 不显示 hover tooltip
- 不参与 preview 数字计算
- 不接收任何 vanilla hook 桥接回调

这意味着 `.WhenActive((card, e) => card.Type == CardType.Attack)` 会完整地抑制附魔的所有可见效果 — 作者无需在每个逻辑分支里手动检查。

### WhenActive 与 WhenActiveStatus

这两个 API 都是“条件满足才生效”，但它们对 UI 状态的处理不同：

| API | 控制 gameplay 是否生效 | 是否同步 `EnchantmentStatus.Disabled` | 适合场景 |
| --- | --- | --- | --- |
| `WhenActive(predicate)` | 是 | 否 | 只想让逻辑休眠，不想改变徽章状态 |
| `WhenActiveStatus(predicate)` / `ShouldBeActive(...)` | 是 | 是 | 条件不满足时希望附魔徽章变暗、tooltip/切片状态也跟着变 |

fluent 写法：

```csharp
MultiEnchantmentApi.Register<HandOnlySharpen>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .WhenActiveStatus((card, enchantment) => card.Pile?.Type == PileType.Hand)
    .Commit();
```

Definition 写法：

```csharp
public sealed class HandOnlySharpenDefinition : EnchantmentDefinition<HandOnlySharpen>
{
    protected override bool ShouldBeActive(CardModel card, HandOnlySharpen enchantment)
    {
        return card.Pile?.Type == PileType.Hand;
    }
}
```

`WhenActiveStatus` 不会移除附魔；它只是在条件为 false 时把状态同步成 `Disabled`。如果你要的是“条件满足后消失”，继续使用 `RemoveWhen`。

## 战斗记录自定义显示

v2 允许附魔作者控制"战斗结束后，这条附魔的应用记录出现在哪里（或是否显示）"。默认（`Auto` 模式）根据作用域自动判断：永久性附魔记录到奖励区，临时性附魔隐藏不显示。

### HistoryDisplayMode 枚举

| 值 | 含义 |
| --- | --- |
| `Auto` | 默认。`Permanent` / `ConditionalActive` / `RemoveWhen` → 显示在奖励区；`UntilCombatEnds` / `UntilTurnEnds` / `LingerForTurns` / `MaxActivations` → 隐藏 |
| `InRewards` | 强制显示在奖励区，无论作用域 |
| `Hidden` | 强制隐藏，不在任何地方显示 |
| `InActions` | 显示在行动区（与"打出了 X 牌"并列），而非奖励区 |
| `CustomGroup` | 以自定义分组标题显示，附加在行动区末尾 |

### Attribute 写法（Tier A）

```csharp
// 强制隐藏（内部标记类附魔，不希望玩家看到记录）
[Enchantment(
    Stack = StackBehavior.DisallowDuplicate,
    Status = StatusAggregation.NotApplicable,
    HistoryDisplay = HistoryDisplayMode.Hidden)]
public sealed class InternalMarker : EnchantmentModel { }

// 临时战斗增强，但仍显示在行动区
[Enchantment(
    Stack = StackBehavior.MergeAmount,
    Status = StatusAggregation.SharedAcrossStack,
    Scope = ScopeKind.UntilCombatEnds,
    HistoryDisplay = HistoryDisplayMode.InActions)]
public sealed class CombatBoost : EnchantmentModel { }

// 永久附魔，以自定义分组显示
[Enchantment(
    Stack = StackBehavior.MergeAmount,
    Status = StatusAggregation.SharedAcrossStack,
    HistoryDisplay = HistoryDisplayMode.CustomGroup,
    HistoryGroupHeader = "战斗强化")]
public sealed class BattleEnhancement : EnchantmentModel { }
```

### Definition 写法（Tier B）

覆写 `HistoryDisplay`、`HistoryGroupHeader`（`CustomGroup` 时）和 `FormatHistoryText`（自定义单条记录文本时）：

```csharp
public sealed class SpecialEnchantDefinition : EnchantmentDefinition<SpecialEnchant>
{
    public override HistoryDisplayMode HistoryDisplay => HistoryDisplayMode.CustomGroup;
    public override string? HistoryGroupHeader => "战斗强化";

    // 可选：自定义单条记录的文本格式。不覆写时使用 vanilla 默认文本。
    // 返回 null 表示回退到默认格式。
    protected override string? FormatHistoryText(string cardTitle, string enchantmentTitle)
    {
        return $"[{enchantmentTitle}] 强化了 {cardTitle}";
    }
}
```

### Fluent 写法（Tier C）

```csharp
// 仅指定模式
MultiEnchantmentApi.Register<CombatBoost>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .HistoryDisplay(HistoryDisplayMode.InActions)
    .Commit();

// 自定义分组（携带分组标题，隐式 CustomGroup 模式）
MultiEnchantmentApi.Register<BattleEnhancement>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .HistoryDisplay(HistoryDisplayMode.CustomGroup, "战斗强化")
    .Commit();

// 自定义每条记录的文本（可与 InRewards / InActions / CustomGroup 组合）
MultiEnchantmentApi.Register<SpecialEnchant>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .HistoryDisplay(HistoryDisplayMode.InRewards)
    .HistoryText((cardTitle, enchantmentTitle) => $"强化了 {cardTitle}: {enchantmentTitle}")
    .Commit();
```

### Auto 模式判断逻辑

`Auto` 模式按注册的作用域类型判断，不依赖运行时状态：

| 作用域 | Auto 下的显示位置 |
| --- | --- |
| `Permanent`（默认） | 奖励区 |
| `ConditionalActive` / `WhenActive` | 奖励区（附魔本身永久存在，只控制活跃状态） |
| `RemoveWhen` | 奖励区（初始是长期附魔，条件满足才消失） |
| `UntilCombatEnds` | 隐藏 |
| `UntilTurnEnds` | 隐藏 |
| `LingerForTurns(n)` | 隐藏 |
| `MaxActivations(n, trigger)` | 隐藏 |

临时性作用域的附魔在战斗结束时已经消失，`Auto` 默认隐藏它们的历史记录。如果临时附魔仍需显示（例如"本场战斗临时强化了哪些牌"），显式指定 `HistoryDisplay = HistoryDisplayMode.InActions`。

### 显示区域说明

- **奖励区（InRewards）**：战斗结束后的奖励界面中，与"获得了遗物 X"并列的附魔记录。格式由 vanilla `_enchanted` LocString 驱动；提供 `HistoryText` / `FormatHistoryText` 后使用自定义文本。
- **行动区（InActions）**：战斗记录界面的行动文字列表（"打出了 X""消耗了 Y"所在区域），附加在末尾。
- **自定义分组（CustomGroup）**：以 `HistoryGroupHeader` 字符串作为分隔符（原样输出，不自动加 BBCode），属于同一分组的记录归组显示在行动区末尾。`HistoryGroupHeader` 不能为 `null`（未设置时回退到 `"Enchantments"`）。

## Hook 执行策略

当同一种附魔在一张卡上有多层或多个实例时，有两件事很容易混在一起：

1. **堆叠行为**决定“第二次附魔时怎么存”：合并 `Amount`、创建新实例，还是拒绝。
2. **执行策略**决定“旧式 `EnchantmentModel` hook 要跑几次”：例如 `OnPlay()`、`AfterCardDrawn()`、`BeforeFlush()`。

可以把执行策略理解成一个很朴素的问题：

```text
这张卡上“看起来/逻辑上有好几份同类附魔”时，
旧式 hook 是只跑一次，还是按层数跑多次，还是按真实实例跑多次？
```

如果你只使用 v2 的 lifecycle 回调（例如 `OnCardPlayed`、`OnTurnStart`、`OnAnyCardPlayed`），通常不需要配置执行策略。执行策略主要服务于 vanilla / 旧式附魔方法，尤其是你覆写了 `EnchantmentModel.OnPlay()`、`AfterCardDrawn()` 这类方法时。

`HookExecutionMode` 类型在旧命名空间 `MultiEnchantmentMod` 里；需要同时引用两个 namespace：

```csharp
using MultiEnchantmentMod;
using MultiEnchantmentMod.Api;
```

### 一句话选择表

先不用记枚举名，先按效果语义选：

| 你想表达的语义 | 选这个 |
| --- | --- |
| 默认就好，不想特殊处理 | `Default` |
| 每一层都独立触发一次 | `MergedTotal` |
| 每个真实实例都独立触发一次 | `PerLiveInstance` |
| 只要有这个附魔就触发一次 | `FirstActiveInstanceOnly` |
| UI 显示几个切片就触发几次 | `PerVisualSlice` |

### 五种执行模式

| 模式 | 它数的是什么 | 什么时候用 |
| --- | --- | --- |
| `Default` | 不自己数，交给 `StackBehavior` 默认值 | 大多数情况 |
| `MergedTotal` | `snapshot.ActiveTotalAmount` | “3 层 = 跑 3 次” |
| `PerLiveInstance` | `snapshot.ActiveInstanceCount` | `DuplicateInstance`，或 `MergeAmount` 里 hook 已经按 `Amount` 算总量 |
| `FirstActiveInstanceOnly` | 是否至少有 1 个 active 实例 | 光环、存在型效果、防止重复副作用 |
| `PerVisualSlice` | `snapshot.ActiveVisualSliceCount` | UI 切片本身代表触发单位的高级用法 |

默认策略来自 `StackBehavior`，你可以先把它们当作“框架猜你最可能想要的次数”：

| StackBehavior | 默认执行策略 | 适合语义 |
| --- | --- | --- |
| `MergeAmount` | `MergedTotal` | 每层都要触发一次的旧式 hook |
| `DuplicateInstance` | `PerLiveInstance` | 每个实例各自触发 |
| `ExistenceStack` | `FirstActiveInstanceOnly` | 只要存在就触发一次 |
| `DisallowDuplicate` | `FirstActiveInstanceOnly` | 反正最多一个 |

`MergedTotal` 是 `MergeAmount` 的全局默认值。少数内置附魔会单独覆盖 `OnPlay` 为 `PerLiveInstance`，因为它们的 `OnPlay()` 本身已经按 `Amount` 放大；如果再按总层数重复调用，就会变成平方级效果。你写自己的附魔时也按这个原则判断。

### 三个数值例子

假设一张卡上同一种附魔最终是 5 层：

```text
情况 A：MergeAmount，先应用 2 层、再应用 3 层
  ActiveTotalAmount       = 5
  ActiveInstanceCount     = 1
  ActiveVisualSliceCount  = 2   // 默认显示 [2, 3] 两片

情况 B：DuplicateInstance，先应用 2 层、再应用 3 层
  ActiveTotalAmount       = 5
  ActiveInstanceCount     = 2
  ActiveVisualSliceCount  = 2

情况 C：ExistenceStack，应用很多次，但只想表达“存在”
  ActiveTotalAmount       = 5   // 数值仍可读
  ActiveInstanceCount     = 2   // 真实实例仍存在
  FirstActiveInstanceOnly = 1   // 旧式 hook 只跑一次
```

所以同样是“5 层”，不同执行模式会得到不同调用次数：

| 执行模式 | 情况 A | 情况 B | 情况 C 常用 |
| --- | ---: | ---: | ---: |
| `MergedTotal` | 5 | 5 | 5 |
| `PerLiveInstance` | 1 | 2 | 2 |
| `PerVisualSlice` | 2 | 2 | 2 |
| `FirstActiveInstanceOnly` | 1 | 1 | 1 |

### 判断该选哪种模式

先问自己一句话：我的 hook 代码内部有没有自己读取 `Amount` 或 `snapshot.ActiveTotalAmount`？

| 你的代码形态 | 推荐 |
| --- | --- |
| “每跑一次只做一层效果”，不读 `Amount` | `MergedTotal` |
| “一次性按 `Amount` 结算总效果” | `PerLiveInstance` 或 `FirstActiveInstanceOnly` |
| 每个实例有独立冷却、独立状态 | `PerLiveInstance` |
| 只要有这个附魔就给一次光环效果 | `FirstActiveInstanceOnly` |
| UI 切片代表独立触发单位 | `PerVisualSlice` |

### 例子 1：每层都触发一次

假设你希望一张卡有 3 层 `SparkOnPlay` 时，打出后触发 3 次小效果。`OnPlay()` 里不读 `Amount`，那么默认 `MergedTotal` 正好符合预期。

```csharp
using MultiEnchantmentMod.Api;

[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class SparkOnPlay : EnchantmentModel
{
    public override bool ShowAmount => true;

    public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 这里替换成你的“一层”效果；3 层时框架会调用 3 次。
        await DealOneSparkDamage(choiceContext, cardPlay);
    }
}
```

### 例子 2：一次性按总层数结算

如果你的 `OnPlay()` 已经用 `Amount` 计算总量，就应该让它只跑一次。否则 3 层会变成“跑 3 次，每次又按 3 层算”。

```csharp
using MultiEnchantmentMod;
using MultiEnchantmentMod.Api;

[EnchantmentExecution(OnPlay = HookExecutionMode.PerLiveInstance)]
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class ScaledOnPlay : EnchantmentModel
{
    public override bool ShowAmount => true;

    public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 这里替换成你的总量效果；3 层时只调用 1 次，效果量是 3。
        await DealDamage(choiceContext, cardPlay, Amount);
    }
}
```

同样的配置也可以用 fluent 写：

```csharp
using MultiEnchantmentMod;
using MultiEnchantmentMod.Api;

MultiEnchantmentApi.Register<ScaledOnPlay>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .Execution(p => p.OnPlay(HookExecutionMode.PerLiveInstance))
    .Commit();
```

### 例子 3：同一附魔不同 hook 使用不同策略

有时一个附魔打出时按总量结算，但抽到时每层随机一次。这时只覆盖需要变化的 hook：

```csharp
using MultiEnchantmentMod;
using MultiEnchantmentMod.Api;

[EnchantmentExecution(
    OnPlay = HookExecutionMode.PerLiveInstance,
    AfterCardDrawn = HookExecutionMode.MergedTotal)]
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class DrawAndPlayEnchant : EnchantmentModel
{
    public override bool ShowAmount => true;
}
```

`All = HookExecutionMode.X` 可以给所有未单独指定的 hook 设置默认策略：

```csharp
MultiEnchantmentApi.Register<MyEnchant>()
    .Stack(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
    .Execution(p => p
        .All(HookExecutionMode.PerLiveInstance)
        .OnPlay(HookExecutionMode.FirstActiveInstanceOnly))
    .Commit();
```

### 哪些 hook 能配置

目前执行策略覆盖这些旧式 hook：

| 策略字段 | 对应语义 |
| --- | --- |
| `OnEnchant` | 附魔应用时的旧式附魔逻辑 |
| `OnPlay` | 卡打出时 `EnchantmentModel.OnPlay()` |
| `AfterCardPlayed` | 卡打出后 |
| `AfterCardDrawn` | 卡抽到后 |
| `AfterPlayerTurnStart` | 玩家回合开始后 |
| `BeforePlayPhaseStart` | 出牌阶段开始前 |
| `BeforeFlush` | 手牌 flush 前 |

如果你正在写新代码，优先使用下面的 v2 lifecycle 回调：`OnCardPlayed`、`OnCardDrawn`、`OnTurnStart`、`OnAnyCardPlayed` 等。它们已经走 `IsActive` 守门、异常保护和快照遍历，更不容易和堆叠次数搅在一起。

### 常见坑

- `MergeAmount + MergedTotal` 下，hook 内不要再手动循环 `Amount`，除非你真的想要平方级效果。
- `DuplicateInstance + MergedTotal` 通常是错的：多个实例的总 `Amount` 会把独立实例语义弄乱，可按 `MEM005` 的思路自查。
- `ExistenceStack + PerLiveInstance` 通常也是错的：存在型附魔一般只要“至少一个”生效，可按 `MEM005` 的思路自查。
- `PerVisualSlice` 会受 `.VisualSlices(...)` 影响；除非你明确把“UI 上的一片”设计成一个触发单位，否则不要优先选它。
- `Default` 不是“跑一次”，而是“回到堆叠行为默认值”。
- `ModifyDynamicVar` 不使用这里的 `HookExecutionMode`；它有自己的组合规则，见“动态变量”一节。

## Stack-aware async hook：合并后执行一次

执行策略解决的是旧式 `EnchantmentModel` hook “跑几次”。但有些附魔不能简单地跑 N 次：

| 典型附魔 | 如果直接跑 N 次的问题 | 推荐写法 |
| --- | --- | --- |
| `SurvivorDiscardEnchantment` / `SteadfastExhaustEnchantment` | 叠 3 层会弹 3 次选牌，体验很差 | `OnPlayStacked` 里一次选择，按层数决定数量 |
| `MeltDoubleVulnerableEnchantment` | 叠 2 层若顺序翻倍，会从 2 层易伤变 8，不一定是设计意图 | `OnPlayStacked` 里明确按总层数计算 |
| `SwordArtEnchantment` | 每层随机目标一次，还是一次随机后多段伤害，要由作者声明 | `OnPlayStacked` 里自己控制随机次数 |
| `SlipperyFirstPlayEnchantment` / `GainBufferPowerEnchantment` | 每个实例各有 `_pendingFirstPlayInCombat`，合并后语义不清 | `BeforeCombatStart` 初始化状态，`OnPlayStacked` 只结算一次 |
| `CorrosiveWaveEnchantment` / `ForgeWaveEnchantment` | 监听的是“打出此牌后，本回合每当抽到另一张牌”，不是“此牌被抽到” | `AfterCardPlayedStacked` 打开监听，`AfterAnyCardDrawnStacked` 响应任意抽牌 |
| `ReaperDoomOnDamageEnchantment` / `FeedEnchantment` | 需要读取真实伤害/击杀结果，不能提前算 | `AfterDamageGivenStacked` |

stacked hook 的共同规则：

- 每种附魔类型在一张卡上只调用一次。
- 通过 `context.Snapshot.ActiveTotalAmount`、`ActiveInstanceCount`、`LiveInstances` 自己决定效果量。
- 只分发给 active 附魔。
- 适合 `await` 命令、选牌、动画、随机目标、读 `DamageResult` 这类不能用纯数值公式表达的行为。

### 例子：`SurvivorDiscardEnchantment`

旧式写法如果设成 `MergedTotal`，3 层会调用 3 次 `CardSelectCmd.FromHandForDiscard`。更自然的设计通常是“一次弹窗，选择最多层数张牌”：

```csharp
public sealed class SurvivorDiscardDefinition
    : EnchantmentDefinition<SurvivorDiscardEnchantment>
{
    protected override async Task OnPlayStacked(StackedOnPlayContext context)
    {
        var card = context.Snapshot.Card;
        var player = card?.Owner;
        if (player?.PlayerCombatState == null)
        {
            return;
        }

        int count = Math.Min(
            context.Snapshot.ActiveTotalAmount,
            player.PlayerCombatState.Hand.Cards.Count(c => c != card));
        if (count <= 0)
        {
            return;
        }

        var prefs = new CardSelectorPrefs(
            new LocString("enchantments", "SURVIVOR_DISCARD_ENCHANTMENT.selectPrompt"),
            count);
        var picked = await CardSelectCmd.FromHandForDiscard(
            context.ChoiceContext,
            player,
            prefs,
            c => c != card,
            context.Snapshot.AnchorInstance);

        foreach (var toDiscard in picked)
        {
            await CardCmd.Discard(context.ChoiceContext, toDiscard);
        }
    }
}
```

这里“叠层”影响选择张数，而不是弹窗次数。这个区别就是 stacked hook 要解决的问题。

### 例子：抽牌波类附魔

`CorrosiveWaveEnchantment`、`CalamityWaveDoomEnchantment`、`ForgeWaveEnchantment` 的语义是：

1. 打出宿主牌后，开启本回合监听。
2. 本回合你每抽到另一张牌，触发一次效果。
3. 回合 flush 时做收尾，关闭监听。

这类效果需要响应“任意牌被抽到”，所以使用 `AfterAnyCardDrawnStacked`，不是 `AfterCardDrawnStacked`。

```csharp
public sealed class CorrosiveWaveEnchantment : EnchantmentModel
{
    internal bool ListenDrawsThisTurn { get; set; }
}

public sealed class CorrosiveWaveDefinition
    : EnchantmentDefinition<CorrosiveWaveEnchantment>
{
    protected override Task AfterCardPlayedStacked(StackedAfterCardPlayedContext context)
    {
        var enchantment = (CorrosiveWaveEnchantment)context.Snapshot.AnchorInstance;
        if (context.CardPlay.Card == context.Snapshot.Card)
        {
            enchantment.ListenDrawsThisTurn = true;
        }

        return Task.CompletedTask;
    }

    protected override Task BeforeFlushStacked(StackedBeforeFlushContext context)
    {
        var enchantment = (CorrosiveWaveEnchantment)context.Snapshot.AnchorInstance;
        enchantment.ListenDrawsThisTurn = false;
        return Task.CompletedTask;
    }

    protected override async Task AfterAnyCardDrawnStacked(StackedAfterCardDrawnContext context)
    {
        var enchantment = (CorrosiveWaveEnchantment)context.Snapshot.AnchorInstance;
        if (!enchantment.ListenDrawsThisTurn || context.DrawnCard == context.Snapshot.Card)
        {
            return;
        }

        var card = context.Snapshot.Card;
        var applier = card?.Owner?.Creature;
        if (card?.CombatState == null || applier == null)
        {
            return;
        }

        decimal poison = 2m * context.Snapshot.ActiveTotalAmount;
        foreach (var enemy in card.CombatState.HittableEnemies)
        {
            await PowerCmdCompat.Apply<PoisonPower>(
                enemy,
                poison,
                applier,
                card,
                context.ChoiceContext);
        }
    }
}
```

这个例子把监听状态放在 `CorrosiveWaveEnchantment` 实例上，而不是放在 definition 的私有字段里。definition 通常是注册对象，可能服务很多张卡；实例字段才表示“这张卡上的这个附魔当前是否正在监听”。

`BeforeFlushStacked` 更适合做状态收尾，不适合弹 UI 或执行依赖选择上下文的命令，因为当前桥接里的 `ChoiceContext` 为空。

如果这个监听状态需要跨存档/联机同步，不要只放在普通字段里；把它写进 `enchantment.Props.strings`，并在写入后调用 `MultiEnchantmentApi.NotifyPropsChanged(enchantment)`，或用框架提供的 scope 状态表达。

### 例子：伤害后附加效果

`ReaperDoomOnDamageEnchantment` 要读取 `DamageResult.TotalDamage`，`FeedEnchantment` 要读取 `WasTargetKilled`。这类逻辑放在 `AfterDamageGivenStacked`：

```csharp
protected override async Task AfterDamageGivenStacked(StackedAfterDamageGivenContext context)
{
    if (context.CardSource != context.Snapshot.Card)
    {
        return;
    }

    if (context.Result.TotalDamage <= 0)
    {
        return;
    }

    decimal doom = context.Result.TotalDamage
        * context.Snapshot.ActiveTotalAmount
        / 2m;
    await PowerCmdCompat.Apply<DoomPower>(
        context.Target,
        doom,
        context.Dealer,
        context.CardSource,
        context.ChoiceContext);
}
```

## 选哪条通道：快速决策

| 需求 | 首选 |
| --- | --- |
| 改牌面或结算中的伤害/格挡/Times | `[ModifyDynamicVar]` |
| 改战斗费用 | `[ModifyEnergyCost]` |
| 改打出次数/段数 | `[ModifyCardPlayCount]` |
| 添加关键词，比如消耗、虚无、奇巧 | `[EnchantmentKeyword]` 或 `.TrackKeyword(...)` |
| 打出时抽牌、生成刀、生成碎屑、获得格挡/能量 | `OnPlayStacked` |
| 打出前按当前手牌计算，比如 `EagerPerAttackEnergyEnchantment` | `BeforeCardPlayedStacked` |
| 打出后开启一个本回合监听 | `AfterCardPlayedStacked`；收尾关闭放在 `BeforeFlushStacked` |
| 监听任意抽牌 | `AfterAnyCardDrawnStacked` |
| 根据实际伤害/击杀结算 | `AfterDamageGivenStacked` |
| 只做同步状态更新，不需要 await | lifecycle 回调，比如 `OnCardPlayed` / `OnTurnStart` |
| 兼容旧附魔覆写，暂时不改代码 | `HookExecutionMode` |

## Vanilla Hook 桥接回调

v2 提供 15 个 vanilla hook 的生命周期桥接，让附魔直接在 lifecycle 框架内响应游戏事件，无需重写 `EnchantmentModel` 虚方法。**所有桥接回调自动受 IsActive 守门保护** — 失活附魔不会收到事件。

### 卡牌事件（仅分发给附魔所在卡）

```csharp
public sealed class MyDefinition : EnchantmentDefinition<MyEnchant>
{
    protected override void OnCardPlayed(CardModel card, MyEnchant enchantment)
    {
        // 卡被打出后
    }

    protected override void OnCardDrawn(CardModel card, MyEnchant enchantment)
    {
        // 卡被抽到后
    }

    protected override void OnCardExhausted(CardModel card, MyEnchant enchantment) { }
    protected override void OnCardDiscarded(CardModel card, MyEnchant enchantment) { }
    protected override void OnCardEnteredCombat(CardModel card, MyEnchant enchantment) { }
    protected override void OnCardChangedPiles(
        CardModel card, MyEnchant enchantment, PileType oldPile, AbstractModel? source) { }
    protected override void OnCardRetained(CardModel card, MyEnchant enchantment) { }
}
```

### 战斗流程（分发给所有玩家的所有卡）

```csharp
// fluent 写法
MultiEnchantmentApi.Register<MyEnchant>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .OnSideTurnStart<MyEnchant>((card, enchantment, side) =>
    {
        if (side == CombatSide.Enemy)
        {
            // 敌方回合开始时执行
        }
    })
    .OnBeforeAttack<MyEnchant>((card, enchantment, command) =>
    {
        // 攻击结算前
    })
    .Commit();
```

`OnSideTurnStart` 同时接收玩家和敌方回合。如果只关心玩家回合，用 `OnTurnStart` 更简洁。

### 伤害/格挡/死亡（分发给拥有方的所有卡）

```csharp
// 受伤后 — 用 DamageReceivedContext 过滤
.OnAfterDamageReceived<MyEnchant>((card, enchantment, ctx) =>
{
    if (ctx.Dealer != null && ctx.Result.UnblockedDamage > 0)
    {
        // 被敌方实际打到了
    }
})

// 阻止死亡 — 返回 false 阻止，任一 false 即生效
.OnShouldDie<MyEnchant>((card, enchantment, creature) =>
{
    return false; // 阻止死亡
})
```

### Context Record

多参数的 hook 使用 record 类型打包参数，支持 positional destructuring：

```csharp
// DamageReceivedContext(Creature Target, DamageResult Result, Creature? Dealer, CardModel? Source)
// BlockGainContext(Creature Creature, decimal Amount, CardModel? Source)

.OnAfterDamageReceived<MyEnchant>((card, enchantment, ctx) =>
{
    var (target, result, dealer, source) = ctx;
    // ...
})
```

### 为什么用 lifecycle 回调而非重写虚方法？

直接重写 `EnchantmentModel.AfterCardPlayed()` 等虚方法**不经过 IsActive 守门** — 失活附魔照样被调用。lifecycle 回调是经过 scope / `WhenActive` 条件控制的安全通道。

例外：需要修改 vanilla 返回值（如 `ModifyDamageAdditive`）时，仍需重写虚方法。lifecycle 回调目前只有 `OnShouldDie` 支持返回值。

完整的回调列表、分发作用域和使用模式见 `docs/v2-lifecycle-wiki.md`。

## 移除附魔

公开 API：

```csharp
MultiEnchantmentApi.RemoveEnchantment(card, enchantment, RemovalReason.Manual);
```

这会走 v2 生命周期逻辑（触发 `OnRemoved` 回调、处理 veto），并清理所有相关状态。不要直接操作内部状态。

### RemovalReason

`OnRemoved` 的 `reason` 参数告诉你附魔为什么被移除，便于分支处理：

| RemovalReason | 来源 |
| --- | --- |
| `Manual` | 调用 `RemoveEnchantment()` 或手动移除 |
| `CardCleared` | 卡牌附魔整体清空（绕过 veto） |
| `CombatEnded` | `UntilCombatEnds` 战斗结束 |
| `TurnEnded` | `UntilTurnEnds` 回合结束 |
| `TurnLimitReached` | `LingerForTurns` 回合数耗尽 |
| `ActivationLimitReached` | `MaxActivations` 次数耗尽 |
| `Replaced` | 附魔被替换 |
| `ConditionMet` | `RemoveWhen` 的 predicate 返回 `true` |

`OnRemoved` 返回 `false` 可以阻止移除（veto）。但 `CardCleared` 是强制移除，不受 veto 影响。

## 多人同步与存档持久化

v2 的生命周期计数器（`ActivationCount`、`TurnsRemaining`）自动序列化到 enchantment 自身的 `Props.strings["MultiEnchantmentScopeData"]`。这些数据通过 game packet 同步到多人对端，通过 save file 持久化到存档。

**这意味着：**

- `LingerForTurns(3)` — 过了 2 回合后存档 → 读档 → `TurnsRemaining` 正确恢复为 1。
- `MaxActivations(3, OnPlay)` — 用了 2 次后对端玩家看到相同的 `ActivationCount = 2`。
- 战斗中克隆卡牌时，scope 状态也会随附魔一起复制。

**作者注意事项：**

1. `ConditionalActive` 和 `RemoveWhen` 的 `Func<>` predicate **不序列化** — 读档后由 registry 重新回填。保证 predicate 是纯函数（只依赖 `card` / `enchantment` 参数）。
2. 如果你的附魔有自定义运行时状态需要跨存档/多人同步，可以用 `enchantment.Props.strings` 存储自定义数据 — 它已经走 game packet 通道。
3. 读档后如需重建内存缓存，用 `OnRestored` 回调。

## 升级建议

**基础接入：**

1. 初始化时调用 `MultiEnchantmentApi.RequireApiVersion(2)`。
2. 对每个自定义附魔明确声明 `StackBehavior` 和 `StatusAggregation`。
3. 只想禁止重复时使用 `DisallowDuplicate`，不要自己只检查 `card.Enchantment`。
4. 需要判断所有槽位时使用 `MultiEnchantmentApi.HasEnchantment<T>(card)`。
5. `MergeAmount` 的后续应用逻辑写在 `OnMergedDelta`，不要依赖 `OnEnchant` 再次执行。
6. 修改伤害/格挡等动态变量时优先使用 `ModifyDynamicVar`，并避免和旧的 `EnchantDamage*` / `EnchantBlock*` 通道重复叠加。
7. 复杂调试或 UI 用 `MultiEnchantmentApi.Snapshots`，普通业务逻辑优先用便捷 API。

**战斗记录：**

8. 临时附魔（`UntilCombatEnds` / `UntilTurnEnds` / `LingerForTurns` / `MaxActivations`）默认不在战斗记录里显示（`Auto` 模式自动隐藏）。如果你的临时附魔仍需展示，显式指定 `.HistoryDisplay(HistoryDisplayMode.InActions)`。
9. 永久性附魔如果是内部实现细节（不希望玩家看到），指定 `.HistoryDisplay(HistoryDisplayMode.Hidden)` 或 `[Enchantment(HistoryDisplay = HistoryDisplayMode.Hidden)]`。
10. 需要把同一类型多条附魔记录归组显示时，用 `CustomGroup` 加 `HistoryGroupHeader`；需要完全替换显示文本时，用 `.HistoryText(formatter)` 或 `Definition.FormatHistoryText`。

**生命周期与 hook：**

11. 响应卡牌事件（打出、抽牌、消耗等）时**优先使用 lifecycle 回调**（`OnCardPlayed`、`OnCardExhausted` 等），而非重写 `EnchantmentModel` 虚方法。lifecycle 回调自动受 `IsActive` 守门保护。
12. 需要跨存档/多人同步的运行时状态，写进 `enchantment.Props.strings`。读档后用 `OnRestored` 重建内存缓存。
13. `WhenActive`、`WhenActiveStatus` 和 `RemoveWhen` 的 predicate 必须是纯函数 — 不序列化、读档后从 registry 重新回填。
14. 想做"满足条件就移除"用 `RemoveWhen`；想做"满足条件才生效、不满足就休眠"用 `WhenActive` 或 `WhenActiveStatus`。如果还要同时设置 `LingerForTurns` / `RemoveWhen` 等 lifetime scope，优先用 `WhenActiveStatus` / `ShouldBeActive` 叠加条件状态。
15. `OnSideTurnStart(side)` 会给**所有卡上所有附魔**广播，在 handler 里用 `side` 参数过滤。只关心玩家回合时用 `OnTurnStart` 更简洁。

## 常见问题

### 为什么 `.Enchantment is MyEnchant` 判断不准？

`card.Enchantment` 只能看到主附魔。MultiEnchantmentMod 允许额外附魔槽，所以应该使用：

```csharp
MultiEnchantmentApi.HasEnchantment<MyEnchant>(card)
```

或读取：

```csharp
MultiEnchantmentApi.Snapshots.ForCard(card)
```

### 我不想让自己的附魔重复出现，应该怎么做？

注册为 `DisallowDuplicate`：

```csharp
[Enchantment(Stack = StackBehavior.DisallowDuplicate, Status = StatusAggregation.NotApplicable)]
public sealed class MyEnchant : EnchantmentModel
{
}
```

### 为什么我的 `MergeAmount` 附魔第二次应用没有跑 `OnEnchant()`？

这是预期行为。`MergeAmount` 会把后续应用合并到锚点实例，后续副作用请写在 `EnchantmentDefinition<T>.OnMergedDelta()` 或 fluent `.OnMergedDelta(...)`。

### `ModifyDynamicVar` 里要不要乘以层数？

多数情况下不要。`MergeAmount` 会按 active gameplay slice 调用贡献公式，作者通常只需要写单层效果，例如 `current + 5m`。

### `WhenActive` 和 `RemoveWhen` 有什么区别？

- `WhenActive(predicate)` — 条件不满足时附魔"休眠"但保留，满足时"苏醒"继续生效。适合"仅攻击牌生效""仅在手牌中生效"等开关语义。
- `RemoveWhen(predicate, triggers)` — 条件满足时附魔**永久移除**。适合"HP 低于阈值时消失""被消耗后消失"等一次性语义。

如果你想让一个附魔“平时按条件休眠，同时满足另一个条件时永久移除”，不要在同一条 fluent 链里同时调用 `.WhenActive(...)` 和 `.RemoveWhen(...)`，因为它们都会设置 scope，后调用者会覆盖先调用者。推荐写法是：用 `.RemoveWhen(...)` 控制移除，再用 `.WhenActiveStatus(...)` 或 `EnchantmentDefinition<T>.ShouldBeActive(...)` 控制当前是否活跃。

### 为什么我的附魔在 `.WhenActive(false)` 后仍然触发了 `AfterCardPlayed`？

你可能直接重写了 `EnchantmentModel.AfterCardPlayed()` 虚方法。虚方法不经过 `IsActive` 守门。改用 lifecycle 回调 `.OnCardPlayed(...)` 或 `EnchantmentDefinition<T>.OnCardPlayed()`，它们会自动被抑制。

### 存档读取后 `ConditionalWeakTable` 里的缓存丢了怎么办？

用 `OnRestored` 回调。它在反序列化路径上触发（存档载入、多人 packet 到达），但不在新附魔首次应用时触发。在 `OnRestored` 里重建缓存：

```csharp
.OnRestored<MyEnchant>((card, enchantment) =>
{
    MyCache.Rebuild(card, enchantment);
})
```

### `OnCardEnteredCombat` 和 `OnCombatStart` 有什么区别？

- `OnCombatStart` — 每场战斗每张卡触发一次（含后来通过 Astrolabe 等加入的卡）。
- `OnCardEnteredCombat` — 卡每次进入战斗都触发（包括初始 deck setup 和中途生成）。

### `OnAfterDamageReceived` 分发给谁？

分发给**受伤方**（`target` 的 owner）所有战斗中卡上的所有活跃附魔，不是只给某张卡。在 handler 里用 `ctx.Target`、`ctx.Dealer`、`ctx.Source` 过滤。

### 怎么自定义一个 `ActivationTrigger`？

```csharp
var trigger = ActivationTrigger.Custom("mymod:OnRelicTriggered");
// 注册时用
.MaxActivations(3, trigger)
// 在你的 patch 里手动计数
MultiEnchantmentScopeSupport.NoteActivation(enchantment, trigger);
```

### 游戏 v0.106.x 之后 `EnchantBlockAdditive` 或回合钩子报方法找不到？

v0.106.x 的 vanilla `EnchantmentModel` 把 block 系列虚方法的 `ValueProp` 参数移除了。如果你的附魔重写了这两个方法：

```csharp
// ❌ v0.105.x 签名（v0.106.x 起运行时抛 MissingMethodException）
public override decimal EnchantBlockAdditive(decimal originalBlock, ValueProp props) { ... }
public override decimal EnchantBlockMultiplicative(decimal originalBlock, ValueProp props) { ... }

// ✅ v0.106.x+ 正确签名
public override decimal EnchantBlockAdditive(decimal originalBlock) { ... }
public override decimal EnchantBlockMultiplicative(decimal originalBlock) { ... }
```

伤害管线的 `EnchantDamageAdditive` / `EnchantDamageMultiplicative` 仍然保留 `ValueProp`，**不要改**。MultiEnchantmentMod 本体已经适配 v0.106.x；下游 mod 需要同步调整自己的 override。

同一轮更新里，原生回合钩子的静态 `Hook` 签名也变了。直接写 Harmony patch 时，请匹配当前签名：

```csharp
Hook.BeforeSideTurnStart(ICombatState, CombatSide, IReadOnlyList<Creature>)
Hook.AfterSideTurnStart(ICombatState, CombatSide, IReadOnlyList<Creature>)
Hook.BeforeTurnEnd(ICombatState, CombatSide, IEnumerable<Creature>)
Hook.AfterTurnEnd(ICombatState, CombatSide, IEnumerable<Creature>)
```

如果你只使用 MultiEnchantmentMod 的 `.OnSideTurnStart(...)` / `.OnBeforeSideTurnStart(...)` lifecycle 回调，不需要处理 `participants` 参数；框架已经适配并保留 `(card, enchantment, side)` 的稳定回调形状。

### handler 里调 `RemoveEnchantment` / `Enchant` / `CardCmd.*` 会不会崩？

不会。从 v2.1（2026-05-23 build）开始，**所有 lifecycle 入口都用 `.ToList()` 快照保护**，可以放心在 `OnCardChangedPiles`、`OnTurnEnd`、`OnApplied`、`RecalculateValues` 等任何 handler 里调修改集合的 mod API。详见 `docs/v2-lifecycle-wiki.md` 「在 lifecycle handler 里调用 mutating API」一节。

历史 bug 参考：v2.0 在多个附魔在 `OnCardChangedPiles` 同时调 `RemoveEnchantment` 时会抛 `InvalidOperationException`；v2.1 已修复。

### 为什么我的临时附魔（`UntilCombatEnds`）没出现在战斗记录里？

`Auto` 模式下，临时性作用域（`UntilCombatEnds` / `UntilTurnEnds` / `LingerForTurns` / `MaxActivations`）的附魔记录默认隐藏。战斗结束时这些附魔本就消失，记录意义不大。

如果仍需在战斗记录里显示，显式声明：

```csharp
// Attribute 写法
[Enchantment(
    Stack = StackBehavior.MergeAmount,
    Status = StatusAggregation.SharedAcrossStack,
    Scope = ScopeKind.UntilCombatEnds,
    HistoryDisplay = HistoryDisplayMode.InActions)]
public sealed class CombatBoost : EnchantmentModel { }

// Fluent 写法
MultiEnchantmentApi.Register<CombatBoost>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .WithScope(EnchantmentScope.UntilCombatEnds)
    .HistoryDisplay(HistoryDisplayMode.InActions)
    .Commit();
```

### `HistoryText` / `FormatHistoryText` 返回 `null` 会怎样？

会回退到 vanilla 的默认格式（"附魔了 <卡名>"）。这是故意设计的 — 可以在部分条件下返回 `null` 让 vanilla 处理，只在特殊情况返回自定义文本。

### `CustomGroup` 的分组标题是什么格式？

原样输出的字符串，框架不做任何包装。想要粗体就自己写 BBCode：

```csharp
HistoryGroupHeader = "[b]战斗强化[/b]"
```

纯文本也完全可以，视觉效果与行动区其他条目一致。

## 增量 API 速查（缺口完善轮）

> 详细说明见 `docs/v2-lifecycle-wiki.md`「增量钩子」一节。本节为速查表。

### 静态助手

| 函数 | 用途 |
| --- | --- |
| `MultiEnchantmentApi.NotifyPropsChanged(enchantment)` | 在非应用路径回调里改字段后强制重算 visual slices / extra-card-text / dynamic-var。 |
| `MultiEnchantmentApi.Enchant(card, enchantment, amount = 1, scopeOverride = null)` | 通过 v2 管线应用附魔，并可给这次应用传入无谓词、可持久化的实例级 scope 覆盖。 |
| `MultiEnchantmentApi.SetScopeOverride(card, enchantment, newScope)` | 修改或清除已附着实例的 scope 覆盖；`null` 表示回到注册默认。 |
| `MultiEnchantmentApi.GetScopeState(enchantment)` | `[Advanced]` 取当前 scope 状态快照（`ScopeRuntimeStateView?`）。 |
| `MultiEnchantmentApi.IsActive(enchantment)` | `[Advanced]` 当前 IsActive 求值结果。 |
| `MultiEnchantmentApi.GetSiblings(card, excludingSelf?)` | `[Advanced]` 同卡所有附魔，可排除自身。 |

### 注册扩展（fluent + 虚方法）

| Hook | 触发时机 | 用途 |
| --- | --- | --- |
| `OnAnyCardPlayed/Drawn/Exhausted/Discarded` | 战斗中**任意**卡发生事件 | 广播版（opt-in） |
| `OnSiblingApplied(card, self, newSibling)` | 同卡新邻居挂载之后 | 联动 / 互斥 |
| `OnSiblingRemoved(card, self, leftSibling, reason)` | 同卡邻居即将卸载（OnRemoved veto 后） | 联动收尾 |
| `ModifyPowerAmountGiven((snapshot, ctx, current) => …)` | 宿主卡施加能力、原版监听管线结算后 | "这张卡给出的易伤 +1 层"（contribution 型，可多 mod 复合） |
| `OnCardAppliedPower(card, self, ctx)` | 宿主卡施加的能力增量结算完成后 | 充能计数 / 连击记账（ctx 含 Power / Amount / Applier / Target） |
| `OnCardTransformed(original, self, replacement)` | 宿主卡被变形为另一张卡之后 | 迁移自定义运行时状态、清理 card-keyed 缓存 |
| `OnCardCloned(original, self, clone)` | 宿主卡被玩法效果克隆之后（克隆体已继承附魔） | 调整克隆体副本（重置计数 / 撕掉灵魂绑定标记）；UI 预览不触发 |

### Stack 配置升级

`Stack(StackDefinition)` 重载，可一次性指定 `MaxInstances` + `OnOverflow`：

```csharp
StackDefinition def = new(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
{
    MaxInstances = 5,
    OnOverflow = StackOverflowPolicy.ReplaceOldest, // 或 ReplaceNewest / Reject
};
MultiEnchantmentApi.Register<MyAura>().Stack(def).Commit();
```

被驱逐的实例 `OnRemoved` 收到 `RemovalReason.OverflowEvicted`。

### Snapshot 增强

`EnchantmentStackSnapshot.ScopeStates`（`IReadOnlyDictionary<EnchantmentModel, ScopeRuntimeStateView>?`）新增。`snapshot.StateOf(enchantment)` 便捷读取。在 `FormatExtraText` / `VisualSlices` / `[ModifyDynamicVar]` 内用以实时显示 `ActivationCount` / `TurnsRemaining` / `IsLimitReached`。

### 单实例 Scope 覆盖

```csharp
MultiEnchantmentApi.Enchant(card, new MyEnchant(), 1, EnchantmentScope.UntilCombatEnds);
MultiEnchantmentApi.SetScopeOverride(card, enchantment, EnchantmentScope.UntilTurnEnds);
MultiEnchantmentApi.SetScopeOverride(card, enchantment, null); // 清除覆盖
```

允许作为覆盖的 scope：`Permanent`、`UntilCombatEnds`、`UntilTurnEnds`、`LingerForTurns(N)`、`MaxActivations(N, trigger)`。`ConditionalActive` / `RemoveWhen` 因为携带谓词不可持久化，会被拒绝并返回 `null` / `false`。运行时有效值为 `OverrideScope ?? registryScope`；`ScopeRuntimeStateView.HasOverride` 可用于表现层显示。

### 重入守护

`ApplyDynamicVarEnchantments` 现在带 `[ThreadStatic]` 重入栈：同一帧内对同一 `(card, varKey)` 的递归求值会被识别并跳过，写一行 warn `ModifyDynamicVar reentrancy detected for var=<key> on card=<id>`，第二层求值返回当前 `baseValue`。正常代码无感。

## Analyzer Rules

`MultiEnchantmentMod.Analyzers` 是可选但强烈推荐启用的编译期检查。它不会随普通 `.dll` 引用自动启用，需要在你的 `.csproj` 里加：

```xml
<ItemGroup>
  <Analyzer Include="$(MultiEnchantmentModPath)/../MultiEnchantmentMod.Analyzers.dll" />
</ItemGroup>
```

完整接入方式见 `docs/integration.md`。启用后，很多“运行时才发现附魔没注册 / 动态变量没生效”的问题会在 IDE 里直接标出来。

| ID | 含义 | 怎么修 |
| --- | --- | --- |
| `MEM001` | `[Enchantment]` 标在了非 `EnchantmentModel` 类型上 | 把 attribute 移到附魔模型类，或让该类继承 `EnchantmentModel` |
| `MEM002` | 模型和 definition 的堆叠语义不一致 | 保持 `[Enchantment]` 与 `EnchantmentDefinition<T>` 的声明一致 |
| `MEM003` | `KeywordEvalMode.PerTotalAmount` 用在非 `MergeAmount` 附魔上 | 改成 `MergeAmount`，或换成 `PerInstance` / `Constant` / 自定义计算 |
| `MEM004` | `EnchantmentDefinition<T>` 没有可访问的无参构造函数 | 增加 `public MyDefinition() { }` 或删掉带参构造依赖 |
| `MEM005` | 执行策略和堆叠语义可疑 | 对照“Hook 执行策略”章节；通常是 `DuplicateInstance + MergedTotal` 或 `ExistenceStack + PerLiveInstance` |
| `MEM006` | `[EnchantmentPresentation]` 没有对应展示覆写 | 覆写 `TryFormatExtraText` / `GetVisualSliceAmounts` / `GetVisualSlices`，或删除 attribute |
| `MEM007` | 程序集缺少 API 兼容声明 | 添加 `[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]` |
| `MEM008` | 同一个附魔模型有多个 `EnchantmentDefinition<T>` | 合并成一个 definition |
| `MEM009` | `[ModifyDynamicVar]` 方法签名错误 | 改成 `decimal Method(EnchantmentStackSnapshot snapshot, decimal currentValue)` |
| `MEM011` | `MaxActivations` 走 attribute 默认触发器，默认会变成 `OnPlay` | 需要非默认触发器时，用 fluent `.MaxActivations(N, ActivationTrigger.AfterCardPlayed)` 或 `EnchantmentDefinition.Scope`；不要在 attribute 里写 `Activation = ...` |
| `MEM012` | 非 `MergeAmount` 附魔覆写了 `OnMergedDelta` | 改成 `MergeAmount`，或删除这个覆写 |
| `MEM013` | `[ModifyEnergyCost]` / `[ModifyCardPlayCount]` 方法签名错误 | 费用用 `decimal Method(EnchantmentStackSnapshot snapshot, decimal currentCost)`；次数用 `int Method(EnchantmentStackSnapshot snapshot, int currentPlayCount)` |

`MEM007` 和 `MEM009` 带 IDE quick fix，可以一键补 compatibility attribute 或改方法签名。其它规则只报告，因为框架无法安全猜出你的设计意图。

## 延伸阅读

- 生命周期专题：`docs/v2-lifecycle-wiki.md` — 详细的 scope、回调、RemoveWhen、vanilla hook 桥接说明
- 生命周期示例：`MultiEnchantmentMod.Samples/Samples/08_UntilCombatEndsScope.cs` 到 `13_LifecycleAndVetoHooks.cs`
- API 类型签名：`Api/IEnchantmentRegistration.cs`（接口 + 强类型扩展方法）
- Definition 基类：`Api/EnchantmentDefinition.cs`（`protected virtual` 回调列表）
- 战斗记录类型定义：`Api/HistoryDisplayMode.cs`（`HistoryDisplayMode` 枚举 + `HistoryTextFormatter` 委托）

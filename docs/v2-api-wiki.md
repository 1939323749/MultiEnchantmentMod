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

如果你不显式注册第三方附魔，默认是 `DisallowDuplicate`。v2 也会对部分未注册但覆写了 `EnchantDamage*` / `EnchantBlock*` 的第三方附魔做 auto-detect，并注册为 `MergeAmount + SharedAcrossStack`；如果不希望这样，请显式注册自己的行为。

### StatusAggregation：多实例状态怎么汇总

推荐配对：

| StackBehavior | 推荐 StatusAggregation |
| --- | --- |
| `MergeAmount` | `SharedAcrossStack` |
| `DuplicateInstance` | `PerInstanceOwned` |
| `ExistenceStack` | `AnyInstanceCountsAsOne` |
| `DisallowDuplicate` | `NotApplicable` 或任意值 |

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
- `.Execution(p => ...)` — 覆盖 hook 执行次数。
- `.OnMergedDelta(...)` / `.OnMergedRefresh(...)` — 处理 `MergeAmount` 的合并和刷新。
- `.TrackKeyword(keyword, snapshot => amount)` — 动态添加或移除关键词。
- `.ModifyDynamicVar(key, (snapshot, current) => next)` — 修改卡牌动态变量。
- `.FormatExtraText(...)` / `.VisualSlices(...)` — 控制额外文本和 UI 切片。

**作用域与激活条件：**

- `.WithScope(scope)` — 设置任意作用域。
- `.LingerForTurns(n)` — 持续 n 回合后移除。
- `.MaxActivations(n, trigger)` — 触发 n 次后移除。
- `.WhenActive(predicate)` — 条件满足时才活跃（不控制移除）。
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

对于调试工具、复杂 UI 或需要读取堆叠细节的 mod，可以使用高级 snapshot API：

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

当一个附魔有多层或多个实例时，hook 调用次数由 `HookExecutionMode` 控制。

常见模式：

- `Default`：使用行为默认值。
- `MergedTotal`：按 active 总层数调用。
- `PerVisualSlice`：按 UI 切片调用。
- `PerLiveInstance`：按真实实例调用。
- `FirstActiveInstanceOnly`：只要有激活实例就调用一次。

默认策略：

- `MergeAmount`：大多数 hook 按 `MergedTotal`；`OnPlay` 默认按 `PerLiveInstance`，避免既按 `Amount` 放大又重复执行。
- `DuplicateInstance`：默认 `PerLiveInstance`。
- `ExistenceStack`：默认 `FirstActiveInstanceOnly`。
- `DisallowDuplicate`：默认 `FirstActiveInstanceOnly`。

Attribute 写法：

```csharp
[EnchantmentExecution(OnPlay = HookExecutionMode.MergedTotal)]
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class MyEnchant : EnchantmentModel
{
}
```

fluent 写法：

```csharp
MultiEnchantmentApi.Register<MyEnchant>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .Execution(p => p.OnPlay(HookExecutionMode.MergedTotal))
    .Commit();
```

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
13. `WhenActive` 和 `RemoveWhen` 的 predicate 必须是纯函数 — 不序列化、读档后从 registry 重新回填。
14. 想做"满足条件就移除"用 `RemoveWhen`；想做"满足条件才生效、不满足就休眠"用 `WhenActive`。二者不是替代关系。
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

多数情况下不要。`MergeAmount` 会按 active gameplay slice 调用你的公式，你通常只需要写单层效果，例如 `current + 5m`。

### `WhenActive` 和 `RemoveWhen` 有什么区别？

- `WhenActive(predicate)` — 条件不满足时附魔"休眠"但保留，满足时"苏醒"继续生效。适合"仅攻击牌生效""仅在手牌中生效"等开关语义。
- `RemoveWhen(predicate, triggers)` — 条件满足时附魔**永久移除**。适合"HP 低于阈值时消失""被消耗后消失"等一次性语义。

两者可以组合使用：一个附魔同时有 `WhenActive`（控制活跃状态）和 `RemoveWhen`（控制移除时机）。

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

### 游戏 v0.106.0 之后 `EnchantBlockAdditive` 报方法找不到？

v0.106.0 的 vanilla `EnchantmentModel` 把 block 系列虚方法的 `ValueProp` 参数移除了。如果你的附魔重写了这两个方法：

```csharp
// ❌ v0.105.x 签名（v0.106.0 起运行时抛 MissingMethodException）
public override decimal EnchantBlockAdditive(decimal originalBlock, ValueProp props) { ... }
public override decimal EnchantBlockMultiplicative(decimal originalBlock, ValueProp props) { ... }

// ✅ v0.106.0+ 正确签名
public override decimal EnchantBlockAdditive(decimal originalBlock) { ... }
public override decimal EnchantBlockMultiplicative(decimal originalBlock) { ... }
```

伤害管线的 `EnchantDamageAdditive` / `EnchantDamageMultiplicative` 仍然保留 `ValueProp`，**不要改**。MultiEnchantmentMod 本体已经适配 v0.106.0；下游 mod 需要同步调整自己的 override。

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

## 延伸阅读

- 生命周期专题：`docs/v2-lifecycle-wiki.md` — 详细的 scope、回调、RemoveWhen、vanilla hook 桥接说明
- 生命周期示例：`MultiEnchantmentMod.Samples/Samples/08_UntilCombatEndsScope.cs` 到 `13_LifecycleAndVetoHooks.cs`
- API 类型签名：`Api/IEnchantmentRegistration.cs`（接口 + 强类型扩展方法）
- Definition 基类：`Api/EnchantmentDefinition.cs`（`protected virtual` 回调列表）
- 战斗记录类型定义：`Api/HistoryDisplayMode.cs`（`HistoryDisplayMode` 枚举 + `HistoryTextFormatter` 委托）

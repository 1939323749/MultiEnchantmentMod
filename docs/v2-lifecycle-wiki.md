# MultiEnchantmentMod v2 生命周期指南

本文是 `docs/v2-api-wiki.md` 的专题补充，专门说明 v2 附魔的生命周期：什么时候应用、什么时候活跃、什么时候触发回调、什么时候移除，以及如何正确选择 `Scope`。

如果你还没有接入 v2 API，请先阅读总览文档里的初始化和注册部分。

## 一句话总结

- `Scope` 决定附魔”存在多久”（`Permanent` / `UntilCombatEnds` / `LingerForTurns` / `MaxActivations` / `RemoveWhen` / `ConditionalActive`）。
- `WhenActive` / `ConditionalActive` 决定附魔”当前是否活跃”，不等于移除。
- `OnApplied` / `OnRemoved` / `OnRestored` 处理应用、移除、恢复时的副作用。
- `RemovalReason` 告诉你为什么被移除（含 `ConditionMet` 用于 `RemoveWhen`）。
- `LingerForTurns` 是按回合持续，`MaxActivations` 是按触发次数消耗。
- 15 个 vanilla hook 桥接回调（`OnCardPlayed`、`OnSideTurnStart`、`OnShouldDie` 等）让你在 lifecycle 框架内安全响应游戏事件。

## 生命周期总览

一个 v2 附魔通常会经历这些阶段：

1. **注册定义**：通过 `[Enchantment]`、`EnchantmentDefinition<T>` 或 `MultiEnchantmentApi.Register<T>()` 声明行为。
2. **应用到卡牌**：附魔被成功附到卡上，进入主附魔槽或额外附魔槽。
3. **执行 `OnApplied`**：仅新应用时执行，用于初始化状态或记录副作用。
4. **进入战斗/回合阶段**：根据当前阶段执行 `OnCombatStart`、`OnTurnStart`、`OnTurnEnd`、`OnCombatEnd`。
5. **响应游戏事件**：vanilla hook 桥接回调（`OnCardPlayed`、`OnAfterDamageReceived`、`OnSideTurnStart` 等）在对应的游戏事件发生时自动分发给活跃附魔。
6. **触发或计数**：如果使用 `MaxActivations`，指定事件发生时会累计激活次数。
7. **检查作用域**：战斗结束、回合结束、持续回合数到期、激活次数到达上限、`RemoveWhen` 条件满足等情况会触发移除。
8. **执行 `OnRemoved`**：移除前执行；返回 `true` 允许移除，返回 `false` 可以 veto 本次移除。

注意：`OnCombatStart/End` 和 `OnTurnStart/End` 是阶段回调，本身不代表附魔一定会移除。是否移除由 `Scope`、触发次数、手动移除或清空卡牌附魔等逻辑决定。

## Scope：控制附魔存在多久

### Scope 速查表

| Scope | 什么时候移除 | 常见 RemovalReason | 适合场景 |
| --- | --- | --- | --- |
| `Permanent` | 不因回合/战斗自然结束移除 | 取决于移除来源 | 永久增强、长期标记 |
| `UntilCombatEnds` | 当前战斗结束 | `CombatEnded` | 本场战斗临时效果 |
| `UntilTurnEnds` | 当前回合结束 | `TurnEnded` | 本回合临时效果 |
| `LingerForTurns(n)` | 持续回合数到期 | `TurnLimitReached` | 持续 N 回合的 buff/debuff |
| `MaxActivations(n, trigger)` | 指定事件触发 n 次后 | `ActivationLimitReached` | 一次性/多次使用预算 |
| `ConditionalActive(predicate)` | 不自动移除，只控制活跃状态 | 不适用 | 条件满足时才生效 |
| `RemoveWhen(predicate, triggers)` | 指定 trigger 时 predicate 为 true 即移除 | `ConditionMet` | 满足条件后永久消失 |

### Permanent

`Permanent` 是默认长期作用域。它不会因为回合结束或战斗结束自动消失。

```csharp
public sealed class MyDefinition : EnchantmentDefinition<MyEnchantment>
{
    public override EnchantmentScope Scope => EnchantmentScope.Permanent;
}
```

它仍然可能被手动移除、替换、卡牌清空附魔等流程移除，所以如果你有外部状态需要清理，仍应考虑 `OnRemoved`。

### UntilCombatEnds

`UntilCombatEnds` 表示只持续到当前战斗结束。

```csharp
[Enchantment(
    Stack = StackBehavior.MergeAmount,
    Status = StatusAggregation.SharedAcrossStack,
    Scope = ScopeKind.UntilCombatEnds)]
public sealed class CombatGuard : EnchantmentModel
{
    public override bool ShowAmount => true;
}
```

适合“本场战斗 +N 伤害”“本场战斗获得某关键词”“战斗内临时机制”等效果。

### UntilTurnEnds

`UntilTurnEnds` 表示只持续到当前回合结束。

```csharp
[Enchantment(
    Stack = StackBehavior.DisallowDuplicate,
    Status = StatusAggregation.NotApplicable,
    Scope = ScopeKind.UntilTurnEnds)]
public sealed class ThisTurnOnly : EnchantmentModel
{
}
```

适合“本回合下一张攻击牌增强”“本回合费用变化”“本回合临时关键词”等效果。

### LingerForTurns

`LingerForTurns(n)` 表示持续指定回合数，回合限制达到后移除。

Attribute 写法：

```csharp
[Enchantment(
    Stack = StackBehavior.DisallowDuplicate,
    Status = StatusAggregation.NotApplicable,
    Scope = ScopeKind.LingerForTurns,
    LingerTurns = 2)]
public sealed class TwoTurnBuff : EnchantmentModel
{
}
```

fluent 写法：

```csharp
MultiEnchantmentApi.Register<TwoTurnBuff>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .LingerForTurns(2)
    .Commit();
```

它适合“持续 N 回合”的设计，不适合“触发 N 次”的设计。触发次数请用 `MaxActivations`。

### MaxActivations

`MaxActivations(n, trigger)` 表示指定事件触发 n 次后移除。

支持的 `ActivationTrigger`：

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
| `Custom("id")` | 第三方自定义事件 | 按调用方决定 |

示例：打出 3 次后移除。

```csharp
MultiEnchantmentApi.Register<ChargedSharpen>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .MaxActivations(3, ActivationTrigger.OnPlay)
    .Commit();
```

如果需要在次数耗尽时做特殊处理，可以配合 `OnRemoved` 判断 `RemovalReason.ActivationLimitReached`。

### ConditionalActive / WhenActive

`ConditionalActive` 和 `.WhenActive(...)` 控制“是否活跃”，不负责移除。

```csharp
MultiEnchantmentApi.Register<HandOnlyEnchant>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .WhenActive((card, enchantment) => card.Pile?.Type == PileType.Hand)
    .Commit();
```

适合“在手牌中才生效”“满足某条件才贡献关键词/动态变量/状态”的设计。

重点：条件不满足时，附魔仍然存在，只是当前不活跃。如果你希望它消失，需要使用临时作用域、`RemoveWhen` 或主动移除。

### RemoveWhen

`RemoveWhen(predicate, triggers)` 在指定的 `ActivationTrigger` 触发时重新评估 predicate；一旦返回 `true`，附魔立即被移除（`RemovalReason.ConditionMet`）。与 `ConditionalActive` 的区别：`ConditionalActive` 让附魔"休眠但保留"，`RemoveWhen` 让附魔"满足条件后永久消失"。

```csharp
// HP 低于 50% 时自动移除
MultiEnchantmentApi.Register<Overconfidence>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .RemoveWhen(
        (card, enchantment) => card.Owner?.Hp < card.Owner?.MaxHp / 2,
        ActivationTrigger.AfterDamageReceived)
    .Commit();
```

```csharp
// 卡被消耗后移除
MultiEnchantmentApi.Register<FragileBoost>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .RemoveWhen(
        (card, enchantment) => card.Pile?.Type == PileType.Exhaust,
        ActivationTrigger.AfterCardExhausted)
    .Commit();
```

注意事项：

- `RemoveWhen` 的 predicate **不会被序列化**（与 `ConditionalActive` 一致），读档后从 registry 回填。因此 predicate 必须是纯函数。
- `RemoveWhen` 跨战斗持续监控 — 战斗结束时**不会**被自动清掉（与 `UntilCombatEnds` 不同）。
- 空的 `checkOn` 列表等同于 `Permanent` — predicate 永远不会被评估。

## 生命周期回调

生命周期回调可以通过 `EnchantmentDefinition<T>` 覆写，也可以用 fluent builder 注册。

### Definition 写法

```csharp
[Enchantment(Stack = StackBehavior.MergeAmount, Status = StatusAggregation.SharedAcrossStack)]
public sealed class LifecycleEnchant : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public sealed class LifecycleEnchantDefinition : EnchantmentDefinition<LifecycleEnchant>
{
    public override EnchantmentScope Scope => EnchantmentScope.UntilCombatEnds;

    protected override void OnApplied(CardModel card, LifecycleEnchant enchantment)
    {
        // 新应用时执行；不用于 save-restore / clone 路径
    }

    protected override bool OnRemoved(
        CardModel card,
        LifecycleEnchant enchantment,
        RemovalReason reason)
    {
        // 返回 true 允许移除；返回 false veto 本次移除
        return reason != RemovalReason.Manual;
    }

    protected override void OnCombatStart(CardModel card, LifecycleEnchant enchantment) { }
    protected override void OnCombatEnd(CardModel card, LifecycleEnchant enchantment) { }
    protected override void OnTurnStart(CardModel card, LifecycleEnchant enchantment) { }
    protected override void OnTurnEnd(CardModel card, LifecycleEnchant enchantment) { }
}
```

### Fluent 写法

```csharp
MultiEnchantmentApi.Register<LifecycleEnchant>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .WithScope(EnchantmentScope.UntilCombatEnds)
    .OnApplied<LifecycleEnchant>((card, enchantment) =>
    {
        // 初始化状态
    })
    .OnRemoved<LifecycleEnchant>((card, enchantment, reason) =>
    {
        // 清理状态；true = 允许移除，false = 阻止移除
        return true;
    })
    .OnTurnStart<LifecycleEnchant>((card, enchantment) =>
    {
        // 回合开始逻辑
    })
    .Commit();
```

### 回调速查表

#### 生命周期阶段回调

| 回调 | 触发时机 | 常见用途 |
| --- | --- | --- |
| `OnApplied` | 新附魔成功附到卡上后 | 初始化状态、记录基准值、添加一次性副作用 |
| `OnRemoved` | 附魔即将被移除时 | 清理状态、撤销副作用、按原因分支处理 |
| `OnRestored` | 存档/packet 反序列化恢复后 | 重建不能序列化的运行时缓存 |
| `OnCombatStart` | 战斗开始阶段 | 战斗内初始化、刷新状态 |
| `OnCombatEnd` | 战斗结束阶段 | 战斗结束前检查状态、统计或清理 |
| `OnTurnStart` | 玩家回合开始 | 回合刷新、倒计时相关逻辑 |
| `OnTurnEnd` | 玩家回合结束 | 回合末处理、临时状态检查 |

#### Vanilla Hook 桥接回调（卡牌事件）

| 回调 | 桥接的 Vanilla Hook | 作用域 | 参数 |
| --- | --- | --- | --- |
| `OnCardPlayed` | `Hook.AfterCardPlayed` | 仅附魔所在卡 | `(card, enchantment)` |
| `OnCardDrawn` | `Hook.AfterCardDrawn` | 仅附魔所在卡 | `(card, enchantment)` |
| `OnCardExhausted` | `Hook.AfterCardExhausted` | 仅附魔所在卡 | `(card, enchantment)` |
| `OnCardDiscarded` | `Hook.AfterCardDiscarded` | 仅附魔所在卡 | `(card, enchantment)` |
| `OnCardEnteredCombat` | `Hook.AfterCardEnteredCombat` | 仅附魔所在卡 | `(card, enchantment)` |
| `OnCardChangedPiles` | `Hook.AfterCardChangedPiles` | 仅附魔所在卡 | `(card, enchantment, oldPile, source)` |
| `OnCardRetained` | `Hook.AfterFlush` | 仅附魔所在卡 | `(card, enchantment)` |

#### Vanilla Hook 桥接回调（战斗流程）

| 回调 | 桥接的 Vanilla Hook | 作用域 | 参数 |
| --- | --- | --- | --- |
| `OnSideTurnStart` | `Hook.AfterSideTurnStart` | 所有玩家的所有卡 | `(card, enchantment, side)` |
| `OnBeforeSideTurnStart` | `Hook.BeforeSideTurnStart` | 所有玩家的所有卡 | `(card, enchantment, side)` |
| `OnBeforeAttack` | `Hook.BeforeAttack` | 所有玩家的所有卡 | `(card, enchantment, command)` |
| `OnAfterAttack` | `Hook.AfterAttack` | 所有玩家的所有卡 | `(card, enchantment, command)` |

#### Vanilla Hook 桥接回调（伤害/格挡/死亡）

| 回调 | 桥接的 Vanilla Hook | 作用域 | 参数 |
| --- | --- | --- | --- |
| `OnAfterDamageReceived` | `Hook.AfterDamageReceived` | 卡牌拥有方的所有卡 | `(card, enchantment, DamageReceivedContext)` |
| `OnBeforeBlockGained` | `Hook.BeforeBlockGained` | 卡牌拥有方的所有卡 | `(card, enchantment, BlockGainContext)` |
| `OnBlockGained` | `Hook.AfterBlockGained` | 卡牌拥有方的所有卡 | `(card, enchantment, BlockGainContext)` |
| `OnShouldDie` | `Hook.ShouldDie` | 卡牌拥有方的所有卡 | `(card, enchantment, creature) → bool` |

补充说明：

- `OnApplied` 只表示”新应用”，不应当作为存档恢复或克隆恢复逻辑。存档恢复请用 `OnRestored`。
- `OnRemoved` 返回 `false` 可以阻止大多数移除，但 `CardCleared` 会绕过 veto。
- **所有** vanilla hook 桥接回调都受 `IsActive` 守门 — 失活附魔不会收到任何事件。
- 回调异常会被捕获并记录，避免单个 handler 直接破坏战斗流程；但仍应让回调尽量简单、可预测。
- `OnShouldDie` 是唯一带返回值的回调：返回 `false` 阻止死亡，`true` 表示不反对。多个附魔中**任一返回 `false`** 即阻止死亡（与 vanilla 语义一致）。

## RemovalReason：为什么被移除

`RemovalReason` 让你在 `OnRemoved` 中区分移除来源。

| RemovalReason | 常见来源 | 适合处理什么 |
| --- | --- | --- |
| `Manual` | 主动调用移除 API 或手动移除流程 | 用户/其他 mod 主动清理 |
| `CardCleared` | 卡牌附魔整体清空 | 强制清理，避免残留状态 |
| `CombatEnded` | `UntilCombatEnds` 战斗结束 | 战斗临时效果收尾 |
| `TurnEnded` | `UntilTurnEnds` 回合结束 | 本回合临时效果收尾 |
| `TurnLimitReached` | `LingerForTurns` 到期 | 持续回合数耗尽 |
| `ActivationLimitReached` | `MaxActivations` 次数耗尽 | 使用次数耗尽 |
| `Replaced` | 附魔被替换 | 撤销旧效果、迁移状态 |
| `ConditionMet` | `RemoveWhen` predicate 返回 `true` | 条件移除收尾 |

示例：只阻止手动移除，不阻止自然过期。

```csharp
protected override bool OnRemoved(CardModel card, MyEnchant enchantment, RemovalReason reason)
{
    return reason != RemovalReason.Manual;
}
```

示例：次数耗尽时允许第一次 veto，让附魔多活一次。

```csharp
private static bool vetoedOnce;

MultiEnchantmentApi.Register<ChargedSharpen>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .MaxActivations(3, ActivationTrigger.OnPlay)
    .OnRemoved<ChargedSharpen>((card, enchantment, reason) =>
    {
        if (reason == RemovalReason.ActivationLimitReached && !vetoedOnce)
        {
            vetoedOnce = true;
            return false;
        }

        return true;
    })
    .Commit();
```

## 常见设计模式

### 本场战斗临时附魔

使用 `UntilCombatEnds`。

```csharp
[Enchantment(
    Stack = StackBehavior.MergeAmount,
    Status = StatusAggregation.SharedAcrossStack,
    Scope = ScopeKind.UntilCombatEnds)]
public sealed class CombatOnlyPower : EnchantmentModel
{
}
```

### 本回合临时附魔

使用 `UntilTurnEnds`。

```csharp
[Enchantment(
    Stack = StackBehavior.DisallowDuplicate,
    Status = StatusAggregation.NotApplicable,
    Scope = ScopeKind.UntilTurnEnds)]
public sealed class TurnOnlyPower : EnchantmentModel
{
}
```

### 持续若干回合

使用 `LingerForTurns`。

```csharp
MultiEnchantmentApi.Register<SlowBurn>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .LingerForTurns(3)
    .Commit();
```

### 触发若干次后消失

使用 `MaxActivations`。

```csharp
MultiEnchantmentApi.Register<ThreeUses>()
    .Stack(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
    .MaxActivations(3, ActivationTrigger.OnPlay)
    .Commit();
```

### 条件满足时才活跃

使用 `WhenActive`。

```csharp
MultiEnchantmentApi.Register<AttackOnlyBonus>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .WhenActive((card, enchantment) => card.Type == CardType.Attack)
    .Commit();
```

### 响应卡牌事件

使用 vanilla hook 桥接回调代替重写 `EnchantmentModel` 虚方法。这样 `IsActive` 守门自动生效。

Definition 写法：

```csharp
public sealed class BurnOnExhaustDefinition : EnchantmentDefinition<BurnOnExhaust>
{
    protected override void OnCardExhausted(CardModel card, BurnOnExhaust enchantment)
    {
        // 附魔所在卡被消耗时触发
        Log.Info($"[BurnOnExhaust] {card.Name} exhausted — dealing damage");
    }

    protected override void OnCardDrawn(CardModel card, BurnOnExhaust enchantment)
    {
        // 附魔所在卡被抽到时触发
        Log.Info($"[BurnOnExhaust] {card.Name} drawn");
    }
}
```

Fluent 写法：

```csharp
MultiEnchantmentApi.Register<BurnOnExhaust>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .OnCardExhausted<BurnOnExhaust>((card, enchantment) =>
    {
        Log.Info($"[BurnOnExhaust] {card.Name} exhausted");
    })
    .Commit();
```

### 响应伤害事件

`OnAfterDamageReceived` 会分发给卡牌拥有方的所有附魔。用 `DamageReceivedContext` 判断来源。

```csharp
MultiEnchantmentApi.Register<ThornsEnchant>()
    .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
    .OnAfterDamageReceived<ThornsEnchant>((card, enchantment, ctx) =>
    {
        // 被敌方攻击时反伤
        if (ctx.Dealer != null && ctx.Result.UnblockedDamage > 0)
        {
            Log.Info($"[Thorns] Received {ctx.Result.UnblockedDamage} damage from {ctx.Dealer.Name}");
        }
    })
    .Commit();
```

### 响应敌方回合

`OnSideTurnStart` 和 `OnBeforeSideTurnStart` 同时接收玩家方和敌方回合事件。用 `CombatSide` 参数过滤。

```csharp
MultiEnchantmentApi.Register<EnemyTurnDebuff>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .OnSideTurnStart<EnemyTurnDebuff>((card, enchantment, side) =>
    {
        if (side == CombatSide.Enemy)
        {
            Log.Info("[EnemyTurnDebuff] Enemy turn started — applying effect");
        }
    })
    .Commit();
```

与 `OnTurnStart` 的关系：`OnTurnStart` 等价于 `OnSideTurnStart` 中 `side == CombatSide.Player` 的特化。如果你只关心玩家回合，用 `OnTurnStart` 更简洁。

### 阻止死亡

`OnShouldDie` 是守卫 hook — 返回 `false` 阻止死亡。适合"复活""死亡保护"类效果。

```csharp
public sealed class LastStandDefinition : EnchantmentDefinition<LastStand>
{
    public override EnchantmentScope Scope =>
        EnchantmentScope.MaxActivations(1, ActivationTrigger.Custom("shouldDie"));

    protected override bool OnShouldDie(CardModel card, LastStand enchantment, Creature creature)
    {
        Log.Info("[LastStand] Preventing death!");
        return false; // 阻止死亡
    }
}
```

注意：当多个附魔都注册了 `OnShouldDie` 时，**任一返回 `false`** 就阻止死亡（与 vanilla 语义一致）。

### 追踪卡牌堆位变化

`OnCardChangedPiles` 让你知道卡什么时候在各堆之间移动。`card.Pile.Type` 是新堆位，`oldPile` 是旧堆位。

```csharp
MultiEnchantmentApi.Register<PileTracker>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.NotApplicable)
    .OnCardChangedPiles<PileTracker>((card, enchantment, oldPile, source) =>
    {
        Log.Info($"[PileTracker] {card.Name} moved from {oldPile} to {card.Pile?.Type}");
    })
    .Commit();
```

## 我该选哪个 API？

| 需求 | 推荐 API |
| --- | --- |
| **作用域** | |
| 一直存在 | `EnchantmentScope.Permanent` 或默认 scope |
| 当前战斗结束后消失 | `ScopeKind.UntilCombatEnds` / `EnchantmentScope.UntilCombatEnds` |
| 当前回合结束后消失 | `ScopeKind.UntilTurnEnds` / `EnchantmentScope.UntilTurnEnds` |
| 持续 N 回合 | `LingerForTurns(N)` |
| 触发 N 次后消失 | `MaxActivations(N, trigger)` |
| 存在但有条件生效 | `ConditionalActive(...)` / `.WhenActive(...)` |
| 满足条件时移除 | `RemoveWhen(predicate, triggers)` |
| **生命周期** | |
| 应用时初始化 | `OnApplied` |
| 移除时清理 | `OnRemoved` |
| 存档恢复后重建缓存 | `OnRestored` |
| 战斗开始刷新 | `OnCombatStart` |
| 回合开始刷新 | `OnTurnStart` |
| **卡牌事件** | |
| 卡被打出后 | `OnCardPlayed` |
| 卡被抽到后 | `OnCardDrawn` |
| 卡被消耗后 | `OnCardExhausted` |
| 卡被弃掉后 | `OnCardDiscarded` |
| 卡进入战斗时 | `OnCardEnteredCombat` |
| 卡换堆位时 | `OnCardChangedPiles` |
| 卡被保留（回合末不弃）时 | `OnCardRetained` |
| **战斗流程** | |
| 任一方回合开始 | `OnSideTurnStart(side)` |
| 任一方回合开始前 | `OnBeforeSideTurnStart(side)` |
| 攻击结算前 | `OnBeforeAttack(command)` |
| 攻击结算后 | `OnAfterAttack(command)` |
| **伤害/格挡/死亡** | |
| 拥有方受伤后 | `OnAfterDamageReceived(ctx)` |
| 拥有方获挡前 | `OnBeforeBlockGained(ctx)` |
| 拥有方获挡后 | `OnBlockGained(ctx)` |
| 阻止死亡 | `OnShouldDie(creature) → false` |

## 和 StackBehavior 的关系

生命周期和堆叠行为是两个维度：

- `StackBehavior` 决定同一种附魔重复应用时怎么合并或创建实例。
- `Scope` 决定这些附魔实例或堆叠状态什么时候过期。

特别注意 `MergeAmount`：第一次应用会走附魔正常应用逻辑，后续同类型应用会合并到锚点实例。后续合并时需要执行的副作用应放在 `OnMergedDelta`，不要依赖 `OnEnchant` 再次执行。

```csharp
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

## 推荐实践与陷阱

1. **不要把“不活跃”和“被移除”混为一谈**  
   `WhenActive` 条件不满足时，附魔仍然存在；它只是暂时不活跃。

2. **修改外部状态时要考虑清理**  
   如果 `OnApplied` 改了卡牌费用、关键词、外部缓存等状态，通常要在 `OnRemoved` 中还原或清理。

3. **按 `RemovalReason` 分支处理**  
   手动移除、战斗结束、次数耗尽、卡牌清空可能需要不同逻辑。

4. **回合限制和次数限制不要混用概念**  
   持续 N 回合用 `LingerForTurns`；触发 N 次后消失用 `MaxActivations`。

5. **避免重复清理**  
   不要在 `OnTurnEnd` 和 `OnRemoved` 中重复撤销同一个状态，除非你的清理逻辑是幂等的。

6. **veto 要谨慎**  
   `OnRemoved` 返回 `false` 会阻止移除。只在确实需要”保护一次””条件不允许移除”时使用，并确保不会让临时附魔永久残留。

7. **优先使用 lifecycle 回调，而非重写 EnchantmentModel 虚方法**  
   直接重写 `EnchantmentModel.AfterCardPlayed()` 等虚方法不经过 `IsActive` 守门 — 失活附魔仍会被调用。用 `OnCardPlayed` 等 lifecycle 回调代替，它们自动受 scope / `WhenActive` 条件控制。

8. **注意 `OnCardEnteredCombat` 与 `OnCombatStart` 的区别**  
   `OnCombatStart` 每场战斗每张卡只触发一次（含后来通过 Astrolabe 等加入的卡）。`OnCardEnteredCombat` 在卡每次进入战斗时都触发（包括战斗开始时的初始化扫描和中途生成）。前者用于”每场战斗初始化一次”，后者用于”每次进入战斗都做某事”。

9. **`OnSideTurnStart` 分发给所有卡**  
   与 `OnCardPlayed`（只分发给所在卡）不同，`OnSideTurnStart` / `OnBeforeAttack` / `OnAfterDamageReceived` 等战斗级回调会分发给**所有玩家的所有卡上的所有附魔**。在回调里注意用参数过滤（如 `side == CombatSide.Player`、`ctx.Target == card.Owner`），避免逻辑被重复执行。

10. **`DamageReceivedContext` / `BlockGainContext` 是 record 类型**  
    可以用 positional destructuring 解构参数：`var (target, result, dealer, source) = ctx;`

## FAQ

### 为什么条件不满足但附魔还在？

因为 `WhenActive` / `ConditionalActive` 只控制是否活跃，不控制是否存在。它适合做“满足条件才生效”的开关。如果你希望附魔消失，请使用 `UntilTurnEnds`、`UntilCombatEnds`、`LingerForTurns`、`MaxActivations` 或主动调用移除 API。

### 为什么达到次数后没有消失？

先检查是否注册了 `MaxActivations`，以及 `ActivationTrigger` 是否对应你期望的事件。其次检查 `OnRemoved` 是否返回了 `false` veto 了 `ActivationLimitReached` 移除。

### `UntilTurnEnds`、`LingerForTurns`、`MaxActivations` 怎么选？

- 只持续当前回合：`UntilTurnEnds`
- 持续多个回合：`LingerForTurns(N)`
- 触发固定次数：`MaxActivations(N, trigger)`

### 移除时怎么知道原因？

在 `OnRemoved` 的第三个参数里读取：

```csharp
protected override bool OnRemoved(CardModel card, MyEnchant enchantment, RemovalReason reason)
{
    if (reason == RemovalReason.CombatEnded)
    {
        // 战斗结束清理
    }

    return true;
}
```

### 为什么 `OnApplied` 没在读档后执行？

`OnApplied` 表示新附魔成功附加时的回调，不是恢复回调。存档恢复和克隆路径会保留运行时状态，不应依赖 `OnApplied` 重新初始化恢复数据。如果你需要在读档/对端 packet 解码后重建运行时缓存（例如 `ConditionalWeakTable<CardModel, T>` 里的数据），改用 `OnRestored(card, enchantment)`。它在反序列化路径上触发一次，但永远不在新附魔挂上时触发。

### `OnRestored` 是否能收到所有附魔？

不一定。`OnRestored` 在 `NormalizeCardEnchantmentStacks` **之后**才 dispatch，所以反序列化后立即被归并 / 去重 / 移除的附魔不会收到这个回调。如果你重建的运行时缓存需要覆盖所有"原始"附魔状态，请考虑用 enchantment 自身的 `Props.strings` 序列化关键数据（与 mod 内部的 `ScopeStateSavePropertyName` 同一通道），让数据跟着 enchantment 自身走。

### `RemoveWhen` 的 predicate 在读档前后行为一致吗？

**不一定 — 这是设计权衡。** 出于安全考虑，`RemoveWhenScope`、`ConditionalActiveScope` 的 `Func<>` predicate **不被序列化**，读档 / packet 解码时由 registry 重新从注册回填。因此：

1. **predicate 必须是纯函数** —— 只依赖参数 `(card, enchantment)`，不引用 mod 之外的静态状态、不读取 `DateTime.Now`、不依赖战斗外可变全局变量。否则同一存档在不同时间打开可能产生不同结果。
2. predicate 闭包捕获的局部变量也会丢失（因为它来自 registry 注册时的 lambda），但运行期生效的状态可以通过 `enchantment.Props` / `enchantment.SavedProperties` 持久化。
3. 多人模式下双方都从各自的 registry 解析 scope，确保双方 mod 注册一致是 modder 的责任。

### `MaxActivations` / `LingerForTurns` 计数器读档后会重置吗？

不会。从 Phase 0 起，`ActivationCount` 和 `TurnsRemaining` 会序列化到 enchantment 自身的 `Props.strings["MultiEnchantmentScopeData"]`，存档和多人 packet 都会带过去。如果你看到计数器看起来"重置了"，可能是：

- JSON 解析失败（mod 日志会有 `Failed to restore scope state for ... falling back to defaults` 的 warn）。检查存档完整性。
- 该 enchantment 不在 registry 里 / scope 解析回 `Permanent`（也会 log warn）。
- 你正在测试一份**新**装备 — 上次没保存就被替换掉了。

### 我应该重写 `EnchantmentModel.AfterCardPlayed` 还是用 `OnCardPlayed`？

**用 `OnCardPlayed`。** 重写 `EnchantmentModel` 虚方法是 vanilla 的做法，但 mod 的 lifecycle 系统不会对虚方法调用做 `IsActive` 检查 — 失活的附魔照样会被调用。lifecycle 回调（`OnCardPlayed`、`OnCardExhausted` 等）是经过 scope / `WhenActive` 守门的安全通道。

例外情况：如果你需要修改 vanilla 的**返回值**（如 `ModifyDamageAdditive`），仍需重写虚方法 — lifecycle 回调目前只有 `OnShouldDie` 支持返回值。

### `OnSideTurnStart` 和 `OnTurnStart` 有什么区别？

`OnTurnStart` 仅在玩家回合开始时触发，`OnSideTurnStart(side)` 对玩家和敌方回合都触发。如果你只关心玩家回合，用 `OnTurnStart` 即可；需要响应敌方回合时用 `OnSideTurnStart` 并检查 `side == CombatSide.Enemy`。

### `OnCardEnteredCombat` 和 `OnCombatStart` 有什么区别？

- `OnCombatStart`：每场战斗每张卡触发一次。包括战斗开始时已有的卡和中途加入的新卡（通过 `Hook.AfterCardEnteredCombat` 补触发）。适合"初始化一次"的场景。
- `OnCardEnteredCombat`：卡每次进入战斗都触发（包括初始 deck setup 和 Astrolabe / Madness / Forge 等生成）。适合"每次进入都做某事"的场景。

### `OnAfterDamageReceived` 会分发给哪些附魔？

所有属于**受伤方**（`target` 的 owner）的卡上的所有活跃附魔。不是只分发给某张卡 — 而是该玩家所有战斗中的卡。在 handler 中用 `ctx.Target`、`ctx.Dealer`、`ctx.Source` 精确过滤你关心的伤害来源。

### `Custom("id")` 怎么用？

`ActivationTrigger.Custom("mymod:xxx")` 让你定义自定义触发事件。在你自己的 Harmony patch / hook 里调用：

```csharp
MultiEnchantmentScopeSupport.NoteActivation(enchantment, ActivationTrigger.Custom("mymod:OnRelicTriggered"));
```

Custom trigger 与 `MaxActivations` / `RemoveWhen` 配合使用，实现"自定义事件触发 N 次后消耗"或"自定义条件满足时移除"。结果会被缓存，同一 `identifier` 每次返回同一个实例。

## 广播事件与同卡邻居事件

v2 缺口完善轮新增了两类**显式 opt-in** 的事件钩子，覆盖此前缺失的"非自卡事件"和"同卡协同"用例。

### `OnAnyCardPlayed` / `OnAnyCardDrawn` / `OnAnyCardExhausted` / `OnAnyCardDiscarded`

这是 per-card 版（`OnCardPlayed` 等）的广播兄弟：战斗中**任何卡**触发对应事件时，本附魔的回调都会被调用，无论事件卡是否就是持有者的卡。

签名：`Action<CardModel /*事件卡*/, CardModel /*selfCard*/, EnchantmentModel /*self*/>`。`selfCard` 始终等于 `self.Card`，闭包内可直接取用。

```csharp
public sealed class TempoTrackerDefinition : EnchantmentDefinition<TempoTracker>
{
    protected override void OnAnyCardPlayed(CardModel played, CardModel self, TempoTracker e)
    {
        // 包括 played == self 的情形——广播刻意不去重，方便统计"已打出张数"。
        e.CardsPlayedThisCombat++;
    }
}
```

适用：

- 跨卡联动（例如"每次任意卡被打出时，对随机敌人造成 1 点伤害"）。
- 战斗节奏统计（已抽张数、已弃张数等计数器）。
- 一次性套牌效果（"本场打出第 N 张卡时触发一次"）。

注意：

- **必须显式 override / 调用 fluent 方法**才会订阅；不订阅时零开销，永远不进入广播派发表。
- 接收者一律受 `IsActive` 过滤——休眠的 `ConditionalActive` / `WhenActive` 附魔不会收到广播。
- 与 per-card 版互不影响：可以同时 override 两边，框架会分别派发。

### `OnSiblingApplied` / `OnSiblingRemoved`

同卡邻居事件，用于附魔与附魔之间的局部联动，无需通过全局注册表查找。

签名：

```csharp
OnSiblingApplied(Action<CardModel, EnchantmentModel /*self*/, EnchantmentModel /*newSibling*/>)
OnSiblingRemoved(Action<CardModel, EnchantmentModel /*self*/, EnchantmentModel /*removedSibling*/, RemovalReason>)
```

时序：

- `OnSiblingApplied` 在新邻居**已挂载**之后触发，回调内调用 `MultiEnchantmentApi.GetSiblings(card)` 能立即看到它。
- `OnSiblingRemoved` 在邻居**即将取下**之前触发，且**仅在** `OnRemoved` veto 链放行后；被否决的移除不会广播。
- **不会自激**：自身的 `OnApplied` / `OnRemoved` 不会反向作为兄弟事件回调到自己。
- 同样受 `IsActive` 过滤。

`OnApplied` 触发时**不会**自动收到已存在邻居的"补发" `OnSiblingApplied`——如果需要冷启动配合现有邻居，请在 `OnApplied` 内手动遍历 `MultiEnchantmentApi.GetSiblings(card, self)` 做一次种子计算（参见 `Sample 20 — SiblingAwareCombo`）。

### 何时调用 `MultiEnchantmentApi.NotifyPropsChanged(self)`

回调里改写了影响表现的字段（驱动 `FormatExtraText` / `VisualSlices` / `[ModifyDynamicVar]` 输出的私有字段）后，需要显式调用：

```csharp
MultiEnchantmentApi.NotifyPropsChanged(self);
```

来让框架重算派生缓存——否则 UI 会停留在最近一次"应用路径"事件（`OnApplied` / `OnMergedDelta` / `OnMergedRefresh` / 升级 / 替换）的快照值。应用路径事件本身已经自动刷新，不必额外调用。

参见 `Sample 22 — PropsChangeRefresh` 演示 `OnAfterDamageReceived` + `NotifyPropsChanged` 的"派生 UI 实时刷新"模式。

## 增量钩子（API v2 缺口完善轮）

下列钩子在 v2 缺口完善轮新增，全部为**纯加法**——已有附魔无需修改即可继续运行。

### 广播版卡牌事件 `OnAnyCard*`

per-card 版的 `OnCardPlayed/Drawn/Exhausted/Discarded` 仅在持有附魔的卡参与事件时触发。如果你需要在战斗中**任意一张**卡发生事件时响应（例如统计回合内打牌数、监听对方关键牌），用对应的广播版本：

| Hook | 触发时机 | 签名 |
| --- | --- | --- |
| `OnAnyCardPlayed` | 任何卡被打出后 | `(playedCard, selfCard, self)` |
| `OnAnyCardDrawn` | 任何卡被抽到后 | `(drawnCard, selfCard, self)` |
| `OnAnyCardExhausted` | 任何卡被消耗后 | `(exhaustedCard, selfCard, self)` |
| `OnAnyCardDiscarded` | 任何卡被弃掉后 | `(discardedCard, selfCard, self)` |

注意：

- **必须显式 opt-in**：仅当你 override `EnchantmentDefinition<T>.OnAnyCardPlayed` 或调 `IEnchantmentRegistration.OnAnyCardPlayed(...)` 才会注册。默认零开销。
- 受 `IsActive` 过滤——休眠 / `ConditionalActive` 失败的附魔不会收到。
- `selfCard` 永远等于 `self.Card`，方便闭包内取用。
- 自身的卡也会被广播——若不需要，比对 `playedCard == selfCard` 自行短路。

参见 [Sample 19 — OnAnyCardPlayedBroadcast](../MultiEnchantmentMod.Samples/Samples/19_OnAnyCardPlayedBroadcast.cs)。

### 同卡邻居事件 `OnSiblingApplied` / `OnSiblingRemoved`

让两个附魔在同一张卡上**互知**：

```csharp
.OnSiblingApplied<Self>((card, self, newSibling) => { /* 邻居入场 */ })
.OnSiblingRemoved<Self>((card, self, leftSibling, reason) => { /* 邻居离场 */ })
```

时序保证：

- `OnSiblingApplied` 在新邻居**已挂载**之后触发。回调内 `MultiEnchantmentApi.GetSiblings(card)` 能立刻看到它。
- `OnSiblingRemoved` 在邻居**即将被取下**之前触发，但**只在** `OnRemoved` veto 链放行后才发——被否决的移除不会广播。
- 不会自激：自身实例的 attach / detach 不会通过该钩子回送给自己。
- 受 `IsActive` 过滤。

适用场景：组合附魔（"如果同卡还有 X 就额外加 Y"）、互斥附魔（"邻位上 Curse 就自我移除"）、依赖邻居数量重算的 UI 附魔。

参见 [Sample 20 — SiblingAwareCombo](../MultiEnchantmentMod.Samples/Samples/20_SiblingAwareCombo.cs)。

### 显式刷新派生状态：`MultiEnchantmentApi.NotifyPropsChanged`

应用路径上的回调（`OnApplied` / `OnMergedDelta` / `OnMergedRefresh` / 移除）框架自动重算 visual slices / extra-card-text / dynamic-var 缓存。但**非应用路径**回调（`OnAfterDamageReceived` / `OnTurnEnd` / `OnAnyCard*` 等）改写附魔字段后，UI 不会自动刷新。

显式触发：

```csharp
.OnAfterDamageReceived<Self>((card, self, ctx) =>
{
    self.SomeCachedField = Recompute(ctx);
    MultiEnchantmentApi.NotifyPropsChanged(self);
})
```

何时**不需要**：在 `OnApplied` / `OnMergedDelta` 等应用路径内修改字段——框架自带刷新。重复调用是 idempotent 的，仅多一次重算；不会导致死循环。

参见 [Sample 22 — PropsChangeRefresh](../MultiEnchantmentMod.Samples/Samples/22_PropsChangeRefresh.cs)。

### 在表现钩子里读 Scope 状态：`EnchantmentStackSnapshot.ScopeStates`

`EnchantmentStackSnapshot` 现在多了一个可选字段 `ScopeStates`，把每个实例的 `ScopeRuntimeStateView`（`ActivationCount` / `TurnsRemaining` / `IsLimitReached` / `IsExpired`）做了不可变快照。

在 `FormatExtraText` / `VisualSlices` / `[ModifyDynamicVar]` 内通过 `snapshot.StateOf(snapshot.AnchorInstance)` 读取，最常见的用法是把"剩余 N 次"渲染进 tooltip：

```csharp
.FormatExtraText((EnchantmentStackSnapshot snapshot, string defaultText, out string formatted) =>
{
    ScopeRuntimeStateView? view = snapshot.StateOf(snapshot.AnchorInstance);
    int remaining = view?.Scope is EnchantmentScope.MaxActivationsScope max
        ? Math.Max(0, max.Max - view.ActivationCount)
        : 0;
    formatted = $"剩余 {remaining} 次释放。";
    return true;
})
```

权衡：`ScopeStates` 是构造 snapshot 那一刻的快照，回调结束之后 scope 状态再变化不会反映——下次渲染会拿到新的 snapshot。

参见 [Sample 21 — ScopeStateInPresentation](../MultiEnchantmentMod.Samples/Samples/21_ScopeStateInPresentation.cs)。

### 高级查询 API

`MultiEnchantmentApi` 上新增三个 `[EditorBrowsable(Advanced)]` 静态方法，主要面向调试 / 工具脚本：

- `GetScopeState(EnchantmentModel)` → `ScopeRuntimeStateView?`，无 scope 时为 `null`。
- `IsActive(EnchantmentModel)` → 当前 IsActive 求值结果（综合 ConditionalActive + scope 限制）。
- `GetSiblings(CardModel?, EnchantmentModel? excludingSelf = null)` → 同卡所有附魔。

普通玩法路径仍走 snapshot / 内部回调；这些是给"读得多于写"的高阶组合留的口子。

### 实例数量上限的覆盖策略：`StackOverflowPolicy`

之前 `StackDefinition.MaxInstances` 触顶时只能拒绝。现在新增 `OnOverflow` 字段，可指定 FIFO / LIFO 替换：

```csharp
StackDefinition def = new(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
{
    MaxInstances = 5,
    OnOverflow = StackOverflowPolicy.ReplaceOldest,
};

MultiEnchantmentApi.Register<MyAura>().Stack(def).Commit();
```

被驱逐的实例会走完整的 `OnRemoved` 流程，`reason = RemovalReason.OverflowEvicted`，可以与"自然移除"区分对待。

参见 [Sample 23 — StackOverflowReplace](../MultiEnchantmentMod.Samples/Samples/23_StackOverflowReplace.cs)。

### 单实例 Scope 覆盖：`Enchant(..., scopeOverride)` / `SetScopeOverride`

注册时的 `WithScope(...)` / `EnchantmentDefinition<T>.Scope` 仍是默认来源，但具体实例现在可以有覆盖值；运行时有效 scope 的优先级是：

```csharp
effectiveScope = OverrideScope ?? registryScope;
```

应用时覆盖：

```csharp
MultiEnchantmentApi.Enchant(
    card,
    new MyEnchant(),
    amount: 1,
    scopeOverride: EnchantmentScope.UntilCombatEnds);
```

追溯修改 / 清除：

```csharp
MultiEnchantmentApi.SetScopeOverride(card, enchantment, EnchantmentScope.UntilTurnEnds);
MultiEnchantmentApi.SetScopeOverride(card, enchantment, null); // 回到注册默认
```

允许的覆盖 scope 仅限可持久化、无谓词类型：`Permanent`、`UntilCombatEnds`、`UntilTurnEnds`、`LingerForTurns(N)`、`MaxActivations(N, trigger)`。`ConditionalActive` / `RemoveWhen` 会被拒绝（返回 `null` / `false` 并写 warn），因为它们携带无法序列化的谓词。

`SetScopeOverride` 改成 `LingerForTurns(N)` 时会重置 `TurnsRemaining = N`；改成 `MaxActivations` 时会重置 `ActivationCount = 0`；其它变更和清除覆盖保留已有计数。

`ScopeRuntimeStateView.HasOverride` 可用于 tooltip / debug UI 显示该实例是否使用了覆盖 scope。覆盖值会随 `MultiEnchantmentScopeData` 保存 / 联机同步。

参见 [Sample 24 — PerInstanceScope](../MultiEnchantmentMod.Samples/Samples/24_PerInstanceScope.cs)。

## 在 lifecycle handler 里调用 mutating API（安全准则）

### TL;DR

从 v2.1（2026-05-23 build）起，**任何 lifecycle handler 都可以放心调用 `MultiEnchantmentApi.RemoveEnchantment` / `Enchant` / `SetScopeOverride` / `CardCmd.*` 等会修改"附魔列表"或"战斗中卡牌集合"的 API**——框架内的迭代点全部使用快照（`.ToList()`）保护，不会再触发 `InvalidOperationException: Collection was modified during enumeration`。

### 为什么这一节存在

这个 mod 是 framework：用户的 handler 通过 `SafeInvoker` 在框架自己的 `foreach` 循环里运行。如果 handler 修改了 *正在被迭代* 的集合，C# 的 `List<>` 枚举器会抛 `InvalidOperationException`。

历史上出现过这样的 bug（参见 issue #3）：

```csharp
// 用户代码 —— 这个用法 *看上去* 应该工作
public override async Task AfterCardChangedPiles(CardModel card, PileType oldPileType, AbstractModel? source)
{
    if (card.Pile?.Type == PileType.Discard)
    {
        MultiEnchantmentApi.RemoveEnchantment(card, this); // 💥 v2.0 在多个附魔同时挂卡时崩
    }
}
```

崩溃的根因是 vanilla `Hook.AfterCardChangedPiles` 通过 `combatState.IterateHookListeners()` 遍历所有 hook listener，而 mod 的 patch 用 `yield return` 把 extra enchantments 注入这条迭代——没有快照。第一个附魔调 `RemoveEnchantment` 修改了底层 `List<>`，第二个附魔的迭代就崩了。

v2.1 修复后：所有这类入口都加了 `.ToList()` 快照。**对你（用户）的可见行为变化**：

| Handler 场景 | v2.0 行为 | v2.1 行为 |
| --- | --- | --- |
| 在 `OnCardChangedPiles` 调 `RemoveEnchantment(card, this)` | ⚠️ 单个附魔安全，多个同卡附魔同时调会崩 | ✅ 总是安全 |
| 在 `OnTurnStart` / `OnTurnEnd` 调 `CardCmd.AddCardToHand` 添新卡 | ⚠️ 可能崩 `AllCards` 迭代器 | ✅ 安全 |
| 在 vanilla `RecalculateValues` override 里调 mod mutating API | ⚠️ 可能崩 | ✅ 安全 |
| 在 `OnApplied` / `OnRemoved` 调 `Enchant` 给同卡加新附魔 | ✅ 一直安全（dispatch 早有快照） | ✅ 安全 |
| 在 `OnAnyCardPlayed` 调 `RemoveEnchantment(otherCard, ...)` | ✅ 一直安全 | ✅ 安全 |

### 仍然不要做的事

即使所有 dispatch 都有快照保护，下面这两类操作仍然属于"不要做"：

1. **不要在 handler 里抛异常**——`SafeInvoker` 会捕获并打 throttle 后的 log（前 3 次详细，后 47 次摘要，然后静默到本场战斗结束）。靠异常做控制流会同时污染日志和搞乱 throttle 计数器。用早 return / `OnRemoved` veto 返回 `false` 等显式机制代替。

2. **不要在 handler 里同步等待用户输入**——所有 lifecycle 都是同步分发的。如果你需要"等用户选择"，把工作丢到 `CardCmd.SelectCard*` 或类似异步 API 里去，不要在 handler 内 `Task.Wait()` / `.GetAwaiter().GetResult()`，会死锁。

3. **避免无限递归**：你在 `OnApplied` 里调 `Enchant` 给同卡加 *同一种* 附魔，又触发 `OnApplied`……快照只防迭代器崩，不防栈溢出。用 scope / `WhenActive` / 显式状态位避免重入。

### 写 handler 时的实用 checklist

- [ ] handler 里只读 vanilla 状态（`card.Pile`、`combatState.Enemies` 等），用 `MultiEnchantmentApi.*` 写状态——这是 v2 的推荐边界。
- [ ] 如果要修改 sibling，**优先用 `OnSiblingApplied` / `OnSiblingRemoved`**，不要在 `OnApplied` 里 `GetSiblings()` 然后批量操作（v2.1 之后两者都安全，但语义更清晰）。
- [ ] 长任务（动画 / 网络 / await）放进 `CardCmd.*` 或 `Cmd.Custom(...)`，不要在 lifecycle handler 内部 await。
- [ ] 涉及多卡的批量操作（Madness 风格效果），可以用 `MultiEnchantmentApi.GetSiblings` / `Snapshots.ForCard` 等只读 API 收集目标，再统一调 mutating API——清晰且 idempotent。

## 附录：Context Record 参考

### DamageReceivedContext

```csharp
public sealed record DamageReceivedContext(
    Creature Target,       // 受伤的生物
    DamageResult Result,   // 伤害结果（含格挡/未格挡/溢出伤害等）
    Creature? Dealer,      // 造成伤害的生物（状态伤害可能为 null）
    CardModel? Source);    // 造成伤害的卡牌（遗物/特性伤害可能为 null）
```

### BlockGainContext

```csharp
public sealed record BlockGainContext(
    Creature Creature,     // 获得格挡的生物
    decimal Amount,        // 格挡数量
    CardModel? Source);    // 来源卡牌（可能为 null）
```

## 延伸阅读

- v2 总览：`docs/v2-api-wiki.md`
- 生命周期示例：`MultiEnchantmentMod.Samples/Samples/08_UntilCombatEndsScope.cs` 到 `13_LifecycleAndVetoHooks.cs`
- API 类型签名：`Api/IEnchantmentRegistration.cs`（接口 + 强类型扩展方法）
- Definition 基类：`Api/EnchantmentDefinition.cs`（`protected virtual` 回调列表）

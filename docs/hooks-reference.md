# Slay the Spire 2 — 完整钩子参考文档

> 本文档系统性地记录了 Slay the Spire 2 中所有钩子（Hooks），覆盖卡牌、能力、遗物、战斗、生物、药水、房间等全部游戏系统。  
> 所有示例名称和效果描述均来自游戏本地化文件（中文）。

> **当前校准：** 本文档已按本仓库当前目标游戏 DLL（**v0.107.1**，commit `59260271`，2026-06-18 本地安装）复核关键签名。许多 `Hook` 静态方法的战斗状态参数公开为 `ICombatState`；下文示例里的 `CombatState` 若出现在卡牌/生物实例属性上，表示运行时具体状态对象。写 Harmony patch 时必须匹配 `Hook` 的 `ICombatState` 签名。代码块内的 `// Hook.cs:NNNN` / `// AbstractModel.cs:NNN` 行号注释为旧版反编译行号，可能已偏移，仅作定位提示，勿当作 v0.107.1 精确行号。

> **0.107.x 迁移重点（自 0.106.x 起仍生效）：** `Hook.BeforeSideTurnStart` / `Hook.AfterSideTurnStart` 带 `IReadOnlyList<Creature> participants`；`Hook.BeforeTurnEnd` / `Hook.AfterTurnEnd` 带 `IEnumerable<Creature> participants`，并分发到 `AbstractModel.BeforeSideTurnEnd*` / `AfterSideTurnEnd*`。`EnchantmentModel.EnchantBlockAdditive` / `EnchantBlockMultiplicative` 的 `ValueProp` 参数已移除。
>
> **v0.107.1 签名更正：** HP 损失修改已统一为单一静态入口 `Hook.ModifyHpLost(..., HpLossHookPhase phases, ...)`——**不存在** `Hook.ModifyHpLostBeforeOsty` / `Hook.ModifyHpLostAfterOsty` 静态分发器（`BeforeOsty`/`AfterOsty` 仅作为 `AbstractModel` 虚方法存在）。`Hook.ModifyOrbValue` 与 `AbstractModel.ModifyOrbValue` 无 `Player` 参数（按 `OrbModel orb` 区分）。`Hook.ModifyOrbPassiveTriggerCount`（单数）带 `out List<AbstractModel> modifyingModels`，对应虚方法为 `ModifyOrbPassiveTriggerCounts`（复数）。`ShouldGainGold` 与 `AfterCardRetained` 已被移除（金币无 Should 守卫，仅 `ShouldGainStars`）。

---

## 目录

### 第一部分：卡牌系统
1. [核心卡牌生命周期钩子](#1-核心卡牌生命周期钩子) — `OnPlay`, `OnUpgrade`, `AfterCreated`, `OnEnqueuePlayVfx`
2. [卡牌事件钩子 — 进入战斗与打出](#2-卡牌事件钩子--进入战斗与打出) — `AfterCardEnteredCombat`, `BeforeCardPlayed`, `AfterCardPlayed`, `AfterCardPlayedLate`
3. [卡牌事件钩子 — 抽牌、弃置、消耗](#3-卡牌事件钩子--抽牌弃置消耗) — `AfterCardDrawn`, `AfterCardDiscarded`, `AfterCardExhausted`
4. [卡牌堆与状态变化钩子](#4-卡牌堆与状态变化钩子) — `AfterCardChangedPiles`, `AfterCardRetained`（已移除）, `BeforeCardRemoved`, `AfterCardGeneratedForCombat`
5. [攻击与格挡钩子（卡牌相关）](#5-攻击与格挡钩子卡牌相关) — `BeforeAttack`, `AfterAttack`, `BeforeBlockGained`, `AfterBlockGained`
6. [回合结束与特殊钩子](#6-回合结束与特殊钩子) — `OnTurnEndInHand`, `AfterTransformed`, `AfterForged`, `AfterCloned`, `GetResultPileType`, `BeforeCardAutoPlayed`
7. [Mod 系统卡牌修改器钩子](#7-mod-系统卡牌修改器钩子) — `AbstractCardModifier` + `CardModifierManager`
8. [CardModel C# 事件](#8-cardmodel-c-事件) — 10 个 `event Action`

### 第二部分：数值与守卫系统
9. [数值修改钩子](#9-数值修改钩子) — `ModifyDamage`, `ModifyBlock`, `ModifyEnergyCostInCombat`, `ModifyCardPlayCount`, `ModifyXValue` 等
10. [守卫与条件钩子](#10-守卫与条件钩子) — `ShouldPlay`, `ShouldAddToDeck`, `ShouldPlayerResetEnergy` 等
11. [修改后通知钩子](#11-修改后通知钩子) — `AfterModifyingDamageAmount`, `AfterEnergyReset`, `AfterEnergySpent` 等
12. [HP/伤害相关修改钩子](#12-hp伤害相关修改钩子) — `ModifyHpLostBeforeOsty`, `ModifyHpLostAfterOsty`, `ModifyHpLost`, `ModifyUnblockedDamageTarget`, `ModifyEnergyGain`, `ModifyMaxEnergy`
13. [能力/充能球/能量相关钩子](#13-能力充能球能量相关钩子) — `ModifyPowerAmountGiven`, `ModifyPowerAmountReceived`, `ModifyOrbValue`, `ModifyOrbPassiveTriggerCounts`
14. [Should 守卫钩子详解](#14-should-守卫钩子详解) — `ShouldDie`/`ShouldDieLate`, `ShouldClearBlock`, `ShouldDraw`, `ShouldGainStars`, `ShouldEtherealTrigger`（`ShouldGainGold` 已移除 → `ModifyGoldGained`）
15. [高级卡牌模式与进阶技巧](#15-高级卡牌模式与进阶技巧) — `BeforeDamage` VFX、`IsPlayable`、`CardSelectCmd`、`ShouldGlowGold`、`CanBeGeneratedByModifiers`

### 第三部分：能力与遗物系统
16. [能力（Power）系统钩子](#16-能力power系统钩子) — `PowerModel` 生命周期、`PowerCmd` 工作流、能力类型与属性
17. [遗物（Relic）系统钩子](#17-遗物relic系统钩子) — `RelicModel` 生命周期、遗物池、获取/熔化/替换机制

### 第四部分：战斗与实体系统
18. [战斗生命周期钩子](#18-战斗生命周期钩子) — `CombatManager` 完整战斗循环、回合结构、钩子时序图
19. [生物（Creature）与死亡系统钩子](#19-生物creature与死亡系统钩子) — `Creature` 类型、死亡/格挡/伤害流水线
20. [药水（Potion）系统钩子](#20-药水potion系统钩子) — `PotionModel` 生命周期、药水槽机制

### 第五部分：地图与资源系统
21. [房间/事件/地图钩子](#21-房间事件地图钩子) — 休息处、商人、地图生成、事件钩子
22. [奖励与资源钩子](#22-奖励与资源钩子) — 金币、星能、宝箱、奖励修改

### 第六部分：总结
23. [钩子执行顺序与阶段总结](#23-钩子执行顺序与阶段总结) — 阶段模式、钩子类型对比、监听器收集规则

---

## 1. 核心卡牌生命周期钩子

这些钩子定义在 `CardModel` 基类中（`src/Core/Models/CardModel.cs`），是每张卡牌最基础的生命周期方法。

### 1.1 `OnPlay`

```csharp
// CardModel.cs:1289
protected virtual Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
```

**触发时机：** 卡牌的实际效果执行阶段。由 `OnPlayWrapper` 内部调用（第 1497 行），是所有卡牌效果的核心入口。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `choiceContext` | `PlayerChoiceContext` | 玩家选择上下文，包含当前模型栈、能量信息等 |
| `cardPlay` | `CardPlay` | 卡牌打出信息，包含目标（`Target`）、资源消耗（`Resources`）、是否自动打出（`IsAutoPlay`）等 |

**执行顺序：** 在 `BeforeCardPlayed` 钩子之后、`Enchantment.OnPlay` / `Affliction.OnPlay` 之前执行。

#### 示例：痛击（Bash）

- **中文名：** 痛击
- **英文名：** Bash
- **效果：** 造成 8 点伤害。给予 2 层易伤。
- **升级后：** 伤害 +2，易伤 +1

```csharp
// src/Core/Models/Cards/Bash.cs
public sealed class Bash : CardModel
{
    // 构造：2费 攻击牌 基础稀有度 可指定任意敌人
    public Bash() : base(2, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 1. 造成伤害
        await DamageCmd.Attack(base.DynamicVars.Damage.BaseValue)
            .FromCard(this)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);

        // 2. 施加易伤
        await PowerCmd.Apply<VulnerablePower>(
            cardPlay.Target,
            base.DynamicVars.Vulnerable.BaseValue,
            base.Owner.Creature, this);
    }
}
```

> **要点：** `OnPlay` 是异步方法（`async Task`），内部通过 `await` 执行命令（`DamageCmd`、`PowerCmd`）。命令系统负责处理动画、网络同步、历史记录等。

#### 示例：全身撞击（Body Slam）

- **中文名：** 全身撞击
- **英文名：** Body Slam
- **效果：** 造成你当前格挡值的伤害。
- **升级后：** 费用 -1

```csharp
// src/Core/Models/Cards/BodySlam.cs
public sealed class BodySlam : CardModel
{
    // 动态伤害变量：伤害 = 0 + (1 × 当前格挡值)
    protected override IEnumerable<DynamicVar> CanonicalVars => new[]
    {
        new CalculationBaseVar(0m),                      // 基础值
        new ExtraDamageVar(1m),                          // 额外附加值
        new CalculatedDamageVar(ValueProp.Move)           // 计算公式：格挡 × 1
            .WithMultiplier((card, _) => card.Owner.Creature.Block)
    };

    public BodySlam() : base(1, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await DamageCmd.Attack(base.DynamicVars.CalculatedDamage)  // 使用动态计算伤害
            .FromCard(this)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }
}
```

> **要点：** `BodySlam` 展示了**动态变量系统**的用法。`CalculatedDamageVar` 允许伤害值根据游戏状态实时计算。`CanonicalVars` 声明卡牌的所有数值变量，引擎自动将其渲染到卡牌描述中（`{CalculatedDamage:diff()}`）。

---

### 1.2 `OnUpgrade`

```csharp
// CardModel.cs:1299
protected virtual void OnUpgrade()
```

**触发时机：** 卡牌被升级时（如使用锻造、神化等效果）。是同步方法。

**说明：** 升级逻辑通常包括：
- 提升伤害/格挡值：`base.DynamicVars.Damage.UpgradeValueBy(N)`
- 降低费用：`base.EnergyCost.UpgradeBy(-N)`
- 修改关键词（如移除虚无）：升级后可能需要重新设置属性

#### 示例：痛击（Bash）的升级

```csharp
// Bash.cs:38
protected override void OnUpgrade()
{
    base.DynamicVars.Damage.UpgradeValueBy(2m);         // 伤害 +2
    base.DynamicVars.Vulnerable.UpgradeValueBy(1m);     // 易伤 +1
}
```

#### 示例：全身撞击（Body Slam）的升级

```csharp
// BodySlam.cs:38
protected override void OnUpgrade()
{
    base.EnergyCost.UpgradeBy(-1);  // 费用从 1 降为 0
}
```

> **要点：** `OnUpgrade` 不直接修改 `DynamicVars` 的 `BaseValue`，而是调用 `UpgradeValueBy()`，这使得升级效果可以被追踪且不会被重复应用。

---

### 1.3 `AfterCreated`

```csharp
// CardModel.cs:957
public virtual void AfterCreated()
```

**触发时机：** 卡牌实例被创建后立即调用。在构造函数完成后、卡牌加入牌库或进入战斗之前触发。

**用途：** 初始化卡牌的运行时状态（非规范属性），如设置计数器、标记等。

#### 示例：藏宝图（Spoils Map）

- **中文名：** 藏宝图
- **英文名：** Spoils Map
- **效果：** 在下一阶段的地图上，标记一个有 600 额外金币的地点。

```csharp
// src/Core/Models/Cards/SpoilsMap.cs
public sealed class SpoilsMap : CardModel
{
    private int _spoilsActIndex = -1;

    public SpoilsMap()
        : base(-1, CardType.Quest, CardRarity.Quest, TargetType.Self) { }

    public override void AfterCreated()
    {
        SpoilsActIndex = 1;  // 初始化任务阶段索引为 1
    }

    // 此卡还重写了 ModifyGeneratedMap、AfterMapGenerated、BeforeCardRemoved...
}
```

> **要点：** `AfterCreated` 用于在卡牌创建后立即设置可变状态。此卡在 `AfterCreated` 中将 `SpoilsActIndex` 设为 1，确保后续的地图修改钩子（`ModifyGeneratedMap`）能正确作用于第一阶段。

---

### 1.4 `OnEnqueuePlayVfx`

```csharp
// CardModel.cs:1294
public virtual Task OnEnqueuePlayVfx(Creature? target)
```

**触发时机：** 卡牌即将打出时，在执行实际效果之前，用于排队播放视觉特效。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `target` | `Creature?` | 卡牌目标生物，可为 null |

#### 示例：燃烧（Inflame）

- **中文名：** 燃烧
- **英文名：** Inflame
- **效果：** 获得 2 点力量。
- **升级后：** 力量 +1

```csharp
// src/Core/Models/Cards/Inflame.cs
public sealed class Inflame : CardModel
{
    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        NPowerUpVfx.CreateNormal(base.Owner.Creature);  // 力量上升粒子效果
        await PowerCmd.Apply<StrengthPower>(
            base.Owner.Creature,
            base.DynamicVars["StrengthPower"].BaseValue,
            base.Owner.Creature, this);
    }

    public override async Task OnEnqueuePlayVfx(Creature? target)
    {
        // 地面火焰特效 + 角色施法动画
        NCombatRoom.Instance?.CombatVfxContainer.AddChildSafely(
            NGroundFireVfx.Create(base.Owner.Creature));
        await CreatureCmd.TriggerAnim(base.Owner.Creature, "Cast",
            base.Owner.Character.CastAnimDelay);
    }

    protected override void OnUpgrade()
    {
        base.DynamicVars["StrengthPower"].UpgradeValueBy(1m);
    }
}
```

> **要点：** `OnEnqueuePlayVfx` 将视觉特效与逻辑分离。在 `OnPlay` 执行伤害/效果之前，引擎先调用此方法来播放前导动画。

---

### OnPlayWrapper — 完整打出流程

`OnPlayWrapper`（`CardModel.cs:1436`）是所有卡牌打出的统一入口，其执行顺序如下：

```
1. Hook.ModifyCardPlayResultPileTypeAndPosition   // 修改结果牌堆
2. AfterModifyingCardPlayResultPileOrPosition     // 通知修改完成
3. Hook.ModifyCardPlayCount                       // 修改重播次数
4. Hook.AfterModifyingCardPlayCount               // 通知修改完成
5. Hook.BeforeCardPlayed                          // 打出前钩子
6. History.CardPlayStarted                        // 记录开始
7. >>> OnPlay(choiceContext, cardPlay) <<<        // 卡牌实际效果
8. Enchantment.OnPlay(...) / Affliction.OnPlay()  // 附魔/苦难效果
9. History.CardPlayFinished                       // 记录结束
10. Hook.AfterCardPlayed                          // 打出后钩子
11. Hook.AfterCardPlayedLate                      // 打出后晚期钩子
12. CardModel.Played 事件触发
```

---

## 2. 卡牌事件钩子 — 进入战斗与打出

这些钩子定义在 `AbstractModel` 基类中（`src/Core/Models/AbstractModel.cs`），由 `Hook` 静态调度器遍历所有监听模型时调用。任何游戏实体（遗物、能力、药水、其他卡牌等）都可以重写这些方法来响应卡牌事件。

`Hook.cs` 中的静态调度方法位于 `src/Core/Hooks/Hook.cs`，它们遍历所有 `AbstractModel` 监听器并调用对应的虚方法。

### 2.1 `AfterCardEnteredCombat`

```csharp
// Hook.cs:143 — 调度方法
public static async Task AfterCardEnteredCombat(ICombatState combatState, CardModel card)

// AbstractModel.cs:191 — 虚方法
public virtual Task AfterCardEnteredCombat(CardModel card) => Task.CompletedTask;
```

**触发时机：** 一张卡牌进入战斗时触发（包括战斗开始时已在牌库中的卡牌和战斗中途生成的卡牌）。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `card` | `CardModel` | 进入战斗的卡牌实例 |

**执行阶段：** 单阶段，无 Early/Late 区分。

**典型用法：** 卡牌在进入战斗时根据战斗历史调整自身费用或状态——这是实现"本回合中每打出过X张Y牌，费用降低"类效果的关键钩子。

#### 示例：女妖之嚎（Banshee's Cry）

- **中文名：** 女妖之嚎
- **英文名：** Banshee's Cry
- **效果：** 对所有敌人造成 33 点伤害。本场战斗中每打出过一张虚无牌，此牌的耗能就减少 2。
- **升级后：** 费用 -2

```csharp
// src/Core/Models/Cards/BansheesCry.cs
public sealed class BansheesCry : CardModel
{
    public BansheesCry() : base(9, CardType.Attack, CardRarity.Rare, TargetType.AllEnemies) { }

    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (card != this) return Task.CompletedTask;      // 只处理自己
        if (base.IsClone) return Task.CompletedTask;       // 克隆体不处理

        // 统计本场战斗中已打完的虚无牌数量
        int etherealCount = CombatManager.Instance.History.CardPlaysFinished
            .Count(e => e.WasEthereal && e.CardPlay.Card.Owner == base.Owner);

        // 每张虚无牌降低费用
        base.EnergyCost.AddThisCombat(-etherealCount * base.DynamicVars.Energy.IntValue);
        return Task.CompletedTask;
    }
}
```

> **关键模式：** `AfterCardEnteredCombat` 中通过 `CombatManager.Instance.History.CardPlaysFinished` 查询战斗历史，实现**回溯性**的费用调整——即使在此卡进入战斗之前打出的虚无牌也能降低其费用。

#### 示例：踩踏（Stomp）

- **中文名：** 踩踏
- **英文名：** Stomp
- **效果：** 对所有敌人造成 12 点伤害。你在本回合中每打出过一张攻击牌，其耗能减少 1。
- **升级后：** 伤害 +3

```csharp
// src/Core/Models/Cards/Stomp.cs
public sealed class Stomp : CardModel
{
    public Stomp() : base(3, CardType.Attack, CardRarity.Uncommon, TargetType.AllEnemies) { }

    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (card != this) return Task.CompletedTask;
        if (base.IsClone) return Task.CompletedTask;

        // 统计本回合已打完的攻击牌数量（只统计自己打出的）
        int amount = CombatManager.Instance.History.CardPlaysFinished
            .Count(e => e.CardPlay.Card.Type == CardType.Attack
                     && e.CardPlay.Card.Owner == base.Owner
                     && e.HappenedThisTurn(base.CombatState));

        ReduceCostBy(amount);
        return Task.CompletedTask;
    }

    private void ReduceCostBy(int amount)
    {
        base.EnergyCost.AddThisTurn(-amount);  // 本回合限定的费用降低
    }
}
```

> **关键区别：** Stomp 使用 `AddThisTurn(-amount)` 而 BansheesCry 使用 `AddThisCombat(-amount)`。前者费用降低仅在本回合有效，后者持续整场战斗。

---

### 2.2 `BeforeCardPlayed`

```csharp
// Hook.cs:172 — 调度方法
public static async Task BeforeCardPlayed(ICombatState combatState, CardPlay cardPlay)

// AbstractModel.cs:211 — 虚方法
public virtual Task BeforeCardPlayed(CardPlay cardPlay) => Task.CompletedTask;
```

**触发时机：** 在卡牌的 `OnPlay` 实际效果执行**之前**。在 `OnPlayWrapper` 中，位于费用消耗之后、`OnPlay` 之前。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `cardPlay` | `CardPlay` | 即将打出的卡牌信息，包含 `Card`、`Target`、`Resources` 等 |

**注意：** 此钩子**不提供** `PlayerChoiceContext`，因此不能执行需要上下文的效果，仅适用于状态修改（如改费用、改数值）。

#### 示例：踩踏（Stomp）的 BeforeCardPlayed

踩踏同时使用了 `AfterCardEnteredCombat` 和 `BeforeCardPlayed`，后者用于在每次有攻击牌打出时持续降低费用：

```csharp
// Stomp.cs:59
public override Task BeforeCardPlayed(CardPlay cardPlay)
{
    if (cardPlay.Card.Owner != base.Owner) return Task.CompletedTask;
    if (cardPlay.Card.Type != CardType.Attack) return Task.CompletedTask;

    // 每当有攻击牌即将打出时，降低此卡本回合费用
    ReduceCostBy(1);
    return Task.CompletedTask;
}
```

> **关键模式：** `AfterCardEnteredCombat` + `BeforeCardPlayed` 组合使用：前者处理进入战斗时已有的攻击牌计数，后者持续监听后续攻击牌打出。两者都通过 `HappenedThisTurn()` 确保只统计本回合。

---

### 2.3 `AfterCardPlayed`

```csharp
// Hook.cs:181 — 调度方法
public static async Task AfterCardPlayed(ICombatState combatState,
    PlayerChoiceContext choiceContext, CardPlay cardPlay)

// AbstractModel.cs:216 — 虚方法
public virtual Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    => Task.CompletedTask;
```

**触发时机：** 在卡牌的 `OnPlay` 执行完毕之后、`AfterCardPlayedLate` 之前。注意 `Hook.AfterCardPlayed` 实际上调度了 `AfterCardPlayed` **和** `AfterCardPlayedLate` 两个阶段。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `choiceContext` | `PlayerChoiceContext` | 包含当前模型栈的上下文 |
| `cardPlay` | `CardPlay` | 已打出的卡牌信息 |

#### 示例：女妖之嚎（Banshee's Cry）的 AfterCardPlayed

```csharp
// BansheesCry.cs:57
public override Task AfterCardPlayed(PlayerChoiceContext context, CardPlay cardPlay)
{
    if (cardPlay.Card.Owner != base.Owner) return Task.CompletedTask;
    if (!cardPlay.Card.Keywords.Contains(CardKeyword.Ethereal)) return Task.CompletedTask;

    // 每次有虚无牌被打出，降低此卡整场战斗费用
    base.EnergyCost.AddThisCombat(-base.DynamicVars.Energy.IntValue);
    return Task.CompletedTask;
}
```

#### 示例：精密瞄准（Pinpoint）

- **中文名：** 精密瞄准
- **英文名：** Pinpoint
- **效果：** 造成 15 点伤害。你在本回合中每打出过一张技能牌，其耗能减少 1。
- **升级后：** 伤害 +4

```csharp
// src/Core/Models/Cards/Pinpoint.cs
public sealed class Pinpoint : CardModel
{
    public Pinpoint() : base(3, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }

    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (card != this) return Task.CompletedTask;
        if (base.IsClone) return Task.CompletedTask;

        int amount = CombatManager.Instance.History.CardPlaysFinished
            .Count(e => e.CardPlay.Card.Type == CardType.Skill
                     && e.CardPlay.Card.Owner == base.Owner
                     && e.HappenedThisTurn(base.CombatState));
        ReduceCostBy(amount);
        return Task.CompletedTask;
    }

    public override Task AfterCardPlayed(PlayerChoiceContext context, CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner != base.Owner) return Task.CompletedTask;
        if (cardPlay.Card.Type != CardType.Skill) return Task.CompletedTask;
        ReduceCostBy(1);
        return Task.CompletedTask;
    }
}
```

> **模式对比：** Pinpoint 与 BansheesCry 的结构非常相似——`AfterCardEnteredCombat` 负责回溯计数，`AfterCardPlayed` 负责持续监听。区别在于 Pinpoint 按技能牌类型过滤且费用降低仅限本回合（`AddThisTurn`）。

---

### 2.4 `AfterCardPlayedLate`

```csharp
// AbstractModel.cs:221 — 虚方法
public virtual Task AfterCardPlayedLate(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    => Task.CompletedTask;
```

**触发时机：** 在 `AfterCardPlayed` **之后**执行。当需要在其他所有 `AfterCardPlayed` 处理完毕后再执行逻辑时使用。

**典型用法：** 条件回手——需要在其他卡牌的打出效果完全处理完毕后（如弃牌堆状态确定后）才触发。

#### 示例：得力助手（Right Hand Hand）

- **中文名：** 得力助手
- **英文名：** Right Hand Hand
- **效果：** 奥斯提造成 4 点伤害。每当你打出耗能为 2 或以上的牌，将此牌从弃牌堆放回你的手牌。
- **升级后：** 伤害 +2

```csharp
// src/Core/Models/Cards/RightHandHand.cs
public sealed class RightHandHand : CardModel
{
    public RightHandHand() : base(0, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }

    public override async Task AfterCardPlayedLate(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 检查是否是自己打出的牌
        if (cardPlay.Card.Owner == base.Owner
            && cardPlay.Resources.EnergyValue >= base.DynamicVars.Energy.IntValue)  // 能量消耗 >= 2
        {
            CardPile? pile = base.Pile;
            if (pile != null && pile.Type == PileType.Discard)  // 此卡在弃牌堆中
            {
                await CardPileCmd.Add(this, PileType.Hand);  // 将其移回手牌
            }
        }
    }
}
```

> **为什么用 Late 阶段？** 正常打出流程中，卡牌打出后会被移到弃牌堆。使用 `AfterCardPlayedLate` 确保在其他所有效果（包括卡牌移堆）完成后，再检查此卡是否在弃牌堆并移回手牌。如果使用普通 `AfterCardPlayed`，可能卡牌尚未被移入弃牌堆，导致条件判断失败。

---

## 3. 卡牌事件钩子 — 抽牌、弃置、消耗

### 3.1 `AfterCardDrawn` / `AfterCardDrawnEarly`

```csharp
// Hook.cs:125 — 调度方法
public static async Task AfterCardDrawn(ICombatState combatState,
    PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)

// AbstractModel.cs:181,186 — 虚方法
public virtual Task AfterCardDrawnEarly(PlayerChoiceContext choiceContext,
    CardModel card, bool fromHandDraw) => Task.CompletedTask;

public virtual Task AfterCardDrawn(PlayerChoiceContext choiceContext,
    CardModel card, bool fromHandDraw) => Task.CompletedTask;
```

**触发时机：** 一张卡牌被抽到手上后触发。`AfterCardDrawnEarly` 先于 `AfterCardDrawn` 执行。Hook 调度器会先遍历所有监听器的 Early 阶段，再遍历 Normal 阶段（见 `Hook.cs:127-140`）。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `choiceContext` | `PlayerChoiceContext` | 玩家选择上下文 |
| `card` | `CardModel` | 被抽到的卡牌 |
| `fromHandDraw` | `bool` | 是否来自"手牌抽牌"效果（如抽牌药水直接从手牌中抽牌） |

**区分 Early 和 Normal：** Early 阶段用于需要在其他抽牌效果之前执行的操作（如先扣除能量），Normal 阶段用于常规响应。

#### 示例：王者之拳（Kingly Punch）

- **中文名：** 王者之拳
- **英文名：** Kingly Punch
- **效果：** 造成 8 点伤害。每当你抽到这张牌时，在这场战斗中其伤害增加 4。
- **升级后：** 伤害 +2，增加值 +2

```csharp
// src/Core/Models/Cards/KinglyPunch.cs
public sealed class KinglyPunch : CardModel
{
    private decimal _extraDamage;

    public KinglyPunch() : base(1, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }

    public override Task AfterCardDrawn(PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        if (card != this) return Task.CompletedTask;  // 只响应自己被抽到

        // 每次抽到自身时，永久增加伤害
        decimal increase = base.DynamicVars["Increase"].BaseValue;
        base.DynamicVars.Damage.BaseValue += increase;
        ExtraDamage += increase;  // 记录额外增加量，用于降级时恢复
        return Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
        base.DynamicVars.Damage.UpgradeValueBy(2m);
        base.DynamicVars["Increase"].UpgradeValueBy(2m);
    }

    protected override void AfterDowngraded()
    {
        base.AfterDowngraded();
        base.DynamicVars.Damage.BaseValue += ExtraDamage;  // 降级时恢复抽牌增加的伤害
    }
}
```

> **关键模式：** `AfterCardDrawn` 中直接修改 `DynamicVars.Damage.BaseValue`，每次抽到自身时伤害递增。还通过 `AfterDowngraded` 确保降级时不丢失已累积的伤害加成。

#### 示例：虚空（Void）

- **中文名：** 虚空
- **英文名：** Void
- **效果：** 每当你抽到这张牌时，失去 1 点能量。不可打出，虚无。

```csharp
// src/Core/Models/Cards/Void.cs
public sealed class Void : CardModel
{
    public Void() : base(-1, CardType.Status, CardRarity.Status, TargetType.None) { }

    public override async Task AfterCardDrawn(PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        if (card == this)
        {
            await Cmd.Wait(0.25f);                                            // 等待动画
            await PlayerCmd.LoseEnergy(base.DynamicVars.Energy.IntValue,       // 失去 1 能量
                base.Owner);
        }
    }
}
```

> **状态牌模式：** 状态牌通过 `AfterCardDrawn` 对玩家施加负面效果。Void 在抽到时直接扣减能量，同时拥有 `Unplayable` + `Ethereal` 关键词防止玩家使用。

---

### 3.2 `AfterCardDiscarded`

```csharp
// Hook.cs:114 — 调度方法
public static async Task AfterCardDiscarded(ICombatState combatState,
    PlayerChoiceContext choiceContext, CardModel card)

// AbstractModel.cs:176 — 虚方法
public virtual Task AfterCardDiscarded(PlayerChoiceContext choiceContext,
    CardModel card) => Task.CompletedTask;
```

**触发时机：** 一张卡牌被弃置后触发。

**注意：** 此钩子在卡牌目录中**没有被任何卡牌直接重写**（卡牌通常不响应自己的弃置事件）。它主要用于遗物和能力来响应"弃牌"事件。

#### 示例：结实绷带（Tough Bandages）遗物

- **中文名：** 结实绷带
- **效果：** 你每在你的回合丢弃一张牌，就获得 3 点格挡。

```csharp
// src/Core/Models/Relics/ToughBandages.cs
public sealed class ToughBandages : RelicModel
{
    public override async Task AfterCardDiscarded(PlayerChoiceContext choiceContext, CardModel card)
    {
        if (card.Owner == base.Owner
            && base.Owner.Creature.Side == base.Owner.Creature.CombatState.CurrentSide)
        {
            Flash();  // 遗物闪光动画
            await CreatureCmd.GainBlock(base.Owner.Creature, base.DynamicVars.Block, null);
        }
    }
}
```

> **要点：** 遗物通过重写 `AfterCardDiscarded` 来响应弃牌事件。`Flash()` 是遗物的视觉提示方法，`Side` 检查确保在正确的战斗方（防止联机多方的混淆）。

---

### 3.3 `AfterCardExhausted`

```csharp
// Hook.cs:152 — 调度方法
public static async Task AfterCardExhausted(ICombatState combatState,
    PlayerChoiceContext choiceContext, CardModel card, bool causedByEthereal)

// AbstractModel.cs:201 — 虚方法
public virtual Task AfterCardExhausted(PlayerChoiceContext choiceContext,
    CardModel card, bool causedByEthereal) => Task.CompletedTask;
```

**触发时机：** 一张卡牌被消耗后触发。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `choiceContext` | `PlayerChoiceContext` | 玩家选择上下文 |
| `card` | `CardModel` | 被消耗的卡牌 |
| `causedByEthereal` | `bool` | 消耗是否由虚无关键词自动触发（而非主动消耗） |

#### 示例：无惧疼痛（Feel No Pain）能力

- **中文名：** 无惧疼痛
- **效果：** 每当有一张牌被消耗时，获得 3 点格挡。

```csharp
// src/Core/Models/Powers/FeelNoPainPower.cs
public sealed class FeelNoPainPower : PowerModel
{
    public override async Task AfterCardExhausted(PlayerChoiceContext choiceContext, CardModel card, bool _)
    {
        if (card.Owner.Creature == base.Owner)
        {
            await CreatureCmd.GainBlock(base.Owner, base.Amount, ValueProp.Unpowered, null);
        }
    }
}
```

> **要点：** `causedByEthereal` 参数允许区分消耗来源。FeelNoPain 忽略此参数（`_`），无论消耗是主动还是虚无触发都给予格挡。

---

## 4. 卡牌堆与状态变化钩子

### 4.1 `AfterCardChangedPiles` / `AfterCardChangedPilesLate`

```csharp
// Hook.cs:100 — 调度方法
public static async Task AfterCardChangedPiles(IRunState runState, ICombatState? combatState,
    CardModel card, PileType oldPile, AbstractModel? source)

// AbstractModel.cs:166,171 — 虚方法
public virtual Task AfterCardChangedPiles(CardModel card, PileType oldPileType,
    AbstractModel? source) => Task.CompletedTask;

public virtual Task AfterCardChangedPilesLate(CardModel card, PileType oldPileType,
    AbstractModel? source) => Task.CompletedTask;
```

**触发时机：** 一张卡牌从一个牌堆移动到另一个牌堆后触发。每次卡牌移动都经过此钩子，使其成为最频繁触发的卡牌事件之一。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `card` | `CardModel` | 移动的卡牌 |
| `oldPileType` | `PileType` | 卡牌原来所在的牌堆类型（`Draw`、`Hand`、`Discard`、`Exhaust`、`Play`、`None` 等） |
| `source` | `AbstractModel?` | 触发此次移动的来源（如遗物、卡牌效果、能力等），可为 null |

**执行顺序：** Normal 阶段先于 Late 阶段。所有监听器的 `AfterCardChangedPiles` 先被全部调用，再全部调用 `AfterCardChangedPilesLate`。

**关键判断模式：**
- `oldPileType == PileType.None` → 新创建的卡牌首次进入牌堆系统
- `oldPileType == PileType.Exhaust` → 从消耗堆返回（罕见）
- `card.Pile.Type == PileType.Exhaust` → 卡牌正在被消耗

#### 示例：君王之剑（Sovereign Blade）

- **中文名：** 君王之剑
- **英文名：** Sovereign Blade
- **效果：** 造成 10 点伤害（保留）。如果你有争锋效果，改为对所有敌人造成伤害。
- **升级后：** 费用 -1

```csharp
// src/Core/Models/Cards/SovereignBlade.cs
public sealed class SovereignBlade : CardModel
{
    public override Task AfterCardChangedPiles(CardModel card, PileType oldPileType, AbstractModel? source)
    {
        if (card != this) return Task.CompletedTask;

        // 新创建的卡牌 (PileType.None) 或从消耗堆返回 → 播放锻造特效
        if ((!CreatedThroughForge && oldPileType == PileType.None) || oldPileType == PileType.Exhaust)
        {
            ForgeCmd.PlayCombatRoomForgeVfx(base.Owner, this);
        }

        // 卡牌进入消耗堆 → 移除专属 VFX 节点
        if (card.Pile.Type == PileType.Exhaust)
        {
            RemoveSovereignBladeNode();
        }
        return Task.CompletedTask;
    }
}
```

> **要点：** SovereignBlade 使用 `AfterCardChangedPiles` 来追踪卡牌的位置变化，在不同牌堆状态触发不同的视觉效果。这是典型的"以位置变化驱动逻辑"的模式。

---

### 4.2 `AfterCardRetained`（v0.107.1 已移除）

> **已废弃，仅作历史参考。** 在 v0.107.1 中 `AfterCardRetained` **既无 `Hook` 静态分发器、也无 `AbstractModel` 虚方法**（全量 corpus 0 命中）。不要再覆写或为其写 Harmony patch。

**触发时机（旧）：** 回合结束时，一张卡牌因"保留"关键词而留在手牌中时触发。

**替代方案：** 需要响应保留事件时，使用现有回合结束 / flush 流程（`AfterFlush` 暴露了 `retainedCards`），或 MultiEnchantmentMod 的 `OnCardRetained` lifecycle 桥接。

---

### 4.3 `BeforeCardRemoved`

```csharp
// Hook.cs:199 — 调度方法
public static async Task BeforeCardRemoved(IRunState runState, CardModel card)

// AbstractModel.cs:316 — 虚方法
public virtual Task BeforeCardRemoved(CardModel card) => Task.CompletedTask;
```

**触发时机：** 一张卡牌即将从牌库中被**永久移除**时触发（如在商人处移除、被变形等）。注意这不是卡牌进入消耗堆，而是彻底离开玩家牌库。

#### 示例：藏宝图（Spoils Map）的 BeforeCardRemoved

```csharp
// SpoilsMap.cs:100
public override Task BeforeCardRemoved(CardModel card)
{
    if (card != this) return Task.CompletedTask;

    // 只有当前幕才需要清理地图任务标记
    if (SpoilsActIndex != base.Owner.RunState.CurrentActIndex) return Task.CompletedTask;
    if (!SpoilsCoord.HasValue) return Task.CompletedTask;

    // 从地图宝箱点移除此任务
    base.Owner.RunState.Map.GetPoint(SpoilsCoord.Value)?.RemoveQuest(this);
    return Task.CompletedTask;
}
```

> **要点：** BeforeCardRemoved 用于在卡牌被移除前清理关联状态。藏宝图在此清理其在地图上的任务标记，防止遗留在不可达的地图点上。

---

### 4.4 `AfterCardGeneratedForCombat`

```csharp
// Hook.cs:163 — 调度方法
public static async Task AfterCardGeneratedForCombat(ICombatState combatState,
    CardModel card, Player? creator)

// AbstractModel.cs:196 — 虚方法
public virtual Task AfterCardGeneratedForCombat(CardModel card, Player? creator)
    => Task.CompletedTask;
```

**触发时机：** 一张卡牌在战斗中被生成时触发（如由药水、卡牌效果或能力创造的临时卡牌）。不同于 `AfterCardEnteredCombat`，此钩子只在"战斗中生成新牌"时触发。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `card` | `CardModel` | 被生成的卡牌 |
| `creator` | `Player?` | 生成这张牌的玩家；非玩家来源可能为 null |

#### 示例：火箭飞拳（Rocket Punch）

- **中文名：** 火箭飞拳
- **英文名：** Rocket Punch
- **效果：** 造成 13 点伤害。抽 1 张牌。每当你生成状态牌时，此牌的耗能将在下一次打出前降为 0。
- **升级后：** 伤害 +1，抽牌 +1

```csharp
// src/Core/Models/Cards/RocketPunch.cs
public sealed class RocketPunch : CardModel
{
    // 构造：2费 攻击牌 非普通 可指定任意敌人
    public RocketPunch() : base(2, CardType.Attack, CardRarity.Uncommon, TargetType.AnyEnemy) { }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await DamageCmd.Attack(base.DynamicVars.Damage.BaseValue).FromCard(this)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
        await CardPileCmd.Draw(choiceContext, base.DynamicVars.Cards.BaseValue, base.Owner);
    }

    public override Task AfterCardGeneratedForCombat(CardModel card, Player? creator)
    {
        // 只响应由本玩家生成的卡牌
        if (creator != base.Owner) return Task.CompletedTask;
        if (card.Owner != base.Owner) return Task.CompletedTask;
        // 只响应状态牌的生成
        if (card.Type != CardType.Status) return Task.CompletedTask;

        // 将自身费用设为 0（直到打出前）
        base.EnergyCost.SetUntilPlayed(0);
        return Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
        base.DynamicVars.Damage.UpgradeValueBy(1m);
        base.DynamicVars.Cards.UpgradeValueBy(1m);
    }
}
```

> **关键模式：** 0.106.x 中 RocketPunch 这类逻辑应使用 `creator` 过滤来源玩家，而不是旧版的 `addedByPlayer` 布尔值。费用使用 `SetUntilPlayed(0)` 而非 `AddThisTurn`，意味着一旦触发，效果持续直到此卡被打出（跨回合有效，但打一次后恢复原费用）。

---

## 5. 攻击与格挡钩子（卡牌相关）

这些钩子定义在 `Hook.cs` 和 `AbstractModel.cs` 中，由攻击/格挡命令触发，卡牌可通过重写来监听这些战斗事件。

### 5.1 `BeforeAttack` / `AfterAttack`

```csharp
// Hook.cs:37,46 — 调度方法
public static async Task BeforeAttack(ICombatState combatState, AttackCommand command)
public static async Task AfterAttack(ICombatState combatState,
    PlayerChoiceContext choiceContext, AttackCommand command)

// AbstractModel.cs — 虚方法
public virtual Task BeforeAttack(AttackCommand command) => Task.CompletedTask;
public virtual Task AfterAttack(AttackCommand command) => Task.CompletedTask;
```

**触发时机：** 
- `BeforeAttack` — 在攻击伤害实际造成**之前**触发
- `AfterAttack` — 在攻击伤害造成**之后**触发

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `command` | `AttackCommand` | 攻击命令，包含 `Attacker`（攻击者）、`Results`（伤害结果）、`CardSource` 等 |

**注意：** 这两个钩子不提供 `PlayerChoiceContext`，因此只能做状态修改，不能执行命令。

#### 示例：重压（Flatten）

- **中文名：** 重压
- **英文名：** Flatten
- **效果：** 奥斯提造成 12 点伤害。如果奥斯提本回合攻击过，则这张牌的耗能变为 0。
- **升级后：** 伤害 +4

```csharp
// src/Core/Models/Cards/Flatten.cs
public sealed class Flatten : CardModel
{
    // 发光提示：奥斯提已攻击时显示金色边框
    protected override bool ShouldGlowGoldInternal => HasOstyAttackedThisTurn;
    protected override bool ShouldGlowRedInternal => base.Owner.IsOstyMissing;

    // 帮助方法：检查奥斯提本回合是否攻击过
    private bool HasOstyAttackedThisTurn => CombatManager.Instance.History.Entries
        .OfType<CreatureAttackedEntry>()
        .Any(e => e.Actor == base.Owner.Osty && e.HappenedThisTurn(base.CombatState));

    public Flatten() : base(2, CardType.Attack, CardRarity.Common, TargetType.AnyEnemy) { }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (!Osty.CheckMissingWithAnim(base.Owner))
        {
            await DamageCmd.Attack(base.DynamicVars.OstyDamage.BaseValue)
                .FromOsty(base.Owner.Osty, this)     // 由奥斯提发动攻击
                .Targeting(cardPlay.Target)
                .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
                .Execute(choiceContext);
        }
    }

    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (card != this) return Task.CompletedTask;
        if (HasOstyAttackedThisTurn) ReduceCost();   // 进入战斗时回溯检查
        return Task.CompletedTask;
    }

    public override Task AfterAttack(AttackCommand command)
    {
        if (command.Attacker == null) return Task.CompletedTask;
        if (command.Attacker != base.Owner.Osty) return Task.CompletedTask;
        ReduceCost();  // 奥斯提攻击后降低费用
        return Task.CompletedTask;
    }

    private void ReduceCost()
    {
        base.EnergyCost.SetThisTurn(0);  // 本回合费用变为 0
    }
}
```

> **关键模式：** Flatten 监听 `AfterAttack` 来追踪特定攻击者（`base.Owner.Osty`）的攻击。当奥斯提攻击后，自动将此卡费用降为 0。同时通过 `ShouldGlowGoldInternal` 提供视觉提示——费用为 0 时卡牌边框会发光。

---

### 5.2 `BeforeBlockGained` / `AfterBlockGained`

```csharp
// Hook.cs:73,82 — 调度方法
public static async Task BeforeBlockGained(ICombatState combatState, Creature creature,
    decimal amount, ValueProp props, CardModel? cardSource)
public static async Task AfterBlockGained(ICombatState combatState, Creature creature,
    decimal amount, ValueProp props, CardModel? cardSource)
```

**触发时机：** 格挡值被给予之前和之后。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `creature` | `Creature` | 获得格挡的生物 |
| `amount` | `decimal` | 格挡数量（修改前） |
| `props` | `ValueProp` | 格挡值的属性标记（如是否为无能量格挡、移动格挡等） |
| `cardSource` | `CardModel?` | 产生格挡的卡牌来源，可为 null（非卡牌来源的格挡） |

**说明：** 这两个钩子主要用于防御型能力/遗物修改格挡值。目前系统中它们主要由 `ModifyBlock` 相关钩子（见第 7 章）间接使用，以实现格挡的数值修改。

#### 示例：格挡获得后累计状态

```csharp
private sealed class Data
{
    public decimal BlockGainedThisTurn;
}

protected override object InitInternalData() => new Data();

public override Task AfterBlockGained(Creature creature, decimal amount,
    ValueProp props, CardModel? cardSource)
{
    if (creature != base.Owner) return Task.CompletedTask;
    if (amount <= 0m) return Task.CompletedTask;

    GetInternalData<Data>().BlockGainedThisTurn += amount;
    Flash();
    return Task.CompletedTask;
}
```

**实际覆写参考：** `JuggernautPower`、`BeaconOfHopePower` 使用 `AfterBlockGained` 响应格挡获得；当前 0.106.1 本体没有发现 `BeforeBlockGained` 的实际覆写，更适合 Mod 做预记录或预动画。

---

### 5.3 `AfterBlockBroken` / `AfterBlockCleared`

```csharp
// Hook.cs:55,64 — 调度方法
public static async Task AfterBlockBroken(ICombatState combatState, Creature creature)
public static async Task AfterBlockCleared(ICombatState combatState, Creature creature)
```

**触发时机：**
- `AfterBlockBroken` — 格挡被攻击打破（降为 0）时触发
- `AfterBlockCleared` — 格挡在回合开始被清空时触发

#### 示例：格挡破裂/清空后重置状态

```csharp
public override Task AfterBlockBroken(Creature creature)
{
    if (creature != base.Owner) return Task.CompletedTask;

    GetInternalData<Data>().WasBlockBrokenThisTurn = true;
    return Task.CompletedTask;
}

public override Task AfterBlockCleared(Creature creature)
{
    if (creature != base.Owner) return Task.CompletedTask;

    GetInternalData<Data>().WasBlockBrokenThisTurn = false;
    return Task.CompletedTask;
}
```

**实际覆写参考：** `BurrowedPower` 使用 `AfterBlockBroken`；`CaptainsWheel`、`HornCleat`、`SelfFormingClayPower`、`ToricToughnessPower` 等使用 `AfterBlockCleared` 做回合开始后的状态处理。

---

## 6. 回合结束与特殊钩子

### 6.1 `OnTurnEndInHand`

```csharp
// CardModel.cs:1303
public virtual Task OnTurnEndInHand(PlayerChoiceContext choiceContext)
```

**触发时机：** 回合结束时，如果此卡牌仍在手牌中，则触发。是状态牌常见的负面效果入口。

**典型用法：** 状态牌（Burn、Decay、BadLuck 等）在每个回合结束时对持牌者造成伤害或施加惩罚。

#### 示例：灼伤（Burn）

- **中文名：** 灼伤
- **英文名：** Burn
- **效果：** 在你的回合结束时，如果这张牌在你的手牌中，你受到 2 点伤害。（不可打出，不可升级）

```csharp
// src/Core/Models/Cards/Burn.cs
public sealed class Burn : CardModel
{
    public override int MaxUpgradeLevel => 0;                          // 不可升级
    public override IEnumerable<CardKeyword> CanonicalKeywords =>      // 不可打出
        new[] { CardKeyword.Unplayable };
    public override bool HasTurnEndInHandEffect => true;               // 声明有回合结束效果

    public Burn() : base(-1, CardType.Status, CardRarity.Status, TargetType.None) { }

    public override async Task OnTurnEndInHand(PlayerChoiceContext choiceContext)
    {
        // 播放火焰视觉特效
        NCombatRoom.Instance?.CombatVfxContainer.AddChildSafely(
            NGroundFireVfx.Create(base.Owner.Creature));
        SfxCmd.Play("event:/sfx/characters/attack_fire");

        // 对持牌者造成伤害
        await CreatureCmd.Damage(choiceContext, base.Owner.Creature,
            base.DynamicVars.Damage, this);
    }
}
```

> **要点：** 状态牌需设置 `HasTurnEndInHandEffect => true` 来告知引擎此卡有回合结束效果（引擎据此决定是否需要在回合结束时遍历手牌）。费用设为 `-1` 和 `Unplayable` 关键词确保此牌无法被打出。

---

### 6.2 `AfterTransformedFrom` / `AfterTransformedTo`

```csharp
// CardModel.cs:1276,1280
public virtual void AfterTransformedFrom() { }
public virtual void AfterTransformedTo() { }
```

**触发时机：**
- `AfterTransformedFrom` — 此卡牌被变形为另一张牌时（变形前）
- `AfterTransformedTo` — 此卡牌从另一张牌变形而来时（变形后）

#### 示例：君王之剑清理变形前 VFX

```csharp
// SovereignBlade.cs:176
public override void AfterTransformedFrom()
{
    RemoveSovereignBladeNode();  // 移除与此卡关联的 VFX 节点
}
```

#### 自定义示例：变形后重建派生状态

```csharp
public override void AfterTransformedTo()
{
    // 新卡从旧卡变形而来后，重新计算只依赖当前卡面的临时缓存。
    RebuildPreviewCache();
}
```

---

### 6.3 `AfterForged` / `AfterDowngraded`

```csharp
// CardModel.cs:1284
public void AfterForged() { this.Forged?.Invoke(); }

// CardModel.cs:1671
protected virtual void AfterDowngraded() { }
```

**触发时机：**
- `AfterForged` — 卡牌被锻造（Forged）后，触发 `Forged` 事件
- `AfterDowngraded` — 卡牌被降级（如被战斗效果降级）后

**说明：** `AfterForged` 不直接可重写——它触发一个事件，外部通过订阅 `card.Forged += ...` 来响应。`AfterDowngraded` 是受保护的虚方法，可被子类重写用于清理升级状态。

#### 示例：监听锻造事件

```csharp
public override void AfterCreated()
{
    base.AfterCreated();
    Forged += OnForged;
}

private void OnForged()
{
    // ForgeCmd 完成后触发，可在这里刷新自定义视觉或缓存。
    RefreshForgedVfx();
}
```

#### 示例：降级后回滚成长值

```csharp
protected override void AfterDowngraded()
{
    base.AfterDowngraded();

    // 真实游戏里 Claw / GeneticAlgorithm / KinglyPunch 等成长牌会在降级后
    // 同步修正由升级带来的派生数值。
    RecalculateGrowthBonusAfterDowngrade();
}
```

---

### 6.4 `AfterCloned`

```csharp
// CardModel.cs（受保护的虚方法）
protected virtual void AfterCloned() { }
```

**触发时机：** 卡牌被克隆后（如通过复制效果）。

#### 示例：君王之剑（Sovereign Blade）的 AfterCloned

```csharp
// SovereignBlade.cs:163
protected override void AfterCloned()
{
    base.AfterCloned();
    CreatedThroughForge = false;  // 克隆体不保留"锻造创建"标记
}
```

> **要点：** 克隆通常不应继承原卡的特殊状态标记（如 `CreatedThroughForge`），`AfterCloned` 提供了重置这些标记的时机。

---

### 6.5 `GetResultPileType`

```csharp
// CardModel.cs（受保护的虚方法）
protected virtual PileType GetResultPileType()
```

**触发时机：** 卡牌打出后，在 `OnPlayWrapper` 中计算结果牌堆时调用。

**默认行为：** 返回 `PileType.Discard`（正常打出进入弃牌堆）。消耗牌返回 `PileType.Exhaust`。

**典型用法：** 特殊卡牌重写此方法来改变打出后的目标牌堆——例如，一张可在打出后回到手牌的卡牌可以返回 `PileType.Hand`。

---

### 6.6 `BeforeCardAutoPlayed`

```csharp
// Hook.cs:91 — 调度方法
public static async Task BeforeCardAutoPlayed(ICombatState combatState, CardModel card,
    Creature? target, AutoPlayType type)

// AbstractModel.cs:206 — 虚方法
public virtual Task BeforeCardAutoPlayed(CardModel card, Creature? target, AutoPlayType type)
    => Task.CompletedTask;
```

**触发时机：** 一张卡牌被自动打出时（如通过 AI、卡牌效果、事件等），在实际打出效果之前触发。

**参数：**
| 参数 | 类型 | 说明 |
|------|------|------|
| `card` | `CardModel` | 将被自动打出的卡牌 |
| `target` | `Creature?` | 自动选择的目标 |
| `type` | `AutoPlayType` | 自动打出类型（区分 AI 自动、效果强制等） |

---

## 7. Mod 系统卡牌修改器钩子

Sts2BaseMod 提供了独立于核心钩子系统的 Mod 开发框架。通过继承 `AbstractCardModifier` 并注册到 `CardModifierManager`，Mod 可以对特定卡牌附加自定义修改器。

**文件位置：**
- `Sts2BaseMod/Abstracts/AbstractCardModifier.cs` — 修改器基类
- `Sts2BaseMod/Helpers/CardModifierManager.cs` — 修改器管理 API

---

### 7.1 修改器生命周期

#### 注册和移除 API

```csharp
// 添加修改器（触发 OnInitialApplication + OnCardModified）
CardModifierManager.AddModifier(card, modifier);

// 移除特定修改器（触发 OnRemove）
CardModifierManager.RemoveSpecificModifier(card, modifier);

// 按 ID 移除（如移除所有 "basemod:Exhaust" 修改器）
CardModifierManager.RemoveModifiersById(card, "basemod:Exhaust");

// 检查是否有特定 ID 的修改器
bool has = CardModifierManager.HasModifier(card, "basemod:Exhaust");

// 复制所有修改器到另一张牌
CardModifierManager.CopyModifiers(fromCard, toCard);

// 移除所有修改器（可保留固有修改器）
CardModifierManager.RemoveAllModifiers(card, includeInherent: true);
```

#### 条件自动移除

引擎在打牌和回合结束时自动调用以下方法：

```csharp
// 打出后自动移除返回 true 的修改器
CardModifierManager.RemoveWhenPlayedModifiers(card);

// 回合结束自动移除返回 true 的修改器
CardModifierManager.RemoveEndOfTurnModifiers(card);
```

---

### 7.2 条件控制钩子

| 钩子 | 签名 | 说明 |
|------|------|------|
| `ShouldApply` | `bool ShouldApply(CardModel card)` | 修改器是否应生效。返回 `false` 时效果被跳过 |
| `IsInherent` | `bool IsInherent(CardModel card)` | 是否为固有修改器（不可被移除，除非 `includeInherent: true`） |
| `RemoveOnCardPlayed` | `bool RemoveOnCardPlayed(CardModel card)` | 卡牌打出后是否自动移除此修改器 |
| `RemoveAtEndOfTurn` | `bool RemoveAtEndOfTurn(CardModel card)` | 回合结束时是否自动移除此修改器 |
| `CanPlayCard` | `bool CanPlayCard(CardModel card)` | 修改器是否可以阻止卡牌被打出（返回 `false` 阻止） |

---

### 7.3 数值修改钩子

这些钩子构成 Mod 系统的数值修改管道，按阶段分为三层：

```csharp
// 三层伤害修改
public virtual void ModifyBaseDamage(CardModel card, Creature? target, ref decimal damage) { }
public virtual void ModifyDamage(CardModel card, Creature? target, ref decimal damage) { }
public virtual void ModifyDamageFinal(CardModel card, Creature? target, ref decimal damage) { }

// 三层格挡修改
public virtual void ModifyBaseBlock(CardModel card, ref decimal block) { }
public virtual void ModifyBlock(CardModel card, ref decimal block) { }
public virtual void ModifyBlockFinal(CardModel card, ref decimal block) { }

// 魔法/特殊数值修改
public virtual void ModifyBaseMagic(CardModel card, ref decimal magic) { }
```

**修改阶段说明：**

| 阶段 | 说明 | 典型用途 |
|------|------|----------|
| `Base` | 修改基础值（`DynamicVars.XXX.BaseValue`） | 永久升级、锻造加成 |
| `Final` | 应用所有加成后的最终调整 | 伤害上限、最小值保证 |
| 无前缀 | 中间计算阶段 | 力量加成、易伤加成 |

**注意：** 参数使用 `ref decimal`，修改器直接修改传入的引用变量值。`CardModifierManager` 遍历所有 `ShouldApply(card) == true` 的修改器并依次调用。

---

### 7.4 文本修改钩子

```csharp
// 修改卡牌效果描述文本
public virtual string ModifyDescription(CardModel card, string rawDescription) => rawDescription;

// 修改卡牌名称
public virtual string ModifyName(CardModel card, string cardName) => cardName;

// 添加额外关键词文本（显示在卡牌效果之上）
public virtual List<string> ExtraDescriptors(CardModel card) => new();

// 添加自定义悬停提示
public virtual List<TooltipInfo> AdditionalTooltips(CardModel card) => new();
```

**`TooltipInfo` 结构：**
```csharp
public class TooltipInfo
{
    public string Header { get; }  // 提示标题
    public string Body { get; }    // 提示正文
}
```

---

### 7.5 事件响应钩子

| 钩子 | 说明 | 触发时机 |
|------|------|----------|
| `OnInitialApplication(CardModel)` | 修改器首次添加到卡牌时 | `AddModifier` 调用 |
| `OnRemove(CardModel)` | 修改器被移除前 | `RemoveModifier` 等调用 |
| `OnCardModified(CardModel)` | 卡牌被任何方式修改时 | 修改器添加/移除时 |
| `OnUse(CardModel, Creature?)` | 卡牌被打出使用时 | 卡牌 `OnPlay` 中 |
| `OnDrawn(CardModel)` | 卡牌被抽到时 | 抽牌系统 |
| `OnExhausted(CardModel)` | 卡牌被消耗时 | 消耗系统 |
| `OnRetained(CardModel)` | 卡牌回合结束时被保留 | 保留系统 |
| `OnBattleStart(CardModel)` | 战斗开始时 | 战斗初始化 |
| `AtEndOfTurn(CardModel)` | 回合结束时 | 回合结束流程 |
| `OnOtherCardPlayed(CardModel, CardModel)` | **其他**卡牌被打出时 | 别卡牌 `OnPlayWrapper` |
| `OnApplyPowers(CardModel)` | 卡牌能力应用时 | 能力修改事件 |
| `OnCalculateCardDamage(CardModel, Creature?)` | 计算卡牌伤害时 | 伤害预览/显示 |
| `GetGlow(CardModel) -> Color?` | 返回额外发光颜色 | 视觉提示（返回 `null` 不发光） |

---

### 7.6 完整示例：消耗修改器

以下是一个为卡牌添加"消耗"关键词的修改器：

```csharp
public class ExhaustCardModifier : AbstractCardModifier
{
    public override string Identifier(CardModel card) => "basemod:Exhaust";

    public override AbstractCardModifier MakeCopy() => new ExhaustCardModifier();

    public override List<string> ExtraDescriptors(CardModel card)
        => new() { "Exhaust." };  // 卡牌上显示 "Exhaust." 文本

    public override bool RemoveOnCardPlayed(CardModel card) => true;  // 打出后移除修改器

    // 卡牌消耗时，修改器自我移除（因为效果已达成）
    public override void OnExhausted(CardModel card)
    {
        CardModifierManager.RemoveSpecificModifier(card, this);
    }
}
```

> **要点：** `Identifier` 返回唯一 ID，`MakeCopy` 用于克隆，`RemoveOnCardPlayed` 在打出后移除，`ExtraDescriptors` 在卡牌上显示关键词文本。

---

## 8. CardModel C# 事件

`CardModel` 暴露了 10 个 C# 原生事件，外部代码可通过 `+=` 订阅来响应卡牌状态变化。这些事件由 `CombatStateTracker` 统一订阅以更新 UI。

### 事件列表

| 事件 | 触发时机 | 源文件行号 |
|------|----------|------------|
| `Played` | 卡牌完成打出流程 (`OnPlayWrapper` 末尾) | CardModel.cs:847 |
| `Drawn` | 卡牌被抽到手上 | CardModel.cs:849 |
| `Upgraded` | 卡牌升级后 (`OnUpgrade` 调用后) | CardModel.cs:853 |
| `Forged` | 卡牌锻造后 (`AfterForged` 调用后) | CardModel.cs:855 |
| `AfflictionChanged` | 苦难效果变化 | CardModel.cs:837 |
| `EnchantmentChanged` | 附魔效果变化 | CardModel.cs:839 |
| `EnergyCostChanged` | 能量费用变化 | CardModel.cs:841 |
| `StarCostChanged` | 星星费用变化 | CardModel.cs:851 |
| `KeywordsChanged` | 关键词（属性）变化 | CardModel.cs:843 |
| `ReplayCountChanged` | 重播计数变化 | CardModel.cs:845 |

### CombatStateTracker 订阅示例

```csharp
// src/Core/Combat/CombatStateTracker.cs (简化版)
public void Subscribe(CardModel card)
{
    card.AfflictionChanged   += OnCardValueChanged;
    card.EnchantmentChanged  += OnCardValueChanged;
    card.EnergyCostChanged   += OnCardValueChanged;
    card.ReplayCountChanged  += OnCardValueChanged;
    card.Played              += OnCardValueChanged;
    card.Drawn               += OnCardValueChanged;
    card.StarCostChanged     += OnCardValueChanged;
    card.Upgraded            += OnCardValueChanged;
    card.Forged              += OnCardValueChanged;
}
```

所有事件都映射到 `OnCardValueChanged`，触发战斗状态重新计算（包括遗物触发条件、能力更新、UI 刷新等）。

### 在 Mod 中使用事件

```csharp
// Mod 中订阅卡牌事件
card.Upgraded += () =>
{
    // 卡牌升级时执行额外逻辑
    GD.Print($"{card.Name} was upgraded!");
};

card.Played += () =>
{
    // 卡牌打出后执行追踪逻辑
    _playCount++;
};
```

> **注意：** 卡牌被销毁或离开游戏时，事件处理器置为 `null`（见 `CardModel.DisposeEvents()` 第 945-954 行）。Mod 不应假设事件订阅在整个游戏周期中持久有效。

---

## 参考文件索引

| 文件 | 说明 |
|------|------|
| `src/Core/Hooks/Hook.cs` | 中心钩子调度器（所有 `Before*`/`After*` 静态方法） |
| `src/Core/Models/AbstractModel.cs` | 抽象模型基类（所有可重写的虚方法） |
| `src/Core/Models/CardModel.cs` | 卡牌模型基类（卡牌专属虚方法 + 事件定义 + `OnPlayWrapper`） |
| `src/Core/Models/Cards/` | 具体卡牌实现目录（150+ 文件） |
| `src/Core/Combat/CombatStateTracker.cs` | 战斗状态追踪器（订阅 `CardModel` 事件） |
| `Sts2BaseMod/Abstracts/AbstractCardModifier.cs` | Mod 卡牌修改器基类 |
| `Sts2BaseMod/Helpers/CardModifierManager.cs` | Mod 卡牌修改器管理 API |
| `localization/zhs/cards.json` | 卡牌中文名称和效果描述 |
| `localization/eng/cards.json` | 卡牌英文名称和效果描述 |

---

## 9. 数值修改钩子

数值修改钩子是游戏中最核心的钩子类别，它们构成了伤害、格挡、费用等数值的"修改管道"。每个修改钩子都遍历所有监听模型的对应虚方法，收集修改结果。

### 9.1 `ModifyDamage` 伤害修改管道

伤害修改分为三个子阶段，按顺序执行：

```
ModifyDamageAdditive (加法) → ModifyDamageMultiplicative (乘法) → ModifyDamageCap (上限)
```

```csharp
// Hook.cs:1130 — 调度入口
public static decimal ModifyDamage(IRunState runState, ICombatState? combatState,
    Creature? target, Creature? dealer, decimal damage, ValueProp props,
    CardModel? cardSource, ModifyDamageHookType hookType, CardPreviewMode previewMode,
    out IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs 虚方法（三个子阶段）
public virtual decimal ModifyDamageAdditive(Creature? target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource) => 0m;  // 默认：不加成

public virtual decimal ModifyDamageMultiplicative(Creature? target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource) => 1m;  // 默认：不乘

public virtual decimal ModifyDamageCap(Creature? target, ValueProp props,
    Creature? dealer, CardModel? cardSource) => decimal.MaxValue;  // 默认：无上限
```

**`ModifyDamageHookType` 枚举**（`ModifyDamageHookType.cs`）控制哪些阶段被调用：
- `Additive` — 仅加法
- `Multiplicative` — 仅乘法
- `All` — 加法 + 乘法 + 上限

#### 示例：力量（StrengthPower）

- **中文名：** 力量
- **效果：** 每层力量增加 1 点攻击伤害。

```csharp
// src/Core/Models/Powers/StrengthPower.cs
public sealed class StrengthPower : PowerModel
{
    public override decimal ModifyDamageAdditive(Creature? target, decimal amount,
        ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (base.Owner != dealer) return 0m;            // 只对拥有者生效
        if (!props.IsPoweredAttack()) return 0m;         // 只对"增强型攻击"生效
        return base.Amount;                              // 每层 +1 伤害
    }
}
```

#### 示例：易伤（VulnerablePower）

- **中文名：** 易伤
- **效果：** 受到攻击伤害 ×1.5。

```csharp
// src/Core/Models/Powers/VulnerablePower.cs
public sealed class VulnerablePower : PowerModel
{
    public override decimal ModifyDamageMultiplicative(Creature? target, decimal amount,
        ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (target != base.Owner) return 1m;                        // 只对被施加者生效
        if (!props.IsPoweredAttack()) return 1m;                     // 只对增强型攻击生效

        decimal multiplier = base.DynamicVars["DamageIncrease"].BaseValue; // 默认 1.5

        // 检查遗物和能力的乘数修改
        if (dealer != null)
        {
            PaperPhrog paperPhrog = dealer.Player?.GetRelic<PaperPhrog>();
            if (paperPhrog != null) multiplier = paperPhrog.ModifyVulnerableMultiplier(...);
            CrueltyPower cruelty = dealer.GetPower<CrueltyPower>();
            if (cruelty != null) multiplier = cruelty.ModifyVulnerableMultiplier(...);
        }
        return multiplier;
    }
}
```

> **要点：** 易伤展示了从 `ModifyDamageMultiplicative` 中调用遗物和其他能力来**链式修改乘数**的模式。`PaperPhrog` 遗物可将易伤乘数进一步提升。

#### 示例：无实体（IntangiblePower）

- **中文名：** 无实体
- **效果：** 将受到的所有伤害上限限制为 1。

```csharp
// src/Core/Models/Powers/IntangiblePower.cs
public sealed class IntangiblePower : PowerModel
{
    public override decimal ModifyDamageCap(Creature? target, ValueProp props,
        Creature? dealer, CardModel? cardSource)
    {
        if (target != base.Owner) return decimal.MaxValue;
        return GetDamageCap(dealer);  // 默认 1，有钉靴时为 5
    }
}
```

---

### 9.2 `ModifyBlock` 格挡修改管道

与伤害类似，格挡修改也分为两个子阶段：`ModifyBlockAdditive` → `ModifyBlockMultiplicative`。

```csharp
// Hook.cs:984 — 调度入口
public static decimal ModifyBlock(ICombatState combatState, Creature target,
    decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay,
    out IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs 虚方法
public virtual decimal ModifyBlockAdditive(Creature target, decimal block,
    ValueProp props, CardModel? cardSource, CardPlay? cardPlay) => 0m;

public virtual decimal ModifyBlockMultiplicative(Creature target, decimal block,
    ValueProp props, CardModel? cardSource, CardPlay? cardPlay) => 1m;
```

#### 示例：敏捷（DexterityPower）

- **中文名：** 敏捷
- **效果：** 每层敏捷增加 1 点从卡牌获得的格挡。

```csharp
// src/Core/Models/Powers/DexterityPower.cs
public sealed class DexterityPower : PowerModel
{
    public override decimal ModifyBlockAdditive(Creature target, decimal block,
        ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
    {
        // 来自卡牌：检查卡牌拥有者
        if (cardSource != null)
        {
            if (cardSource.Owner.Creature != base.Owner) return 0m;
        }
        // 非卡牌来源：检查直接目标
        else if (base.Owner != target) return 0m;

        if (!props.IsPoweredCardOrMonsterMoveBlock()) return 0m;
        return base.Amount;  // 每层 +1 格挡
    }
}
```

#### 示例：脆弱（FrailPower）

- **中文名：** 脆弱
- **效果：** 格挡效果减少。

```csharp
// src/Core/Models/Powers/FrailPower.cs
public sealed class FrailPower : PowerModel
{
    public override decimal ModifyBlockMultiplicative(...)
    {
        if (target != base.Owner) return 1m;
        // 返回小于 1 的值以减少格挡
    }
}
```

---

### 9.3 `ModifyEnergyCostInCombat`

```csharp
// Hook.cs:1215
public static decimal ModifyEnergyCostInCombat(ICombatState combatState,
    CardModel card, decimal originalCost)

// AbstractModel.cs:821
public virtual bool TryModifyEnergyCostInCombat(CardModel card,
    decimal originalCost, out decimal modifiedCost)
// 返回 true 表示已修改, modifiedCost 设为新值
```

**触发时机：** 在计算卡牌打出所需的实际能量费用之前。

#### 示例：腐化（CorruptionPower）

- **中文名：** 腐化
- **效果：** 所有技能牌费用变为 0，打出后消耗。

```csharp
// src/Core/Models/Powers/CorruptionPower.cs
public sealed class CorruptionPower : PowerModel
{
    public override bool TryModifyEnergyCostInCombat(CardModel card,
        decimal originalCost, out decimal modifiedCost)
    {
        if (card.Owner.Creature != base.Owner || card.Type != CardType.Skill)
        {
            modifiedCost = originalCost;
            return false;
        }
        modifiedCost = 0;  // 费用变为 0
        return true;
    }

    // 同时修改卡牌打出后的目标牌堆——技能牌被消耗
    public override (PileType, CardPilePosition) ModifyCardPlayResultPileTypeAndPosition(
        CardModel card, bool isAutoPlay, ResourceInfo resources,
        PileType pileType, CardPilePosition position)
    {
        if (card.Owner.Creature != base.Owner || card.Type != CardType.Skill)
            return (pileType, position);
        return (PileType.Exhaust, position);  // 改为进入消耗堆
    }
}
```

> **关键模式：** 腐化同时使用 `TryModifyEnergyCostInCombat`（降费用）和 `ModifyCardPlayResultPileTypeAndPosition`（改牌堆），是典型的"多钩子协作实现一个效果"的模式。

---

### 9.4 `ModifyStarCost`

```csharp
// Hook.cs:1540
public static decimal ModifyStarCost(ICombatState combatState,
    CardModel card, decimal originalCost)

// AbstractModel.cs:827
public virtual bool TryModifyStarCost(CardModel card,
    decimal originalCost, out decimal modifiedCost)
```

**说明：** 与 `ModifyEnergyCostInCombat` 模式完全相同，但针对星石费用。主要用于如 `BrilliantScarf`（降低星石费用）等遗物。

#### 示例：星能费用减免

```csharp
// BrilliantScarf.cs / VoidFormPower.cs 使用同类模式
public override bool TryModifyStarCost(CardModel card, decimal originalCost,
    out decimal modifiedCost)
{
    if (card.Owner != base.Owner)
    {
        modifiedCost = originalCost;
        return false;
    }

    modifiedCost = Math.Max(0m, originalCost - 1m);
    return modifiedCost != originalCost;
}
```

`TryModifyStarCost` 是短路式修改：第一个返回 `true` 的监听器给出最终费用，后续监听器不会继续覆盖。

---

### 9.5 `ModifyCardPlayCount`

```csharp
// Hook.cs:1040
public static int ModifyCardPlayCount(ICombatState combatState, CardModel card,
    int playCount, Creature? target, out List<AbstractModel> modifyingModels)

// AbstractModel.cs:631
public virtual int ModifyCardPlayCount(CardModel card, Creature? target,
    int playCount) => playCount;  // 默认：不修改
```

**触发时机：** 卡牌即将执行 `OnPlay` 之前，决定卡牌需要被"重播"多少次。

#### 示例：回响形态（EchoFormPower）

- **中文名：** 回响形态
- **效果：** 每回合打出的第一张牌打出两次。

```csharp
// src/Core/Models/Powers/EchoFormPower.cs
public sealed class EchoFormPower : PowerModel
{
    public override int ModifyCardPlayCount(CardModel card, Creature? target, int playCount)
    {
        if (card.Owner.Creature != base.Owner) return playCount;

        // 统计本回合已开始的首次打出次数
        int playedCount = CombatManager.Instance.History.CardPlaysStarted
            .Count(e => e.Actor == base.Owner && e.CardPlay.IsFirstInSeries
                     && e.HappenedThisTurn(base.CombatState));

        // 如果已达到层数上限，不再触发
        if (playedCount >= base.Amount) return playCount;

        // 重播一次（playCount + 1 = 打出两次）
        return playCount + 1;
    }

    public override Task AfterModifyingCardPlayCount(CardModel card)
    {
        Flash();  // 遗物闪光动画
        return Task.CompletedTask;
    }
}
```

> **注意：** 还有 `BurstPower`（技能牌打出两次）、`DuplicationPower`（下一张牌打出两次）等使用相同的修改模式。

---

### 9.6 `ModifyXValue`

```csharp
// Hook.cs:1584
public static int ModifyXValue(ICombatState combatState, CardModel card, int originalValue)

// AbstractModel.cs:789
public virtual int ModifyXValue(CardModel card, int originalValue) => originalValue;
```

**触发时机：** X 费用卡牌打出时，计算实际的 X 值。

#### 示例：化学物X（Chemical X）

- **中文名：** 化学物X
- **效果：** 耗能为 X 的牌的效果数值增加 2 点。

```csharp
// src/Core/Models/Relics/ChemicalX.cs
public sealed class ChemicalX : RelicModel
{
    public override int ModifyXValue(CardModel card, int originalValue)
    {
        if (base.Owner != card.Owner) return originalValue;

        // X 值 +2
        return originalValue + base.DynamicVars["Increase"].IntValue;
    }
}
```

---

### 9.7 `ModifyCardBeingAddedToDeck`

```csharp
// Hook.cs:1016
public static CardModel ModifyCardBeingAddedToDeck(IRunState runState,
    CardModel card, out List<AbstractModel> modifyingModels)

// AbstractModel.cs:794,800
public virtual bool TryModifyCardBeingAddedToDeck(CardModel card,
    out CardModel? newCard) { newCard = null; return false; }
public virtual bool TryModifyCardBeingAddedToDeckLate(CardModel card,
    out CardModel? newCard) { newCard = null; return false; }
```

**触发时机：** 卡牌即将被添加到玩家牌库时（旅程中，非战斗中）。这是**唯一可以替换卡牌本身的钩子**——监听器可返回一张修改后的新卡牌。

**分 Early/Normal/Late 三阶段。** 各阶段依次执行，每个阶段都可以替换 `newCard`，下一个阶段的输入是上一阶段的输出。

#### 示例：冻结之蛋（FrozenEgg）

- **中文名：** 冻结之蛋
- **效果：** 每当你获得能力牌时，将其升级。

```csharp
// src/Core/Models/Relics/FrozenEgg.cs
public sealed class FrozenEgg : RelicModel
{
    public override bool TryModifyCardBeingAddedToDeck(CardModel card, out CardModel? newCard)
    {
        newCard = null;
        if (card.Owner != base.Owner) return false;
        if (card.Type != CardType.Power) return false;
        if (!card.IsUpgradable) return false;

        // 克隆卡牌并升级
        newCard = base.Owner.RunState.CloneCard(card);
        CardCmd.Upgrade(newCard, CardPreviewStyle.None);
        return true;  // 返回 true 表示已替换
    }
}
```

> **姊妹遗物：** `MoltenEgg`（攻击牌）和 `ToxicEgg`（技能牌）使用相同模式。

---

### 9.8 其他数值修改钩子

#### `ModifyCardRewardCreationOptions`

```csharp
// Hook.cs:1088
public static CardCreationOptions ModifyCardRewardCreationOptions(IRunState runState,
    Player player, CardCreationOptions options)

// AbstractModel.cs:646,651
public virtual CardCreationOptions ModifyCardRewardCreationOptions(Player player,
    CardCreationOptions options) => options;
public virtual CardCreationOptions ModifyCardRewardCreationOptionsLate(Player player,
    CardCreationOptions options) => options;
```

**用途：** 修改卡牌奖励的创建选项（如将其他角色的卡牌加入奖励池）。

**示例：** `PrismaticGem` 将所有其他角色的卡牌加入卡牌奖励池。

#### `ModifyCardRewardUpgradeOdds`

```csharp
// Hook.cs:1120
public static decimal ModifyCardRewardUpgradeOdds(IRunState runState,
    Player player, CardModel card, decimal originalOdds)
```

**用途：** 修改奖励卡牌出现已升级版本的概率。

**自定义示例：** 将本玩家的技能牌奖励升级概率提高到 100%。

```csharp
public override decimal ModifyCardRewardUpgradeOdds(Player player,
    CardModel card, decimal odds)
{
    if (player != base.Owner) return odds;
    if (card.Type != CardType.Skill) return odds;

    return 1m;
}
```

#### `ModifyHandDraw`

```csharp
// Hook.cs:1275
public static decimal ModifyHandDraw(ICombatState combatState, Player player,
    decimal originalCardCount, out IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs:691,696
public virtual decimal ModifyHandDraw(Player player, decimal count) => count;
public virtual decimal ModifyHandDrawLate(Player player, decimal count) => count;
```

**用途：** 修改每回合抽牌数量。

**示例：** `RingOfTheSnake`（第一回合多抽 2 张）、`BagOfPreparation`（第一回合多抽 2 张）、`SneckoEye`（多抽 2 张）。

#### `ModifyMaxEnergy`

```csharp
// Hook.cs:1353
public static decimal ModifyMaxEnergy(ICombatState combatState, Player player,
    decimal amount)

// AbstractModel.cs:721
public virtual decimal ModifyMaxEnergy(Player player, decimal amount) => amount;
```

**用途：** 修改能量上限。

**示例：** `VelvetChoker`（+1 能量）、`PhilosophersStone`（+1 能量）、`Sozu`（+1 能量）。

#### `ModifyShuffleOrder`

```csharp
// Hook.cs:1532
public static void ModifyShuffleOrder(ICombatState combatState, Player player,
    List<CardModel> cards, bool isInitialShuffle)

// AbstractModel.cs:760
public virtual void ModifyShuffleOrder(Player player, List<CardModel> cards,
    bool isInitialShuffle) { }
```

**用途：** 修改洗牌后的卡牌顺序（原地修改列表）。

**示例：** `PerfectFit` 附魔在洗牌时将附魔卡牌置于牌库顶。

---

## 10. 守卫与条件钩子

守卫钩子（`Should*` / `Try*`）用于控制游戏行为是否允许发生。它们的返回值决定了游戏流程的走向。

### 10.1 `ShouldPlay` — 控制卡牌是否可打出

```csharp
// Hook.cs:1828
public static bool ShouldPlay(ICombatState combatState, CardModel card,
    out AbstractModel? preventer, AutoPlayType autoPlayType)

// AbstractModel.cs:949
public virtual bool ShouldPlay(CardModel card, AutoPlayType autoPlayType) => true;
```

**逻辑：** 所有监听器**必须全部返回 `true`** 卡牌才可打出（AND 逻辑）。首个返回 `false` 的监听器被记录为 `preventer`。

#### 示例：凡庸（Normality）

- **中文名：** 凡庸
- **英文名：** Normality
- **效果：** 你在本回合不能打出超过 3 张牌。

```csharp
// src/Core/Models/Cards/Normality.cs
public sealed class Normality : CardModel
{
    private bool ShouldPreventCardPlay =>
        CardsPlayedThisTurn >= 3;  // 已打出 3 张即阻止

    private int CardsPlayedThisTurn => CombatManager.Instance.History.CardPlaysStarted
        .Count(e => e.HappenedThisTurn(base.CombatState)
                 && e.CardPlay.Card.Owner == base.Owner);

    public override bool ShouldPlay(CardModel card, AutoPlayType _)
    {
        if (card.Owner != base.Owner) return true;   // 只限制自己的牌
        // 凡庸本身不在手牌中时不影响
        CardPile? pile = base.Pile;
        if (pile == null || pile.Type != PileType.Hand) return true;

        return !ShouldPreventCardPlay;  // 超过 3 张时阻止
    }
}
```

#### 示例：天鹅绒颈圈（VelvetChoker）

- **中文名：** 天鹅绒颈圈
- **效果：** 获得 1 点能量。你每回合不能打出超过 6 张牌。

```csharp
// src/Core/Models/Relics/VelvetChoker.cs
public sealed class VelvetChoker : RelicModel
{
    // 同时使用多个钩子实现完整功能
    public override decimal ModifyMaxEnergy(Player player, decimal amount)
        => amount + base.DynamicVars.Energy.BaseValue;  // +1 能量上限

    public override bool ShouldPlay(CardModel card, AutoPlayType _)
    {
        if (card.Owner != base.Owner) return true;
        return !ShouldPreventCardPlay;  // 达到 6 张后阻止
    }

    public override Task AfterCardPlayed(PlayerChoiceContext context, CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner == base.Owner) _cardsPlayedThisTurn++;
        return Task.CompletedTask;
    }

    public override Task BeforeSideTurnStart(PlayerChoiceContext choiceContext,
        CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        _cardsPlayedThisTurn = 0;  // 每回合重置计数
        return Task.CompletedTask;
    }
}
```

---

### 10.2 `ShouldAddToDeck`

```csharp
// Hook.cs:1594
public static bool ShouldAddToDeck(IRunState runState, CardModel card,
    out AbstractModel? preventer)

// AbstractModel.cs:864
public virtual bool ShouldAddToDeck(CardModel card) => true;
```

**用途：** 阻止某些卡牌被加入牌库（旅程级别，非战斗中）。

#### 自定义示例：阻止状态牌进入牌库

```csharp
public override bool ShouldAddToDeck(CardModel card)
{
    if (card.Owner != base.Owner) return true;

    return card.Type != CardType.Status;
}

public override Task AfterAddToDeckPrevented(CardModel card)
{
    if (card.Owner == base.Owner)
        Flash();
    return Task.CompletedTask;
}
```

`ShouldAddToDeck` 只负责 veto；如需播放反馈、消耗层数或记录日志，使用 `AfterAddToDeckPrevented` 响应真正被阻止的结果。

---

### 10.3 `ShouldPlayerResetEnergy`

```csharp
// Hook.cs:1842
public static bool ShouldPlayerResetEnergy(ICombatState combatState, Player player)

// AbstractModel.cs:954
public virtual bool ShouldPlayerResetEnergy(Player player) => true;
```

**用途：** 返回 `false` 阻止回合间能量重置（即保留能量）。

#### 示例：冰淇淋（IceCream）

- **中文名：** 冰淇淋
- **效果：** 多余的能量可以留到下一回合。

```csharp
// src/Core/Models/Relics/IceCream.cs
public sealed class IceCream : RelicModel
{
    public override bool ShouldPlayerResetEnergy(Player player)
    {
        if (player.Creature.CombatState.RoundNumber == 1) return true;  // 第一回合正常
        if (player != base.Owner) return true;
        return false;  // 阻止重置——保留能量
    }
}
```

> **极简实现：** 只需 20 行代码，只覆盖一个钩子，实现一个经典遗物效果。这是钩子系统强大灵活的最佳范例。

---

### 10.4 其他 Should 守卫钩子

| 钩子 | AbstractModel 行号 | 说明 | 示例 |
|------|-------------------|------|------|
| `ShouldEtherealTrigger` | 919 | 返回 false 阻止虚无自动消耗 | — |
| `ShouldAfflict` | 869 | 返回 false 阻止对卡牌施加苦难 | — |
| `ShouldAllowMerchantCardRemoval` | 979 | 返回 false 阻止商人的卡牌移除 | `Hoarder` 修改器 |
| `ShouldAllowSelectingMoreCardRewards` | 889 | 返回 true 允许选择额外卡牌奖励（OR 逻辑） | — |
| `ShouldClearBlock` | — | 返回 false 阻止回合开始清空格挡 | `BarricadePower`（壁垒）、`SturdyClamp` |
| `ShouldDraw` | — | 返回 false 阻止抽牌 | `NoDrawPower` |
| `ModifyGoldGained` | — | 返回 0 阻止获得金币（`ShouldGainGold` 已移除） | `Ectoplasm`（灵体外质） |
| `ShouldDie` / `ShouldDieLate` | — | 返回 false 阻止死亡 | `LizardTail`（蜥蜴尾巴）、`FairyInABottle` |

---

### 10.5 `IsPlayable` — 卡牌级可打出条件

除了 `ShouldPlay` 全局钩子，卡牌本身还可以通过重写 `IsPlayable` 属性来控制自己能否被打出：

```csharp
// CardModel.cs
protected virtual bool IsPlayable => true;
```

#### 示例：交锋（Clash）

- **中文名：** 交锋
- **效果：** 只有在手牌中每一张牌都是攻击牌时才能被打出。造成 14 点伤害。

```csharp
// src/Core/Models/Cards/Clash.cs
public sealed class Clash : CardModel
{
    protected override bool IsPlayable =>
        CardPile.GetCards(base.Owner, PileType.Hand)
            .All(c => c.Type == CardType.Attack);

    protected override bool ShouldGlowGoldInternal => IsPlayable;  // 可打出时发光
}
```

#### 示例：华丽收场（GrandFinale）

- **中文名：** 华丽收场
- **效果：** 只能在抽牌堆为空时打出。

```csharp
// src/Core/Models/Cards/GrandFinale.cs
protected override bool IsPlayable =>
    PileType.Draw.GetPile(owner).Cards.Count == 0;
```

---

## 11. 修改后通知钩子

这些钩子在数值修改**完成之后**触发，用于视觉反馈（如闪光效果）或后续逻辑触发。它们以 `AfterModifying*` 命名。

### 11.1 `AfterModifyingDamageAmount`

```csharp
// Hook.cs:506
public static async Task AfterModifyingDamageAmount(IRunState runState,
    ICombatState? combatState, CardModel? cardSource, IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs:386
public virtual Task AfterModifyingDamageAmount(CardModel? cardSource)
    => Task.CompletedTask;
```

**触发时机：** 在 `ModifyDamage` 管道完成之后。系统只通知**实际参与了伤害修改的**那些监听器（通过 `modifiers` 列表）。

#### 示例：无实体（IntangiblePower）

```csharp
// IntangiblePower.cs:49
public override Task AfterModifyingDamageAmount(CardModel? cardSource)
{
    Flash();  // 在伤害被限制为 1 时闪光
    return Task.CompletedTask;
}
```

---

### 11.2 `AfterEnergyReset` / `AfterEnergyResetLate`

```csharp
// Hook.cs:362
public static async Task AfterEnergyReset(ICombatState combatState, Player player)

// AbstractModel.cs:301,306
public virtual Task AfterEnergyReset(Player player) => Task.CompletedTask;
public virtual Task AfterEnergyResetLate(Player player) => Task.CompletedTask;
```

**触发时机：** 回合开始时能量重置后。先 Normal 后 Late。

**示例：** `VenerableTeaSet`（休息后额外能量）、`ArtOfWar`（战斗开始时额外能量）。

---

### 11.3 `AfterEnergySpent`

```csharp
// Hook.cs:376
public static async Task AfterEnergySpent(ICombatState combatState,
    CardModel card, int amount)

// AbstractModel.cs:311
public virtual Task AfterEnergySpent(CardModel card, int amount)
    => Task.CompletedTask;
```

**触发时机：** 能量因卡牌被消耗后。

**示例：** `OrbitPower` — 能量花费时触发轨道效果。

---

### 11.4 `AfterModifyingBlockAmount`

```csharp
// Hook.cs:470
public static async Task AfterModifyingBlockAmount(ICombatState combatState,
    decimal modifiedBlock, CardModel? cardSource, CardPlay? cardPlay,
    IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs:361
public virtual Task AfterModifyingBlockAmount(decimal modifiedAmount,
    CardModel? cardSource, CardPlay? cardPlay) => Task.CompletedTask;
```

**示例：** `FastenPower`、`PaelsLegion`、`Vambrace` 在格挡数值被修改后闪光或消耗自身状态。

```csharp
public override Task AfterModifyingBlockAmount(decimal modifiedAmount,
    CardModel? cardSource, CardPlay? cardPlay)
{
    if (modifiedAmount <= 0m) return Task.CompletedTask;

    Flash();
    return Task.CompletedTask;
}
```

---

### 11.5 `AfterModifyingCardPlayCount`

```csharp
// Hook.cs:482
public static async Task AfterModifyingCardPlayCount(ICombatState combatState,
    CardModel card, IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs:366
public virtual Task AfterModifyingCardPlayCount(CardModel card)
    => Task.CompletedTask;
```

**示例：** `EchoFormPower`、`BurstPower`、`DuplicationPower` — 在重播次数修改后减少自身层数或闪光。

---

### 11.6 `AfterModifyingHandDraw` / `AfterModifyingCardRewardOptions`

```csharp
// Hook.cs:530
public static async Task AfterModifyingHandDraw(ICombatState combatState,
    IEnumerable<AbstractModel> modifiers)

// Hook.cs:494
public static async Task AfterModifyingCardRewardOptions(IRunState runState,
    IEnumerable<AbstractModel> modifiers)
```

**用途：** 在抽牌数量或奖励选项被修改后通知相关监听器。

#### 示例：抽牌数量修改后反馈

```csharp
public override decimal ModifyHandDraw(Player player, decimal count)
{
    if (player != base.Owner) return count;
    return count + 2m;
}

public override Task AfterModifyingHandDraw()
{
    Flash();
    return Task.CompletedTask;
}
```

#### 示例：奖励选项修改后反馈

```csharp
public override bool TryModifyCardRewardOptions(Player player,
    List<CardCreationResult> cardRewardOptions,
    CardCreationOptions creationOptions)
{
    if (player != base.Owner) return false;

    foreach (CardCreationResult option in cardRewardOptions)
        CardCmd.Upgrade(option.Card, CardPreviewStyle.None);
    return true;
}

public override Task AfterModifyingCardRewardOptions()
{
    Flash();
    return Task.CompletedTask;
}
```

**实际覆写参考：** `Pocketwatch`、`PollinousCore`、`MindRotPower` 使用 `AfterModifyingHandDraw`；`SilkenTress`、`SilverCrucible` 使用 `AfterModifyingCardRewardOptions`。

---

## 12. HP/伤害相关修改钩子

本章覆盖与 HP 损失计算、伤害转移、能量获取和最大能量相关的修改钩子。这些钩子构成了游戏中"伤害最终如何落到目标身上"的完整流水线。

### 12.1 `ModifyHpLostBeforeOsty` — 减血前修改（Osty 之前）

```csharp
// 无独立 Hook 静态入口：统一通过 Hook.ModifyHpLost(..., HpLossHookPhase.BeforeOsty, ...) 调度（见 12.4）。
// 下面是 AbstractModel 虚方法（在 RelicModel/PowerModel 中重写）：
public virtual decimal ModifyHpLostBeforeOsty(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource) => amount;
public virtual decimal ModifyHpLostBeforeOstyLate(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource) => amount;
```

**触发时机：** 在 `Osty`（护盾/格挡吸收）处理**之前**，即在格挡值被扣除之前修改将要损失的生命值。这是伤害流水线的第一阶段。

**参数说明：**
- `target`: 受到伤害的目标
- `amount`: 当前计算的 HP 损失量
- `props`: 伤害属性（是否攻击、伤害类型等）
- `dealer`: 伤害来源
- `cardSource`: 造成伤害的卡牌

**执行顺序：** `ModifyHpLostBeforeOsty` → `ModifyHpLostBeforeOstyLate`

---

**示例：发条靴（The Boot）**

> 中文效果：如果你的攻击伤害没有超过 `4` 点，将其提升至 `5` 点。

```csharp
// TheBoot.cs — 伤害保底机制
public override decimal ModifyHpLostBeforeOsty(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource)
{
    if (dealer != base.Owner.Creature)
        return amount;
    if (!props.IsPoweredAttack())    // 只对"攻击"生效
        return amount;
    if (amount < 1m) return amount;
    if (amount >= base.DynamicVars["DamageMinimum"].BaseValue)  // >= 5 不处理
        return amount;
    return base.DynamicVars["DamageMinimum"].BaseValue;  // 提升到 5
}

public override Task AfterModifyingHpLostBeforeOsty()
{
    Flash();  // 遗物闪光
    return Task.CompletedTask;
}
```

**解析：** 发条靴在 Osty（格挡减免）之前检查伤害量。如果攻击伤害低于 5，直接将其提升到 5。这是"伤害保底"——无论格挡多少，至少造成 5 点伤害的基础量。

**关键点：** `ModifyHpLostBeforeOsty` 发生在格挡计算**之前**，所以发条靴提升的是"原始伤害"，然后格挡才从中减免。如果这个钩子放在 Osty 之后，5 点伤害会被格挡几乎完全吸收。

---

### 12.2 `ModifyHpLostAfterOsty` — 减血后修改（Osty 之后）

```csharp
// 无独立 Hook 静态入口：统一通过 Hook.ModifyHpLost(..., HpLossHookPhase.AfterOsty, ...) 调度（见 12.4）。
// 下面是 AbstractModel 虚方法：
public virtual decimal ModifyHpLostAfterOsty(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource) => amount;
public virtual decimal ModifyHpLostAfterOstyLate(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource) => amount;
```

**触发时机：** 在格挡已经吸收了部分伤害**之后**，对剩余将要扣除 HP 的数值进行修改。这是伤害流水线的第二阶段。

**执行顺序：** `ModifyHpLostAfterOsty` → `ModifyHpLostAfterOstyLate`

---

**示例1：钨合金棍（Tungsten Rod）**

> 中文效果：每当你的 HP 将要降低时，少降低 `1` 点。

```csharp
// TungstenRod.cs — 伤害减免
public override decimal ModifyHpLostAfterOsty(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource)
{
    if (target != base.Owner.Creature) return amount;
    return Math.Max(0m, amount - base.DynamicVars["HpLossReduction"].BaseValue);
    // 每次减少 1 点（最低降到 0）
}

public override Task AfterModifyingHpLostAfterOsty()
{
    Flash();
    return Task.CompletedTask;
}
```

**解析：** 钨合金棍在格挡已经减免之后，再减去 1 点。这意味着即使攻击穿透了格挡，也会被钨合金棍进一步削减。`Math.Max(0m, ...)` 确保不会把伤害减成负数（即不会变成治疗）。

---

**示例2：缓冲（Buffer Power）**

> 中文效果：防止一次 HP 损失。

```csharp
// BufferPower.cs — 完全免疫一次伤害
public override decimal ModifyHpLostAfterOstyLate(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource)
{
    if (target != base.Owner) return amount;
    return 0m;  // 直接返回 0，完全免疫
}

public override async Task AfterModifyingHpLostAfterOsty()
{
    await PowerCmd.Decrement(this);  // 消耗一层
}
```

**解析：** 缓冲使用 `Late` 阶段返回 0，直接免疫所有伤害。使用 `Late` 意味着它在所有其他修改器之后执行，确保伤害被完全归零。之后通过 `PowerCmd.Decrement` 消耗一层。

---

**示例3：无实体（Intangible Power）**

> 中文效果：将受到的 HP 损失降低为 `1`。

```csharp
// IntangiblePower.cs — 伤害上限
public override decimal ModifyHpLostAfterOsty(Creature target, decimal amount,
    ValueProp props, Creature? dealer, CardModel? cardSource)
{
    if (!CombatManager.Instance.IsInProgress) return amount;
    if (target != base.Owner) return amount;
    return Math.Min(GetDamageCap(dealer), amount);
}

public override decimal ModifyDamageCap(Creature? target, ValueProp props,
    Creature? dealer, CardModel? cardSource)
{
    if (target != base.Owner) return decimal.MaxValue;
    return GetDamageCap(dealer);
}

private int GetDamageCap(Creature? dealer)
{
    // 特殊互动：如果伤害来源有发条靴，伤害上限为 5 而不是 1
    Player player = dealer?.Player ?? dealer?.PetOwner;
    if (player == null || !player.Relics.Any((RelicModel r) => r is TheBoot))
        return 1;
    return 5;
}
```

**解析：** 无实体是一个经典的能力，它通过 `ModifyHpLostAfterOsty` 将每次受到的伤害上限限制在 1 点（或如果攻击者有发条靴则为 5 点）。它还实现了 `ModifyDamageCap` 来控制伤害预览显示。这个遗物交互（发条靴 + 无实体 = 5 点伤害上限）是一个精妙的游戏设计。

---

### 12.3 `ModifyUnblockedDamageTarget` — 修改未格挡伤害目标

```csharp
// Hook.cs:1564
public static Creature ModifyUnblockedDamageTarget(ICombatState combatState,
    Creature originalTarget, decimal amount, ValueProp props, Creature? dealer)

// AbstractModel.cs:769
public virtual Creature ModifyUnblockedDamageTarget(Creature target, decimal _,
    ValueProp props, Creature? __) => target;
```

**触发时机：** 当有未格挡的伤害要打到某个目标身上时，允许修改实际承受伤害的目标。

---

**示例：为你而死（Die For You Power）**

> 中文效果：宠物代替主人承受所有未格挡的攻击伤害。

```csharp
// DieForYouPower.cs — 宠物挡刀
public override Creature ModifyUnblockedDamageTarget(Creature target, decimal _,
    ValueProp props, Creature? __)
{
    if (target != base.Owner.PetOwner?.Creature)  // 只保护主人
        return target;
    if (base.Owner.IsDead)                          // 自己死了就保护不了
        return target;
    if (!props.IsPoweredAttack())                   // 只对攻击伤害生效
        return target;
    return base.Owner;  // 将伤害目标从主人转移到自己
}

public override bool ShouldAllowHitting(Creature creature)
{
    return creature.IsAlive;  // 死后不再允许被击中
}

public override bool ShouldCreatureBeRemovedFromCombatAfterDeath(Creature creature)
{
    if (creature != base.Owner) return true;
    return false;  // 死后留在场上（因为还有效果要展示）
}
```

**解析：** 为你而死是一个多层钩子协同的例子。核心是 `ModifyUnblockedDamageTarget`——当主人的攻击伤害穿过格挡后，实际承受伤害的目标被替换为宠物。配合 `ShouldAllowHitting` 和 `ShouldCreatureBeRemovedFromCombatAfterDeath` 确保宠物死后不再被选为目标、且尸体留在场上展示。

---

### 12.4 `ModifyHpLost` — HP 损失汇总入口

```csharp
// Hook.cs:1285
public static decimal ModifyHpLost(IRunState runState, ICombatState? combatState,
    Creature target, decimal amount, ValueProp props, Creature? dealer,
    CardModel? cardSource, HpLossHookPhase phases,
    out IEnumerable<AbstractModel> modifiers)
```

**用途：** 这是 Hook 层公开的 HP 损失汇总调度入口，会按 `HpLossHookPhase` 选择并串联 `ModifyHpLostBeforeOsty` / `ModifyHpLostAfterOsty` 及其 Late 阶段。作者通常不覆写这个入口本身，而是在 `AbstractModel` 层重写前后两个具体阶段。

#### 调用链示例：命令侧统一计算 HP 损失

```csharp
IEnumerable<AbstractModel> modifiers;
decimal hpLoss = Hook.ModifyHpLost(runState, combatState, target,
    rawAmount, props, dealer, cardSource,
    HpLossHookPhase.BeforeOsty | HpLossHookPhase.AfterOsty,
    out modifiers);

await Hook.AfterModifyingHpLostBeforeOsty(runState, combatState, modifiers);
await Hook.AfterModifyingHpLostAfterOsty(runState, combatState, modifiers);
```

`ModifyHpLost` 是命令/引擎层的聚合入口，不是模型作者的覆写点。Mod 作者要改变伤害落地数值时，仍应覆写 `ModifyHpLostBeforeOsty*` 或 `ModifyHpLostAfterOsty*`。

---

### 12.5 `ModifyEnergyGain` — 修改能量获取

```csharp
// Hook.cs:1229
public static decimal ModifyEnergyGain(ICombatState combatState, Player player,
    decimal amount, out IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs:676
public virtual decimal ModifyEnergyGain(Player player, decimal amount) => amount;
```

**触发时机：** 每当玩家将要获得能量时（回合开始回能、卡牌效果回能等）。

---

**示例：无法获得能量（No Energy Gain Power）**

> 中文效果：无法获得任何能量。

```csharp
// NoEnergyGainPower.cs — 能量封锁
public override decimal ModifyEnergyGain(Player player, decimal amount)
{
    if (player != base.Owner.Player) return amount;
    return 0m;  // 直接返回 0，屏蔽所有能量获取
}

public override Task AfterModifyingEnergyGain()
{
    Flash();
    return Task.CompletedTask;
}

public override async Task AfterSideTurnEnd(PlayerChoiceContext choiceContext,
    CombatSide side, IEnumerable<Creature> participants)
{
    await PowerCmd.Remove(this);  // 回合结束时自动移除
}
```

**解析：** 这个能力将任何能量获取直接返回 0，实现完全的能量封锁。它在回合结束时自动移除，是一个临时 debuff。

---

### 12.6 `ModifyMaxEnergy` — 修改最大能量

```csharp
// Hook.cs:1353
public static decimal ModifyMaxEnergy(ICombatState combatState, Player player,
    decimal amount)

// AbstractModel.cs:721
public virtual decimal ModifyMaxEnergy(Player player, decimal amount) => amount;
```

**触发时机：** 每回合开始时计算玩家最大能量时。

**示例：** 天鹅绒颈圈（Velvet Choker）— 最大能量 +1，但每回合只能打出有限数量的卡牌。Boss 圣物通过 `ModifyMaxEnergy` 增加能量上限，同时通过 `ShouldPlay` 限制出牌数。

---

### 12.7 `ShouldPlayerResetEnergy` — 玩家是否重置能量

```csharp
// Hook.cs:1842
public static bool ShouldPlayerResetEnergy(ICombatState combatState, Player player)

// AbstractModel.cs:954
public virtual bool ShouldPlayerResetEnergy(Player player) => true;
```

**示例：** 冰淇淋（Ice Cream）— 返回 `false`，使能量不会在回合结束时清空，多余的能量保留到下一回合。

---

## 13. 能力/充能球/能量相关钩子

### 13.1 `ModifyPowerAmountGiven` — 修改给予的能力层数

```csharp
// Hook.cs:1443
public static decimal ModifyPowerAmountGiven(ICombatState combatState, PowerModel power,
    Creature giver, decimal amount, Creature? target, CardModel? cardSource,
    out IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs:750
public virtual decimal ModifyPowerAmountGiven(PowerModel power, Creature giver,
    decimal amount, Creature? target, CardModel? cardSource) => amount;
```

**触发时机：** 当任何来源将要给予目标一个能力时，在应用之前修改层数。

---

**示例：异蛇头骨（Snecko Skull）**

> 中文效果：每当你给予敌人中毒时，所给予的中毒层数增加 `1` 层。

```csharp
// SneckoSkull.cs — 中毒层数加成
public override decimal ModifyPowerAmountGiven(PowerModel power, Creature giver,
    decimal amount, Creature? target, CardModel? cardSource)
{
    if (!(power is PoisonPower))     // 只对中毒能力生效
        return amount;
    if (giver != base.Owner.Creature) // 必须是持有者给予的
        return amount;
    return amount + (decimal)base.DynamicVars.Poison.IntValue;  // +1 层
}

public override Task AfterModifyingPowerAmountGiven(PowerModel power)
{
    Flash();
    return Task.CompletedTask;
}
```

**解析：** 异蛇头骨通过类型检查 `power is PoisonPower` 精确匹配目标能力类型。使用 `base.DynamicVars.Poison.IntValue` 获取动态变量值（这里固定为 1），确保数值在卡牌描述中正确显示。

---

### 13.2 `ModifyPowerAmountReceived` / `TryModifyPowerAmountReceived` — 修改接收的能力层数

```csharp
// Hook.cs:1460
public static decimal ModifyPowerAmountReceived(ICombatState combatState,
    PowerModel canonicalPower, Creature target, decimal amount, Creature? giver,
    out IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs:833
public virtual bool TryModifyPowerAmountReceived(PowerModel canonicalPower,
    Creature target, decimal amount, Creature? giver, out decimal modifiedAmount)
{
    modifiedAmount = amount;
    return false;
}
```

**触发时机：** 当目标将要接收一个能力时，允许完全阻止或修改层数。Hook 层公开入口名是 `ModifyPowerAmountReceived`，内部遍历 `AbstractModel.TryModifyPowerAmountReceived`；模型覆写层仍使用 `TryModify` 模式（返回 `bool` + `out` 参数），意味着一旦某个修改器返回 `true`，后续修改器将不再执行。

---

**示例：人工制品（Artifact Power）**

> 中文效果：阻止一次 debuff。

```csharp
// ArtifactPower.cs — 阻挡 debuff
public override bool TryModifyPowerAmountReceived(PowerModel canonicalPower,
    Creature target, decimal amount, Creature? _, out decimal modifiedAmount)
{
    if (target != base.Owner) { modifiedAmount = amount; return false; }

    // 只阻挡 Debuff 类型的能力
    if (canonicalPower.GetTypeForAmount(amount) != PowerType.Debuff)
    { modifiedAmount = amount; return false; }

    // 不可见的能力不阻挡
    if (!canonicalPower.IsVisible)
    { modifiedAmount = amount; return false; }

    modifiedAmount = default(decimal);  // 设为 0 层
    return true;  // 已处理，阻止后续修改器
}

public override async Task AfterModifyingPowerAmountReceived(PowerModel power)
{
    await PowerCmd.Decrement(this);  // 消耗一层人工制品
}
```

**解析：** 人工制品使用 `TryModify` 模式，`return true` 意味着"我已处理完毕"。关键逻辑：
1. 只对 Debuff 类型生效（不阻挡 Buff）
2. 不可见的 debuff 不阻挡（如某些内部标记）
3. 将 `modifiedAmount` 设为 `default(decimal)`（即 0），完全阻止
4. 然后消耗一层人工制品

**与 `ModifyPowerAmountGiven` 的区别：** `TryModify` 返回 `bool` 有短路语义——一旦返回 `true`，后续修改器不再迭代。适合"一次性完全阻止"的场景。

---

### 13.3 `ModifyOrbValue` — 修改充能球数值

```csharp
// Hook.cs:2018
public static decimal ModifyOrbValue(ICombatState combatState, OrbModel orb, decimal amount)

// AbstractModel.cs:1796
public virtual decimal ModifyOrbValue(OrbModel orb, decimal value) => value;
```

**触发时机：** 当充能球的被动数值（如闪电球的伤害、冰霜球的格挡）需要计算时。

---

**示例：集中（Focus Power）**

> 中文效果：充能球的数值增加集中层数。

```csharp
// FocusPower.cs — 充能球数值加成
public override decimal ModifyOrbValue(OrbModel orb, decimal value)
{
    if (base.Owner.Player != orb.Owner) return value;  // 只对自己的充能球生效
    return Math.Max(value + (decimal)base.Amount, 0m);  // 不低于 0
}
```

**解析：** 集中是充能球体系的核心能力。它将自身的层数直接加到充能球的数值上。`Math.Max(..., 0m)` 确保即使集中为负数（可通过某些卡牌降集中），充能球数值也不会变成负数。`AllowNegative = true` 允许集中自身为负值。

---

### 13.4 `ModifyOrbPassiveTriggerCount` — 修改充能球被动触发次数

```csharp
// Hook.cs:1999 — 静态入口为单数 ModifyOrbPassiveTriggerCount，带 out 修改器列表
public static int ModifyOrbPassiveTriggerCount(ICombatState combatState,
    OrbModel orb, int triggerCount, out List<AbstractModel> modifyingModels)

// AbstractModel.cs:1500 — 虚方法为复数 ModifyOrbPassiveTriggerCounts
public virtual int ModifyOrbPassiveTriggerCounts(OrbModel orb,
    int triggerCount) => triggerCount;
```

**用途：** 允许修改充能球每回合被动的触发次数。例如，某些能力可以让充能球触发两次。

---

## 14. Should 守卫钩子详解

本章详细分析关键的 `Should*` 守卫钩子。这些钩子通过返回 `bool` 来控制游戏行为是否允许执行——`false` 意味着阻止该行为。

### 14.1 `ShouldDie` / `ShouldDieLate` — 是否应该死亡

```csharp
// Hook.cs:1708
public static bool ShouldDie(IRunState runState, ICombatState? combatState,
    Creature creature, out AbstractModel? preventer)

// AbstractModel.cs:899
public virtual bool ShouldDie(Creature creature) => true;
// AbstractModel.cs:904
public virtual bool ShouldDieLate(Creature creature) => true;
```

**触发时机：** 当生物 HP 降到 0 或以下时，检查是否真的应该死亡。

**执行顺序：** `ShouldDie` → `ShouldDieLate`

**关键设计：** 注意 Hook.cs 中的特殊处理——当 `ShouldDie` 阻止死亡后，会调用 `AfterPreventingDeath` 让阻止者执行后续逻辑（如闪光、回血）。

---

**示例：蜥蜴尾巴（Lizard Tail）**

> 中文效果：当你本场战斗第一次濒死时，防止这次死亡并回复最大生命值的 `50%`。

```csharp
// LizardTail.cs — 免死金牌
public override bool ShouldDieLate(Creature creature)
{
    if (creature != base.Owner.Creature) return true;  // 不是持有者，不管
    if (WasUsed) return true;                            // 已经用过了，允许死亡
    return false;  // 阻止死亡！
}

public override async Task AfterPreventingDeath(Creature creature)
{
    Flash();
    WasUsed = true;  // 标记已使用（之后允许正常死亡）
    decimal amount = Math.Max(1m, (decimal)creature.MaxHp
        * (base.DynamicVars.Heal.BaseValue / 100m));
    await CreatureCmd.Heal(creature, amount);  // 回血 50%
}
```

**解析：** 蜥蜴尾巴使用 `ShouldDieLate` 而不是 `ShouldDie`。`Late` 阶段确保其他正常能力检查完毕后，蜥蜴尾巴作为最后一道防线介入。阻止死亡后，`AfterPreventingDeath` 回调被触发，执行回血并将自己标记为已使用。

---

### 14.2 `ShouldClearBlock` — 是否清除格挡

```csharp
// Hook.cs:1682
public static bool ShouldClearBlock(ICombatState combatState, Creature creature,
    out AbstractModel? preventer)

// AbstractModel.cs:894
public virtual bool ShouldClearBlock(Creature creature) => true;
```

**触发时机：** 每回合开始时，系统检查是否清除该生物的格挡值。

---

**示例：壁垒（Barricade Power）**

> 中文效果：格挡不再在你的回合开始时清除。

```csharp
// BarricadePower.cs — 保留格挡
public override bool ShouldClearBlock(Creature creature)
{
    if (base.Owner != creature) return true;  // 不是持有者，正常清除
    return false;  // 持有者不清除格挡！
}
```

**解析：** 壁垒是游戏中最简洁的能力实现之一——仅仅通过返回 `false` 阻止格挡清除，就实现了"格挡永久保留"的核心机制。

---

### 14.3 `ShouldPlay` — 是否允许打出卡牌

```csharp
// Hook.cs:1828
public static bool ShouldPlay(ICombatState combatState, CardModel card,
    out AbstractModel? preventer, AutoPlayType autoPlayType)

// AbstractModel.cs:949
public virtual bool ShouldPlay(CardModel card, AutoPlayType autoPlayType) => true;
```

**触发时机：** 当玩家试图打出一张卡牌时（包括手动打出和自动打出）。

---

**示例1：执迷（Enthralled）**

> 中文效果：诅咒。永恒。你手牌中不能打出其他卡牌。

```csharp
// Enthralled.cs — 封锁手牌
public override bool ShouldPlay(CardModel card, AutoPlayType autoPlayType)
{
    if (card.Owner != base.Owner) return true;
    CardPile? pile = base.Pile;
    if (pile == null || pile.Type != PileType.Hand) return true;
    if (card is Enthralled) return true;       // 可以打出另一张执迷
    if (autoPlayType != AutoPlayType.None) return true;  // 自动打出不受限
    return false;  // 手牌中的其他卡牌不能打出！
}

public override bool CanBeGeneratedByModifiers => false;  // 不能被随机生成
public override int MaxUpgradeLevel => 0;                 // 不能升级
```

**解析：** 执迷是一张极为特殊的诅咒卡——它在手牌中时阻止打出任何其他卡牌（只能打出执迷本身）。`ShouldPlay` 的逻辑层层过滤：
1. 不是持有者的卡牌 → 允许
2. 自己不在手牌中 → 允许（不封锁）
3. 要打出的是另一张执迷 → 允许
4. 自动打出（AutoPlayType.None 以外）→ 允许

注意自动打出豁免——这是为了避免与其他系统产生死锁。

---

**示例2：魂缚锁链（Chains of Binding Power）**

> 中文效果：抽到的第一张牌被束缚，每回合只能打出一张被束缚的牌。

```csharp
// ChainsOfBindingPower.cs — 每回合限制被束缚的牌
private class Data { public bool boundCardPlayed; }

public override async Task AfterCardDrawn(PlayerChoiceContext choiceContext,
    CardModel card, bool fromHandDraw)
{
    // 给抽到的第一张牌附加束缚
    if (card.Owner == base.Owner.Player && ...)
    {
        int num = CombatManager.Instance.History.Entries
            .OfType<CardAfflictedEntry>()
            .Count(e => e.HappenedThisTurn(...) && e.Actor == base.Owner
                && e.Affliction is Bound);
        if (num < base.Amount)
            await CardCmd.AfflictAndPreview<Bound>(...);
    }
}

public override Task BeforeCardPlayed(CardPlay cardPlay)
{
    // 标记已打出一张被束缚的牌
    if (cardPlay.Card.Affliction is Bound)
        GetInternalData<Data>().boundCardPlayed = true;
    return Task.CompletedTask;
}

public override bool ShouldPlay(CardModel card, AutoPlayType autoPlayType)
{
    if (!(card.Affliction is Bound)) return true;
    // 已经打过一张被束缚的牌，不能再打
    return !GetInternalData<Data>().boundCardPlayed;
}

public override Task BeforeSideTurnEnd(PlayerChoiceContext choiceContext,
    CombatSide side, IEnumerable<Creature> participants)
{
    GetInternalData<Data>().boundCardPlayed = false;  // 重置状态
    // 清除所有束缚
    foreach (var item in allCards)
        if (item.Affliction is Bound) CardCmd.ClearAffliction(item);
    return Task.CompletedTask;
}
```

**解析：** 魂缚锁链展示了多个钩子如何协同实现复杂机制：
1. `AfterCardDrawn` — 给抽到的牌附加 `Bound` 刻印
2. `BeforeCardPlayed` — 标记第一张 Bound 牌已打出
3. `ShouldPlay` — 检查是否已经打过 Bound 牌，如果是则阻止打出更多
4. `BeforeSideTurnEnd` — 回合结束时重置状态并清除所有 Bound 刻印

使用内部数据类 `Data` 管理状态，通过 `GetInternalData<T>()` 存取。

---

### 14.4 `ShouldDraw` — 是否允许抽牌

```csharp
// Hook.cs:1742
public static bool ShouldDraw(ICombatState combatState, Player player,
    bool fromHandDraw, out AbstractModel? modifier)

// AbstractModel（通常在 PowerModel/RelicModel 中重写）
public virtual bool ShouldDraw(Player player, bool fromHandDraw) => true;
```

**用途：** 阻止抽牌。例如某些 debuff 能力可以限制每回合的抽牌数量。

---

### 14.5 ~~`ShouldGainGold`~~（v0.107.1 已移除）— 改用 `ModifyGoldGained`

> **该守卫钩子已不存在。** v0.107.1 全量 corpus 中无 `ShouldGainGold`（static / virtual 皆无）。金币没有 `Should*` 守卫——要阻止或修改获得的金币，覆写 `ModifyGoldGained`（返回 `0` 即等价于"阻止获得"）。星能仍有守卫 `ShouldGainStars`（见 14.7）。

```csharp
// AbstractModel.cs:1602 — 修改金币获得量（返回 0 = 不获得）
public virtual decimal ModifyGoldGained(Player player, decimal amount) => amount;
```

#### 实际覆写参考：灵体外质（Ectoplasm）

```csharp
// Ectoplasm.cs — 持有者不再获得金币
public override decimal ModifyGoldGained(Player player, decimal amount)
{
    if (player != base.Owner) return amount;
    return 0m;   // 阻止获得金币
}
```

> 详见第 9 章数值修改钩子中的 `ModifyGoldGained` / `AfterModifyingGoldGained`。

---

### 14.6 `ShouldEtherealTrigger` — 是否触发虚无

```csharp
// Hook.cs:1756
public static bool ShouldEtherealTrigger(ICombatState combatState, CardModel card)

// AbstractModel.cs:919
public virtual bool ShouldEtherealTrigger(CardModel card) => true;
```

**用途：** 控制带有 Ethereal 关键词的卡牌是否在回合结束时被消耗。某些能力可以阻止虚无触发。

#### 自定义示例：本回合保护一张虚无牌

```csharp
public override bool ShouldEtherealTrigger(CardModel card)
{
    if (card.Owner != base.Owner) return true;
    if (card != ProtectedCard) return true;

    return false;
}
```

当前 0.106.1 本体未发现实际覆写；该入口主要留给 Mod 实现“本回合保留一张虚无牌”这类保护效果。

---

### 14.7 `ShouldGainStars` — 是否允许获得星能

```csharp
// Hook.cs:1804
public static bool ShouldGainStars(ICombatState combatState, decimal amount,
    Player player)

// AbstractModel.cs:934
public virtual bool ShouldGainStars(decimal amount, Player player) => true;
```

**用途：** 控制星能的获取。当前版本暂无实际使用此钩子的模型。

#### 自定义示例：封锁星能获取

```csharp
public override bool ShouldGainStars(decimal amount, Player player)
{
    if (player != base.Owner) return true;

    Flash();
    return false;
}
```

该钩子返回 `false` 会阻止这次星能进入玩家资源池。（金币侧没有对应的 `Should*` 守卫，改用 `ModifyGoldGained`。）

---

## 15. 高级卡牌模式与进阶技巧

### 15.1 `BeforeDamage` VFX 回调 — 攻击前视觉特效

`DamageCmd` 提供了 `BeforeDamage` 回调，允许在伤害实际造成之前执行视觉特效。

---

**示例：恶魔之焰（Fiend Fire）**

> 中文效果：消耗所有手牌。对所有敌人造成手牌数量 `×7` 点伤害。消耗。

```csharp
// FiendFire.cs — BeforeDamage VFX
protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
{
    List<CardModel> list = PileType.Hand.GetPile(base.Owner).Cards.ToList();
    int cardCount = list.Count;

    // 先消耗所有手牌
    foreach (CardModel item in list)
        await CardCmd.Exhaust(choiceContext, item);

    float scale = 0.8f;
    await DamageCmd.Attack(base.DynamicVars.Damage.BaseValue)
        .WithHitCount(cardCount)  // 每张手牌一次攻击
        .FromCard(this)
        .Targeting(cardPlay.Target)
        .BeforeDamage(delegate
        {
            // 每次攻击前创建火焰特效
            NGroundFireVfx nGroundFireVfx = NGroundFireVfx.Create(cardPlay.Target);
            SfxCmd.Play("event:/sfx/characters/attack_fire");
            nGroundFireVfx.Scale = Vector2.One * scale;
            NCombatRoom.Instance?.CombatVfxContainer.AddChildSafely(nGroundFireVfx);
            scale += 0.1f;  // 每次攻击火焰越来越大
            return Task.CompletedTask;
        })
        .Execute(choiceContext);
}
```

**解析：** `BeforeDamage` 回调在每次攻击命中**之前**触发。恶魔之焰用它来创建递进式火焰特效——第一发火焰小，最后一发火焰大。`scale += 0.1f` 在每次回调中递增，创建视觉上的层次感。

**关键模式：** `DamageCmd.Attack(...)` → `.WithHitCount(...)` → `.FromCard(...)` → `.Targeting(...)` → `.BeforeDamage(...)` → `.Execute(...)` 是标准的攻击命令链式调用。

---

### 15.2 `IsPlayable` — 条件可打出性

```csharp
// CardModel.cs
protected virtual bool IsPlayable => true;
```

**触发时机：** 每次卡牌状态更新时检查（手牌变化、能量变化等），决定卡牌是否高亮显示为"可打出"。

---

**示例：华丽收场（Grand Finale）**

> 中文效果：只有在你的抽牌堆为空时才能打出。对所有敌人造成 `60` 点伤害。

```csharp
// GrandFinale.cs — 条件可用
protected override bool ShouldGlowGoldInternal => IsPlayable;
// 当可打出时，卡牌发出金色闪光

protected override bool IsPlayable =>
    PileType.Draw.GetPile(base.Owner).Cards.Count == 0;
// 抽牌堆为空时才能打出

protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
{
    await DamageCmd.Attack(base.DynamicVars.Damage.BaseValue)
        .FromCard(this)
        .TargetingAllOpponents(base.CombatState)
        .WithHitFx("vfx/vfx_attack_slash", null, "blunt_attack.mp3")
        .Execute(choiceContext);
}
```

**解析：** 华丽收场的 `IsPlayable` 动态检查抽牌堆数量——只有当抽牌堆为空时才返回 `true`。配合 `ShouldGlowGoldInternal => IsPlayable`，卡牌在满足条件时会发出金色光芒提示玩家。

---

### 15.3 `CardCmd.DiscardAndDraw` — 弃牌并抽牌

**示例：计算下注（Calculated Gamble）**

> 中文效果：弃掉所有手牌，然后抽等量的牌。消耗。

```csharp
// CalculatedGamble.cs — 弃牌并抽牌
protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
{
    IEnumerable<CardModel> cards = PileType.Hand.GetPile(base.Owner).Cards;
    int cardsToDraw = cards.Count();
    await CardCmd.DiscardAndDraw(choiceContext, cards, cardsToDraw);
}

protected override void OnUpgrade()
{
    AddKeyword(CardKeyword.Retain);  // 升级后获得保留关键词
}
```

**解析：** `CardCmd.DiscardAndDraw` 是一个组合命令——先弃掉手中所有牌，再抽等量的牌。升级后通过 `AddKeyword(CardKeyword.Retain)` 添加新关键词，展示了升级如何改变卡牌机制而非仅仅是数值。

---

### 15.4 `CanBeGeneratedByModifiers` — 控制随机生成

```csharp
// CardModel.cs
public virtual bool CanBeGeneratedByModifiers => true;
```

**触发时机：** 当 Mod 系统（如随机生成卡牌的遗物/能力）尝试创建卡牌时检查。

**示例：** 执迷（Enthralled）重写为 `false`，确保它永远不会被随机生成到玩家的卡组中。这是对特殊诅咒卡的保护性设计。

---

### 15.5 `ShouldGlowGoldInternal` — 卡牌金色发光

```csharp
// CardModel.cs
protected virtual bool ShouldGlowGoldInternal => false;
```

**触发时机：** 每次渲染卡牌时检查，决定卡牌是否显示金色发光效果。

**示例：** 华丽收场（Grand Finale）— 当 `IsPlayable` 时发光，提示玩家条件已满足。其他使用场景包括：有额外效果待触发、combo 准备就绪等。

---

### 15.6 `ExtraRunAssetPaths` — 额外资源加载

```csharp
// CardModel.cs
protected virtual IEnumerable<string> ExtraRunAssetPaths => Enumerable.Empty<string>();
```

**触发时机：** 游戏开始时预加载卡牌所需的额外资源。

**示例：** 恶魔之焰（Fiend Fire）— `NGroundFireVfx.AssetPaths`。由于恶魔之焰在运行时需要动态创建火焰 VFX，通过 `ExtraRunAssetPaths` 确保相关资源在开局就已加载，避免首次使用时卡顿。

---

### 15.7 钩子协同 — 完整卡牌模式总结

一张卡牌从创建到打出的完整生命周期涉及多个钩子的协同：

```
卡牌创建     → AfterCreated（设置动态变量、触发初始效果）
进入战斗     → AfterCardEnteredCombat（初始化战斗状态）
抽到手上     → AfterCardDrawn（抽牌触发的效果）
尝试打出     → IsPlayable（检查是否可打出）
              → ShouldPlay（守卫检查）
              → CanPlayCard（Mod 系统检查）
              → ModifyEnergyCostInCombat（能量消耗修改）
              → ModifyStarCost（星能消耗修改）
打出前       → BeforeCardPlayed（打出前触发）
              → BeforeDamage VFX（攻击前视觉特效）
              → OnUse（Mod 系统钩子）
打出         → OnPlay（核心逻辑：伤害/格挡/能力）
              → OnEnqueuePlayVfx（添加打出特效）
打出后       → AfterCardPlayed / AfterCardPlayedLate
伤害计算     → ModifyDamage（Additive → Multiplicative → Final）
HP 计算      → ModifyHpLostBeforeOsty → Osty（格挡吸收）
              → ModifyHpLostAfterOsty → ModifyHpLostAfterOstyLate
进入弃牌堆   → AfterCardChangedPiles
回合结束     → OnTurnEndInHand / RemoveAtEndOfTurn / AtEndOfTurn
消耗         → AfterCardExhausted
```

理解这个流水线是编写自定义卡牌、Mod 和能力的关键。

---

## 16. 能力（Power）系统钩子

能力是 Slay the Spire 2 中最核心的游戏机制之一。代码库中有超过 250 个能力文件，每个能力通过重写 `PowerModel` 和 `AbstractModel` 中的虚方法来响应各种游戏事件。

### 16.1 PowerModel 基类结构

```csharp
// src/Core/Models/PowerModel.cs
public abstract class PowerModel : AbstractModel
{
    public abstract PowerType Type { get; }           // Buff 或 Debuff
    public abstract PowerStackType StackType { get; }  // Counter 或 Single
    public virtual bool IsInstanced => false;           // 是否允许多实例共存
    public virtual bool AllowNegative => false;         // 层数是否可为负
    public virtual bool ShouldScaleInMultiplayer => false;
    public override bool ShouldReceiveCombatHooks => true; // 接收战斗钩子
    public Creature Owner { get; }        // 能力持有者
    public Creature? Applier { get; set; } // 能力给予者
    public Creature? Target { get; set; }  // 能力关联目标
    public int Amount { get; }             // 当前层数
}
```

**PowerType 枚举** (`src/Core/Entities/Powers/PowerType.cs`)：
- `None` — 无类型
- `Buff` — 增益（绿色显示）
- `Debuff` — 减益（红色显示）

**PowerStackType 枚举** (`src/Core/Entities/Powers/PowerStackType.cs`)：
- `Counter` — 计数器模式：层数叠加（如力量 +2，易伤 +3）
- `Single` — 单实例模式：只能存在一个实例（如腐化、壁垒）

---

### 16.2 能力生命周期钩子

这些是 `PowerModel` 自身定义的专用钩子（非继承自 `AbstractModel`）：

#### 应用前/后

```csharp
// PowerModel.cs
public virtual Task BeforeApplied(Creature target, decimal amount,
    Creature? applier, CardModel? cardSource) => Task.CompletedTask;

public virtual Task AfterApplied(Creature? applier, CardModel? cardSource)
    => Task.CompletedTask;
```

**触发时机：** `BeforeApplied` 在能力被附加到生物**之前**触发；`AfterApplied` 在附加**之后**触发。前者在 `PowerCmd.Apply` 中能力被实际添加前调用，后者在应用完成后调用。

#### 移除后

```csharp
// PowerModel.cs
public virtual Task AfterRemoved(Creature oldOwner) => Task.CompletedTask;
```

**触发时机：** 能力从生物身上被移除后触发。`oldOwner` 是能力之前附着的生物。

#### 死亡相关

```csharp
// PowerModel.cs
public virtual bool ShouldPowerBeRemovedAfterOwnerDeath() => true;
public virtual bool ShouldOwnerDeathTriggerFatal() => true;
```

- `ShouldPowerBeRemovedAfterOwnerDeath` — 持有者死亡后能力是否移除。返回 `false` 可让能力在持有者死后继续保留（如为你而死）。
- `ShouldOwnerDeathTriggerFatal` — 持有者死亡是否算作"致命死亡"。

#### 内部状态管理

```csharp
// PowerModel.cs
protected virtual object? InitInternalData() => null;
protected T GetInternalData<T>();
```

`InitInternalData` 在能力创建时调用一次，返回一个内部数据对象。`GetInternalData<T>()` 用于在钩子中获取该数据。这是能力的"私有状态"机制——不需要在类上声明字段，状态由框架管理并在克隆时自动重建。

---

### 16.3 PowerCmd 命令系统

能力通过 `PowerCmd` 静态类进行操作。

#### Apply 流程

```
PowerCmd.Apply<T>(target, amount, applier, cardSource)
  │
  ├─ 检查战斗是否结束 → return null
  ├─ 检查目标 CanReceivePowers → return null
  ├─ 如果是新实例 || !target.HasPower<T>() → 创建新实例
  └─ 否则 → 修改现有能力的层数
       │
       ├─ Hook.BeforePowerAmountChanged（全局通知）
       ├─ Hook.ModifyPowerAmountGiven（给予者侧修改）
       ├─ Hook.ModifyPowerAmountReceived（接收者侧修改，可短路）
       ├─ power.BeforeApplied()（能力自身钩子）
       ├─ power.ApplyInternal() → SetAmount() + Owner.ApplyPowerInternal()
       ├─ CombatManager.Instance.History.PowerReceived（记录到历史）
       ├─ power.AfterApplied()（能力自身钩子）
       ├─ Hook.AfterModifyingPowerAmountGiven
       ├─ Hook.AfterModifyingPowerAmountReceived
       └─ Hook.AfterPowerAmountChanged
```

#### ModifyAmount / Decrement / Remove

```csharp
// 修改层数（可正可负）
PowerCmd.ModifyAmount(power, offset, applier, cardSource)

// 减少一层（考虑 SkipNextDurationTick）
PowerCmd.Decrement(power) → ModifyAmount(power, -1)

// 减少持续时间（回合结束调用）
PowerCmd.TickDownDuration(power) → 检查 SkipNextDurationTick → Decrement

// 移除
PowerCmd.Remove(power) → RemoveInternal() → AfterRemoved(oldOwner)
```

**关键细节：** `SkipNextDurationTick` 标志允许玩家侧 debuff 有一个"宽限期"——当 debuff 刚被应用时，跳过下一次 Tick，防止同回合被双倍扣除。

---

### 16.4 能力专用属性详解

#### IsInstanced — 多实例共存

默认 `false`。当设为 `true` 时，同一个生物身上可以同时存在多个相同能力的实例。

**示例：劫掠（Heist Power）— 多人模式掠夺标记**

```csharp
// HeistPower.cs — 每个玩家的劫掠独立存在
public override bool IsInstanced => true;

public override Task BeforeDeath(Creature target)
{
    if (base.Owner != target) return Task.CompletedTask;
    if (base.CombatState.RunState.CurrentRoom is CombatRoom combatRoom)
    {
        // 持有者死亡时，给标记的玩家返还金币
        combatRoom.AddExtraReward(base.Target.Player,
            new GoldReward(base.Amount, base.Target.Player, wasGoldStolenBack: true));
    }
    return Task.CompletedTask;
}
```

**解析：** `IsInstanced = true` 允许多个玩家各自施加劫掠到同一目标上，每个实例独立追踪其 `Target`（标记的玩家）。当目标死亡时，每个劫掠实例各自返还金币。

#### AllowNegative — 允许负层数

默认 `false`。当设为 `true` 时，能力层数可以为负数。

**示例：集中（Focus Power）— 可为负值**

```csharp
// FocusPower.cs
public override bool AllowNegative => true;
```

这允许某些卡牌降低集中，使充能球数值减少。`GetTypeForAmount` 方法会根据层数的正负动态返回 Buff/Debuff。

---

### 16.5 具体能力示例

#### 示例1：中毒（Poison Power）— DOT 模式 + 多段触发

> 中文效果：中毒的生物在回合开始时受到等同于层数的伤害，然后中毒层数减少 1。

```csharp
// PoisonPower.cs — 持续伤害 (DOT)
public override PowerType Type => PowerType.Debuff;
public override PowerStackType StackType => PowerStackType.Counter;

// TriggerCount 考虑了触媒（AccelerantPower）的加成
private int TriggerCount
{
    get
    {
        IEnumerable<Creature> opponents = from c in base.Owner.CombatState
            .GetOpponentsOf(base.Owner) where c.IsAlive select c;
        return Math.Min(base.Amount,
            1 + opponents.Sum(c => c.GetPowerAmount<AccelerantPower>()));
    }
}

public override async Task AfterSideTurnStart(CombatSide side,
    IReadOnlyList<Creature> participants, ICombatState combatState)
{
    if (side != base.Owner.Side) return;

    int iterations = TriggerCount;
    for (int i = 0; i < iterations; i++)
    {
        // 造成不可格挡、不受力量影响的伤害
        await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(),
            base.Owner, base.Amount,
            ValueProp.Unblockable | ValueProp.Unpowered, null, null);

        if (base.Owner.IsAlive)
            await PowerCmd.Decrement(this);  // 每跳一次，层数减 1
        else
            await Cmd.CustomScaledWait(0.1f, 0.25f);
    }
}
```

**解析：** 中毒演示了经典的 DOT 模式：
1. 使用 `AfterSideTurnStart` 在生物回合开始时触发
2. `TriggerCount` 计算实际触发次数：基础 1 次 + 触媒加成
3. 每次造成伤害后通过 `PowerCmd.Decrement` 减少 1 层
4. 伤害使用 `Unblockable | Unpowered` 确保原始伤害不被格挡/力量影响

---

#### 示例2：荆棘（Thorns Power）— 反伤模式

> 中文效果：每当受到攻击伤害时，对攻击者造成等同于层数的反伤。

```csharp
// ThornsPower.cs — 受伤前反伤
public override PowerType Type => PowerType.Buff;
public override PowerStackType StackType => PowerStackType.Counter;

public override async Task BeforeDamageReceived(PlayerChoiceContext choiceContext,
    Creature target, decimal amount, ValueProp props, Creature? dealer,
    CardModel? cardSource)
{
    if (target == base.Owner && dealer != null
        && (props.IsPoweredAttack() || cardSource is Omnislice))
    {
        Flash();
        // 对攻击者造成反伤
        await CreatureCmd.Damage(choiceContext, dealer, base.Amount,
            ValueProp.Unpowered | ValueProp.SkipHurtAnim, base.Owner, null);
    }
}
```

**解析：** 使用 `BeforeDamageReceived`（而非 `AfterDamageReceived`）确保反伤在伤害实际造成**之前**结算。`SkipHurtAnim` 避免反伤播放受伤动画。特别处理了 `Omnislice`（千刀万剐）这种特殊卡牌。

---

#### 示例3：黑暗拥抱（Dark Embrace Power）— 延迟结算模式

> 中文效果：每当一张牌被消耗时，抽 1 张牌。由虚无消耗的牌在回合结束时统一抽牌。

```csharp
// DarkEmbracePower.cs — 双路径结算
private class Data { public int etherealCount; }

public override PowerType Type => PowerType.Buff;
public override PowerStackType StackType => PowerStackType.Counter;

protected override object InitInternalData() => new Data();

public override async Task AfterCardExhausted(PlayerChoiceContext choiceContext,
    CardModel card, bool causedByEthereal)
{
    if (card.Owner.Creature == base.Owner)
    {
        if (causedByEthereal)
            GetInternalData<Data>().etherealCount++;  // 延迟：只计数
        else
            await CardPileCmd.Draw(choiceContext, base.Amount, base.Owner.Player);
    }
}

public override async Task AfterSideTurnEnd(PlayerChoiceContext choiceContext,
    CombatSide side, IEnumerable<Creature> participants)
{
    if (side == CombatSide.Player)
    {
        Data data = GetInternalData<Data>();
        // 回合结束时一次性抽牌（etherealCount × Amount）
        await CardPileCmd.Draw(choiceContext,
            base.Amount * data.etherealCount, base.Owner.Player);
        data.etherealCount = 0;
    }
}
```

**解析：** 黑暗拥抱是"分离结算"模式的典型案例：
1. 非虚无消耗（如腐化、主动消耗）→ 立即抽牌
2. 虚无消耗（回合结束自动消耗）→ 累计计数，回合结束时一次性抽牌
3. 使用内部类 `Data` 管理状态，通过 `InitInternalData` 初始化

这种设计避免了虚无消耗时每张牌单独抽牌导致的不流畅体验。

---

#### 示例4：撕裂（Rupture Power）— 多钩子协同模式

> 中文效果：每当你在自己的回合受到来自卡牌的未格挡伤害时，获得力量。

```csharp
// RupturePower.cs — 三钩子追踪
private class Data
{
    public readonly Dictionary<CardModel, int> playedCards = new();
}

public override PowerType Type => PowerType.Buff;
public override PowerStackType StackType => PowerStackType.Counter;

protected override object InitInternalData() => new Data();

// 钩子1：记录打出的每张牌
public override Task BeforeCardPlayed(CardPlay cardPlay)
{
    if (cardPlay.Card.Owner.Creature != base.Owner) return Task.CompletedTask;
    if (base.CombatState.CurrentSide != base.Owner.Side) return Task.CompletedTask;
    GetInternalData<Data>().playedCards.Add(cardPlay.Card, 0);
    return Task.CompletedTask;
}

// 钩子2：受伤时累积力量
public override async Task AfterDamageReceived(..., CardModel? cardSource)
{
    if (target == base.Owner && result.UnblockedDamage > 0
        && base.CombatState.CurrentSide == base.Owner.Side)
    {
        if (cardSource == null || !GetInternalData<Data>().playedCards.ContainsKey(cardSource))
            await PowerCmd.Apply<StrengthPower>(base.Owner, base.Amount, ...);
        else
            GetInternalData<Data>().playedCards[cardSource] += base.Amount;
    }
}

// 钩子3：卡牌打完后应用累积的力量
public override async Task AfterCardPlayed(PlayerChoiceContext context, CardPlay cardPlay)
{
    if (cardPlay.Card.Owner.Creature == base.Owner
        && GetInternalData<Data>().playedCards.Remove(cardPlay.Card, out var value))
    {
        await PowerCmd.Apply<StrengthPower>(base.Owner, value, ...);
    }
}
```

**解析：** 撕裂是 STS2 中最复杂的多钩子协同案例之一：
1. `BeforeCardPlayed` — 在 `Dictionary` 中记录即将打出的卡牌
2. `AfterDamageReceived` — 如果伤害来源是已记录的卡牌，累积力量值（延迟）；否则立即给予力量
3. `AfterCardPlayed` — 卡牌打完后，应用累积的力量值

这种设计处理了"一张牌造成多次伤害"的情况——力量在牌完全打完后统一获得，而非每次伤害单独获得。

---

## 17. 遗物（Relic）系统钩子

遗物是 Slay the Spire 2 中的被动装备系统。玩家通过战斗奖励、商店购买、事件等方式获取遗物，每个遗物提供独特的被动效果。

### 17.1 RelicModel 基类结构

**文件：** `src/Core/Models/RelicModel.cs`

```csharp
public abstract class RelicModel : AbstractModel
{
    public abstract RelicRarity Rarity { get; }  // 稀有度

    // 生命周期
    public virtual Task AfterObtained();     // 获得时触发
    public virtual Task AfterRemoved();      // 移除时触发
    public virtual bool IsAllowed(IRunState); // 池过滤

    // 状态
    public virtual bool IsUsedUp => false;         // 是否已消耗
    public virtual bool HasUponPickupEffect => false; // 有拾取效果
    public virtual bool IsStackable => false;      // 是否可堆叠
    public int StackCount { get; }                 // 堆叠数量
    public RelicStatus Status { get; }             // Normal/Active/Disabled

    // 闪光
    public virtual string FlashSfx => "event:/sfx/ui/relic_activate_general";
    public virtual bool ShouldFlashOnPlayer => true;
}
```

**RelicRarity 枚举** (`src/Core/Entities/Relics/RelicRarity.cs`)：
`None`, `Starter`, `Common`, `Uncommon`, `Rare`, `Shop`, `Event`, `Ancient`

**RelicStatus 枚举** (`src/Core/Entities/Relics/RelicStatus.cs`)：
- `Normal` — 正常状态
- `Active` — 激活/高亮状态（如苦无即将触发）
- `Disabled` — 禁用/已消耗状态

---

### 17.2 遗物命令系统（RelicCmd）

```csharp
// 获得
RelicCmd.Obtain(relic, player, index) → player.AddRelicInternal()
    → relic.AfterObtained()

// 移除
RelicCmd.Remove(relic) → player.RemoveRelicInternal()
    → relic.AfterRemoved()

// 熔化（蜡质遗物）
RelicCmd.Melt(relic) → player.MeltRelicInternal()
    → IsMelted = true, Status = Disabled

// 替换
RelicCmd.Replace(original, replacement) → 在相同索引处移除 + 获取
```

**遗物池系统：** 遗物通过 `RelicGrabBag` 按稀有度分组管理，`RelicFactory` 从池中抽取。`IsAllowed(IRunState)` 用于过滤（如蛋类遗物在第三幕宝箱后不再出现）。

---

### 17.3 遗物闪光与状态系统

```csharp
// RelicModel.cs
protected void Flash()           // 触发闪光特效 + SFX
protected void InvokeDisplayAmountChanged() // 更新计数器显示

// 状态切换示例（苦无）
base.Status = RelicStatus.Active;   // 即将触发，高亮
base.Status = RelicStatus.Normal;   // 正常
base.Status = RelicStatus.Disabled; // 已消耗（如蜥蜴尾巴）
```

**C# 事件（RelicModel）：**
```csharp
public event Action<RelicModel, IEnumerable<Creature>>? Flashed;
public event Action? DisplayAmountChanged;
public event Action? StatusChanged;
```

---

### 17.4 遗物示例详解

以下 16 个遗物示例展示不同类型、不同复杂度的钩子使用模式。

#### 示例1：化学物X（Chemical X）— 简单数值修改

> 中文效果：X 费用牌的效果数值增加 2。

```csharp
public override int ModifyXValue(CardModel card, int originalValue)
{
    if (base.Owner != card.Owner) return originalValue;
    return originalValue + base.DynamicVars["Increase"].IntValue;
}
```

---

#### 示例2：硫磺（Brimstone）— 回合开始双方效果

> 中文效果：在你的回合开始时获得 2 力量，所有敌人获得 1 力量。

```csharp
// Brimstone.cs — 商店遗物，双向效果
public override async Task AfterSideTurnStart(CombatSide side,
    IReadOnlyList<Creature> participants, ICombatState combatState)
{
    if (side == base.Owner.Creature.Side)
    {
        Flash();
        // 给自己 +2 力量
        await PowerCmd.Apply<StrengthPower>(base.Owner.Creature,
            base.DynamicVars["SelfStrength"].BaseValue, base.Owner.Creature, null);
        // 给所有存活敌人 +1 力量
        IEnumerable<Creature> targets = from c in combatState
            .GetOpponentsOf(base.Owner.Creature) where c.IsAlive select c;
        await PowerCmd.Apply<StrengthPower>(targets,
            base.DynamicVars["EnemyStrength"].BaseValue, null, null);
    }
}
```

**解析：** `AfterSideTurnStart` 用于每回合开始时触发。这个遗物使用 `DynamicVars` 区分两种强度值（SelfStrength 2, EnemyStrength 1），展示了参数化设计。

---

#### 示例3：孙子兵法（Art of War）— 状态机模式

> 中文效果：如果你在一回合中没有打出攻击牌，在下一回合获得额外 1 能量。

```csharp
// ArtOfWar.cs — 复杂状态追踪
private bool _anyAttacksPlayedLastTurn;
private bool _anyAttacksPlayedThisTurn;

// 钩子1：追踪攻击牌使用
public override Task AfterCardPlayed(PlayerChoiceContext context, CardPlay cardPlay)
{
    if (base.Owner != cardPlay.Card.Owner) return Task.CompletedTask;
    if (cardPlay.Card.Type != CardType.Attack) return Task.CompletedTask;
    if (AnyAttacksPlayedLastTurn) return Task.CompletedTask; // 已触发过
    base.Status = RelicStatus.Normal;
    AnyAttacksPlayedThisTurn = true;
    return Task.CompletedTask;
}

// 钩子2：回合结束时保存状态
public override Task AfterSideTurnEnd(PlayerChoiceContext choiceContext,
    CombatSide side, IEnumerable<Creature> participants)
{
    if (side != base.Owner.Creature.Side) return Task.CompletedTask;
    AnyAttacksPlayedLastTurn = AnyAttacksPlayedThisTurn;
    AnyAttacksPlayedThisTurn = false;
    return Task.CompletedTask;
}

// 钩子3：能量重置时给额外能量
public override async Task AfterEnergyReset(Player player)
{
    if (base.Owner.Creature.CombatState.RoundNumber > 1
        && !AnyAttacksPlayedLastTurn)
    {
        Flash();
        await PlayerCmd.GainEnergy(base.DynamicVars.Energy.BaseValue, base.Owner);
    }
    AnyAttacksPlayedLastTurn = false;
}

// 钩子4：战斗结束清理
public override Task AfterCombatEnd(CombatRoom _)
{
    base.Status = RelicStatus.Normal;
    AnyAttacksPlayedLastTurn = false;
    AnyAttacksPlayedThisTurn = false;
    return Task.CompletedTask;
}
```

**解析：** 孙子兵法展示了 **4 个钩子协同** 的复杂遗物：
- `AfterCardPlayed` — 检测攻击牌
- `AfterSideTurnEnd` — 上回合状态传递给下回合
- `AfterEnergyReset` — 在正确的时机给予额外能量
- `AfterCombatEnd` — 战斗结束清理状态

`RelicStatus.Active` / `RelicStatus.Normal` 切换提供视觉反馈。

---

#### 示例4：苦无（Kunai）— 计数器模式

> 中文效果：每打出 3 张攻击牌获得 1 敏捷。

```csharp
// Kunai.cs — 攻击计数器 + 阈值触发
public override bool ShowCounter => CombatManager.Instance.IsInProgress;
public override int DisplayAmount => AttacksPlayedThisTurn % 3;

// 回合开始重置计数
public override Task BeforeSideTurnStart(PlayerChoiceContext choiceContext,
    CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
{
    AttacksPlayedThisTurn = 0;
    base.Status = RelicStatus.Normal;
    return Task.CompletedTask;
}

// 每次出牌检查
public override async Task AfterCardPlayed(PlayerChoiceContext context, CardPlay cardPlay)
{
    if (cardPlay.Card.Type == CardType.Attack)
    {
        AttacksPlayedThisTurn++;
        if (AttacksPlayedThisTurn % 3 == 0)
        {
            Flash();
            await PowerCmd.Apply<DexterityPower>(...);
        }
    }
}
```

---

#### 示例5：斗篷扣（Cloak Clasp）— 手牌数量倍率

> 中文效果：在你的回合结束时，每有一张手牌获得 1 格挡。

```csharp
// CloakClasp.cs
public override async Task BeforeSideTurnEnd(PlayerChoiceContext choiceContext,
    CombatSide side, IEnumerable<Creature> participants)
{
    if (side == base.Owner.Creature.Side)
    {
        IReadOnlyList<CardModel> cards = PileType.Hand.GetPile(base.Owner).Cards;
        if (cards.Count != 0)
        {
            int num = (int)((decimal)cards.Count * base.DynamicVars.Block.BaseValue);
            Flash();
            await CreatureCmd.GainBlock(base.Owner.Creature, num, ValueProp.Unpowered, null);
        }
    }
}
```

**解析：** 斗篷扣使用 `BeforeSideTurnEnd` 在回合结束之前检查手牌数量并给予格挡。`cards.Count * BlockValue` 实现了"每张手牌 N 格挡"的线性缩放。

---

#### 示例6：卡戎之灰（Charon's Ashes）— 消耗触发

> 中文效果：每当你消耗一张牌时，对所有敌人造成 3 点伤害。

```csharp
// CharonsAshes.cs
public override async Task AfterCardExhausted(PlayerChoiceContext choiceContext,
    CardModel card, bool _)
{
    if (card.Owner == base.Owner)
    {
        Flash();
        await CreatureCmd.Damage(choiceContext,
            base.Owner.Creature.CombatState.HittableEnemies,
            base.DynamicVars.Damage.BaseValue,
            base.DynamicVars.Damage.Props, base.Owner.Creature, null);
    }
}
```

---

#### 示例7：百年谜题（Centennial Puzzle）— 受伤触发（一次性）

> 中文效果：每场战斗首次受到未格挡伤害时，抽 3 张牌。

```csharp
// CentennialPuzzle.cs — 一次性战斗效果
private bool _usedThisCombat;
public override string FlashSfx => "event:/sfx/ui/relic_activate_draw";

public override async Task AfterDamageReceived(..., DamageResult result, ...)
{
    if (target == base.Owner.Creature && result.UnblockedDamage > 0 && !UsedThisCombat)
    {
        Flash();
        UsedThisCombat = true;
        for (int i = 0; i < 3; i++)
            await CardPileCmd.Draw(choiceContext, base.Owner);
    }
}

public override Task AfterCombatEnd(CombatRoom _)
{
    UsedThisCombat = false;  // 重置
    return Task.CompletedTask;
}
```

---

#### 示例8：地精之角（Gremlin Horn）— 敌人死亡触发

> 中文效果：每当敌人死亡时，获得 1 能量并抽 1 张牌。

```csharp
// GremlinHorn.cs
public override async Task AfterDeath(PlayerChoiceContext choiceContext,
    Creature target, bool wasRemovalPrevented, float deathAnimLength)
{
    if (target.Side != base.Owner.Creature.Side)  // 只对敌方死亡响应
    {
        Flash();
        await PlayerCmd.GainEnergy(base.DynamicVars.Energy.BaseValue, base.Owner);
        await CardPileCmd.Draw(choiceContext, base.DynamicVars.Cards.BaseValue, base.Owner);
    }
}
```

---

#### 示例9：准备背包（Bag of Preparation）— 首回合额外抽牌

> 中文效果：在第一回合额外抽 2 张牌。

```csharp
// BagOfPreparation.cs
public override decimal ModifyHandDraw(Player player, decimal count)
{
    if (player != base.Owner) return count;
    if (player.Creature.CombatState.RoundNumber > 1) return count; // 仅第一回合
    return count + base.DynamicVars.Cards.BaseValue;
}
```

---

#### 示例10：锚（Anchor）— 战斗开始格挡

> 中文效果：在每场战斗开始时获得 10 格挡。

```csharp
// Anchor.cs
public override async Task BeforeCombatStart()
{
    Flash();
    await CreatureCmd.GainBlock(base.Owner.Creature, base.DynamicVars.Block, null);
}
```

---

#### 示例11：血瓶（Blood Vial）— 首回合回血

> 中文效果：在每场战斗的第一回合恢复 2 点生命值。

```csharp
// BloodVial.cs
public override async Task AfterPlayerTurnStartLate(PlayerChoiceContext choiceContext,
    Player player)
{
    if (player == base.Owner && player.Creature.CombatState.RoundNumber <= 1)
        await CreatureCmd.Heal(base.Owner.Creature, base.DynamicVars.Heal.IntValue);
}
```

---

#### 示例12：棱镜宝石（Prismatic Gem）— 卡池修改

> 中文效果：普通战斗奖励中的卡牌池现在包含所有职业的卡牌。

```csharp
// PrismaticGem.cs — 跨职业卡池
public override decimal ModifyMaxEnergy(Player player, decimal amount) { ... + 1 }

public override CardCreationOptions ModifyCardRewardCreationOptions(Player player,
    CardCreationOptions options)
{
    if (base.Owner != player) return options;
    if (options.Flags.HasFlag(CardCreationFlags.NoCardPoolModifications)) return options;
    if (options.CustomCardPool != null) return options;
    if (options.CardPools.All(p => p.IsColorless)) return options;

    // 合并当前职业卡池和所有其他职业卡池
    IEnumerable<CardPoolModel> pools = player.UnlockState.CharacterCardPools
        .Union(options.CardPools);
    return options.WithCardPools(pools, options.CardPoolFilter);
}
```

---

#### 示例13：燃烧之血（Burning Blood）— 战斗后回血

> 中文效果：在每场战斗胜利后恢复 6 点生命值。

```csharp
// BurningBlood.cs — Starter 遗物
public override RelicRarity Rarity => RelicRarity.Starter;

public override async Task AfterCombatVictory(CombatRoom _)
{
    if (!base.Owner.Creature.IsDead)
    {
        Flash();
        await CreatureCmd.Heal(base.Owner.Creature, base.DynamicVars.Heal.BaseValue);
    }
}
```

---

#### 示例14：哲学家之石（Philosopher's Stone）— 多钩子 Ancient 遗物

> 中文效果：获得 1 能量。所有敌人获得 1 力量。

```csharp
// PhilosophersStone.cs — Ancient 遗物（Boss 遗物）
public override RelicRarity Rarity => RelicRarity.Ancient;

// 每回合 +1 最大能量
public override decimal ModifyMaxEnergy(Player player, decimal amount)
{
    if (player != base.Owner) return amount;
    return amount + 1;
}

// 战斗中添加敌人 → 给敌人力量
public override Task AfterCreatureAddedToCombat(Creature creature)
{
    if (creature.Side != base.Owner.Creature.Side)
        return PowerCmd.Apply<StrengthPower>(creature, 1, null, null);
    return Task.CompletedTask;
}

// 进入房间时 → 给已有敌人力量
public override async Task AfterRoomEntered(AbstractRoom room)
{
    if (room is CombatRoom)
    {
        var targets = from c in base.Owner.Creature.CombatState
            .GetOpponentsOf(base.Owner.Creature) where c.IsAlive select c;
        await PowerCmd.Apply<StrengthPower>(targets, 1, null, null);
    }
}
```

**解析：** 哲学家之石需要两个钩子来确保敌人获得力量：
1. `AfterCreatureAddedToCombat` — 覆盖**之后**生成的敌人
2. `AfterRoomEntered` — 覆盖**当前**已在场上的敌人

这展示了如何处理"进入房间时已有敌人 + 后续可能新增敌人"的场景。

---

#### 示例15：灵体外质（Ectoplasm）— 守卫钩子

> 中文效果：获得 1 能量。无法获得金币。

```csharp
// Ectoplasm.cs
public override decimal ModifyMaxEnergy(Player player, decimal amount)
{
    if (player != base.Owner) return amount;
    return amount + 1;
}

public override decimal ModifyGoldGained(Player player, decimal amount)
{
    if (player != base.Owner) return amount;
    return 0m;  // 持有者不能获得金币（ShouldGainGold 在 v0.107.1 已移除）
}
```

---

#### 示例16：冻结之蛋（Frozen Egg）— 三阶段卡牌升级

> 中文效果：每当你获得能力牌时，将其升级。

```csharp
// FrozenEgg.cs — 3 个获取点
// 1. 商店购买时
public override void ModifyMerchantCardCreationResults(Player player,
    List<CardCreationResult> cards) { ... }

// 2. 战斗奖励（晚阶段，允许其他修改器先处理）
public override bool TryModifyCardRewardOptionsLate(Player player,
    List<CardCreationResult> cardRewards, CardCreationOptions options) { ... }

// 3. 卡牌加入牌组时（如事件直接给牌）
public override bool TryModifyCardBeingAddedToDeck(CardModel card,
    out CardModel? newCard) { ... }
```

**解析：** 冻结之蛋需要在三个不同点拦截能力牌的获取：商店购买、战斗奖励、直接加入牌组。每个入口都有不同的参数和语义，确保无论能力牌如何进入牌组，都能被自动升级。

---

## 18. 战斗生命周期钩子

战斗由 `CombatManager` (`src/Core/Combat/CombatManager.cs`) 编排。理解战斗循环是理解何时触发哪个钩子的关键。

### 18.1 完整战斗钩子时序图

```
┌─────────────────────────────────────────────────────────────┐
│ BeforeCombatStart [Early → Late]                            │
│   ├─ 敌人生成 / 玩家布阵                                      │
│   └─ StartTurn()                                            │
│                                                             │
│ ┌─── 回合循环 ──────────────────────────────────────────┐    │
│ │                                                       │    │
│ │ BeforeSideTurnStart(side)                              │    │
│ │   └─ AfterSideTurnStart(side) [Early → Late]           │    │
│ │                                                       │    │
│ │ [玩家回合]                                              │    │
│ │   ├─ ShouldPlayerResetEnergy? → AfterEnergyReset      │    │
│ │   ├─ ModifyHandDraw → BeforeHandDraw → 抽牌           │    │
│ │   ├─ AfterPlayerTurnStart [Early → Normal → Late]     │    │
│ │   ├─ BeforePlayPhaseStart [Normal → Late]              │    │
│ │   │                                                   │    │
│ │   │ [玩家自由出牌阶段]                                   │    │
│ │   │   ├─ ShouldPlay(card)                              │    │
│ │   │   ├─ BeforeCardPlayed                              │    │
│ │   │   ├─ OnPlay (卡牌核心逻辑)                          │    │
│ │   │   ├─ AfterCardPlayed → AfterCardPlayedLate         │    │
│ │   │   └─ ... (更多卡牌)                                │    │
│ │   │                                                   │    │
│ │   ├─ BeforeSideTurnEnd [VeryEarly → Early → Normal]    │    │
│ │   ├─ BeforeFlush [Normal → Late]                       │    │
│ │   ├─ 弃牌 / 保留牌处理                                   │    │
│ │   ├─ AfterSideTurnEnd [Normal → Late]                  │    │
│ │   └─ ShouldTakeExtraTurn? → AfterTakingExtraTurn       │    │
│ │                                                       │    │
│ │ [敌方回合]                                              │    │
│ │   └─ BeforeSideTurnStart → AfterSideTurnStart          │    │
│ │       └─ 每个怪物执行 MoveState                          │    │
│ │           └─ BeforeSideTurnEnd → AfterSideTurnEnd      │    │
│ │                                                       │    │
│ └─── 重复 ─────────────────────────────────────────────┘    │
│                                                             │
│ ShouldStopCombatFromEnding?                                  │
│   ├─ AfterCombatEnd                                         │
│   └─ AfterCombatVictory [Early → Normal]                    │
└─────────────────────────────────────────────────────────────┘
```

### 18.2 关键战斗生命周期钩子详解

#### 战斗开始

```csharp
// Hook.cs — 战斗开始前
public static async Task BeforeCombatStart(IRunState runState, ICombatState? combatState)

// AbstractModel.cs
public virtual Task BeforeCombatStart() => Task.CompletedTask;
public virtual Task BeforeCombatStartLate() => Task.CompletedTask;
```

**触发时机：** 敌人已生成、玩家已布阵，但第一回合尚未开始。

**示例：锚（Anchor）** — 用 `BeforeCombatStart` 在战斗开始时获得格挡。

#### 回合开始

```csharp
// Hook.cs
public static async Task BeforeSideTurnStart(ICombatState combatState,
    CombatSide side, IReadOnlyList<Creature> participants)
public static async Task AfterSideTurnStart(ICombatState combatState,
    CombatSide side, IReadOnlyList<Creature> participants)

// AbstractModel.cs
public virtual Task BeforeSideTurnStart(PlayerChoiceContext choiceContext,
    CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
    => Task.CompletedTask;
public virtual Task AfterSideTurnStart(CombatSide side,
    IReadOnlyList<Creature> participants, ICombatState combatState) => Task.CompletedTask;
public virtual Task AfterSideTurnStartLate(CombatSide side,
    IReadOnlyList<Creature> participants, ICombatState combatState) => Task.CompletedTask;
```

**注意：** 没有独立的 `BeforeTurnStart` 钩子！`StartTurn()` 是 `CombatManager` 的私有方法，通过 `Creature.BeforeTurnStart` / `Creature.AfterTurnStart` 直接调用。对外暴露的钩子是 `BeforeSideTurnStart`、`AfterSideTurnStart` 和 `AfterPlayerTurnStart`。0.106.x 起，side-turn 钩子还会传入本次回合开始的 `participants`，下游直接 override `AbstractModel` 时也必须保留该参数；MultiEnchantmentMod 的 `OnSideTurnStart` / `OnBeforeSideTurnStart` lifecycle 回调会把它抽象成 `(card, enchantment, side)`。

#### 玩家回合

```csharp
// 玩家回合开始（3 阶段）
AfterPlayerTurnStartEarly(PlayerChoiceContext, Player)
AfterPlayerTurnStart(PlayerChoiceContext, Player)
AfterPlayerTurnStartLate(PlayerChoiceContext, Player)

// 出牌阶段开始（2 阶段）
BeforePlayPhaseStart(HookPlayerChoiceContext, Task, ICombatState, Player)
BeforePlayPhaseStartLate(HookPlayerChoiceContext, Task, ICombatState, Player)
```

#### 回合结束

```csharp
// 回合结束前（3 阶段）
BeforeSideTurnEndVeryEarly(PlayerChoiceContext, CombatSide, IEnumerable<Creature>)
BeforeSideTurnEndEarly(PlayerChoiceContext, CombatSide, IEnumerable<Creature>)
BeforeSideTurnEnd(PlayerChoiceContext, CombatSide, IEnumerable<Creature>)

// 回合结束后（2 阶段）
AfterSideTurnEnd(PlayerChoiceContext, CombatSide, IEnumerable<Creature>)
AfterSideTurnEndLate(PlayerChoiceContext, CombatSide, IEnumerable<Creature>)
```

#### 抽牌流程

```csharp
// Hook.cs
ShouldDraw(Player, bool fromHandDraw) → bool
ModifyHandDraw(Player, decimal count) → decimal
ModifyHandDrawLate(Player, decimal count) → decimal
BeforeHandDraw(Player, PlayerChoiceContext, ICombatState)
BeforeHandDrawLate(Player, PlayerChoiceContext, ICombatState)
AfterModifyingHandDraw(ICombatState, IEnumerable<AbstractModel>)
```

**示例参考：**
- `BeforeHandDraw`：`NinjaScroll`、`Toolbox`、`CreativeAiPower`、`InfiniteBladesPower` 在抽牌前生成或加入起手资源。
- `BeforeHandDrawLate`：当前 0.106.1 本体未发现实际覆写，适合 Mod 在所有普通抽牌前效果完成后做最终修正。
- `AfterModifyingHandDraw`：`Pocketwatch`、`MindRotPower` 在抽牌数量被修改后播放反馈或消费状态。

#### 额外回合

```csharp
// Hook.cs
ShouldTakeExtraTurn(Player) → bool
AfterTakingExtraTurn(Player)
```

**示例参考：** `PaelsEye` 使用 `ShouldTakeExtraTurn` 允许玩家获得额外回合，并在 `AfterTakingExtraTurn` 中标记或消耗这次额外回合来源。

#### 战斗结束

```csharp
// Hook.cs
ShouldStopCombatFromEnding(ICombatState) → bool
AfterCombatEnd(IRunState, ICombatState?, CombatRoom)
AfterCombatVictoryEarly(IRunState, ICombatState?, CombatRoom)
AfterCombatVictory(IRunState, ICombatState?, CombatRoom)
```

**执行顺序：** `AfterCombatEnd` 处理任何战斗结束后事务（无论胜负），`AfterCombatVictory` 仅胜利时触发。`ShouldStopCombatFromEnding` 是短路检查——任何监听器返回 `true` 都会阻止战斗结束。

**示例参考：**
- `ShouldStopCombatFromEnding`：`AdaptablePower`、`InfestedPower`、`SteamEruptionPower` 可延迟战斗结束以完成特殊结算。
- `AfterCombatEnd`：`CentennialPuzzle`、`HappyFlower` 等在任意战斗结束后重置一次性状态。
- `AfterCombatVictoryEarly`：`MeatOnTheBone` 在胜利早期处理回血。
- `AfterCombatVictory`：`BurningBlood`、`BlackBlood` 在胜利后回血，`WarHammer` 在胜利后处理奖励向效果。

---

### 18.3 CombatState 监听器收集规则

```csharp
// CombatState.IterateHookListeners() 收集以下监听器：
1. 所有存活玩家的 PowerModel 实例
2. 所有存活玩家的 RelicModel（未熔化、未消耗）
3. 所有存活玩家的 PotionModel
4. 所有存活玩家的卡牌（手牌/抽牌堆/弃牌堆中实现了钩子的卡牌）
5. 所有存活玩家的 AfflictionModel（刻印）
6. 所有存活玩家的 EnchantmentModel（附魔）
7. 所有存活玩家的 OrbModel
8. 每个存活敌方生物的 PowerModel 实例
9. 战斗 Modifiers（模组系统）
10. MultiplayerScalingModel（多人缩放）
```

---

## 19. 生物（Creature）与死亡系统钩子

### 19.1 Creature 实体类型

`Creature` (`src/Core/Entities/Creatures/Creature.cs`) 是战斗参与者的核心实体。三种类型：

| 属性 | 玩家 (Player) | 怪物 (Monster) | 宠物 (Pet) |
|------|--------------|---------------|-----------|
| `IsPlayer` | `true` | `false` | `false` |
| `IsMonster` | `false` | `true` | `false` |
| `IsPet` | `false` | `false` | `true` |
| `PetOwner` | `null` | `null` | `Player` |
| `Player` | 自身 | `null` | `null` |
| `Monster` | `null` | `MonsterModel` | `null` |

**关键属性：**
```csharp
public decimal Block { get; }           // 当前格挡
public decimal CurrentHp { get; }       // 当前 HP
public int MaxHp { get; }              // 最大 HP
public List<PowerModel> Powers { get; } // 附着的能力
public CombatSide Side { get; }        // Player / Enemy
public bool IsHittable { get; }        // 是否可被击中（检查 IsAlive + ShouldAllowHitting）
```

**C# 事件：** `Died`, `Revived`, `BlockChanged`, `CurrentHpChanged`, `MaxHpChanged`, `PowerApplied`, `PowerIncreased`, `PowerDecreased`, `PowerRemoved`

---

### 19.2 死亡流水线

```
生物 HP ≤ 0
  │
  ├─ BeforeDeath(creature)         ← 死亡前通知（可做最后一件事）
  │
  ├─ ShouldDie(creature)           ← 守卫：返回 false 阻止死亡
  │   └─ ShouldDieLate(creature)   ← 晚阶段守卫
  │       └─ 如果阻止 → AfterPreventingDeath(creature)
  │
  ├─ AfterDeath(context, creature, wasRemovalPrevented, deathAnimLength)
  │   └─ AfterDiedToDoom(creatures)  ← 末日死亡专用
  │
  └─ ShouldCreatureBeRemovedFromCombatAfterDeath(creature)
      └─ 返回 true → 生物从场上移除
      └─ 返回 false → 尸体留在场上（宠物、需要展示的效果）
```

---

### 19.3 伤害接收流水线

```
BeforeDamageReceived(context, target, amount, props, dealer, cardSource)
  │  ← 反伤能力在这里触发
  │
  ├─ 格挡计算
  │
  └─ AfterDamageReceived(context, target, result, props, dealer, cardSource)
       └─ AfterDamageReceivedLate(...)
```

---

### 19.4 格挡系统

```csharp
// 格挡获取前/后
BeforeBlockGained(ICombatState, Creature, decimal, ValueProp, CardModel?)
AfterBlockGained(ICombatState, Creature, decimal, ValueProp, CardModel?)

// 格挡被打破/清除
AfterBlockBroken(ICombatState, Creature)    // 格挡归零
AfterBlockCleared(ICombatState, Creature)   // 回合开始清除

// 守卫
ShouldClearBlock(ICombatState, Creature, out AbstractModel?) → bool
AfterPreventingBlockClear(ICombatState, AbstractModel, Creature)
```

---

### 19.5 未格挡伤害重定向

```csharp
// Hook.cs:1564
public static Creature ModifyUnblockedDamageTarget(ICombatState combatState,
    Creature originalTarget, decimal amount, ValueProp props, Creature? dealer)
```

**示例回顾：** 为你而死（Die For You Power）将未格挡伤害从主人重定向到宠物。见第 12.3 节。

---

### 19.6 其他生物钩子

```csharp
// 生物加入战斗
AfterCreatureAddedToCombat(ICombatState, Creature)

// HP 变化
AfterCurrentHpChanged(IRunState, ICombatState?, Creature, decimal delta)

// 宠物复活
AfterOstyRevived(ICombatState, Creature)

// 可攻击性守卫
ShouldAllowHitting(ICombatState, Creature) → bool
ShouldAllowTargeting(ICombatState, Creature, out AbstractModel?) → bool
```

**示例参考：**
- `AfterCreatureAddedToCombat`：`PhilosophersStone`、`FurCoat` 在新敌人加入战斗后补上对应效果。
- `AfterCurrentHpChanged`：`MeatOnTheBone`、`RedSkull` 根据生命值变化更新遗物状态或触发阈值效果。
- `AfterOstyRevived`：`SandpitPower` 响应奥斯提复活。
- `ShouldAllowHitting`：`DieForYouPower`、`IllusionPower`、`ReattachPower` 控制某些单位是否还能被命中。
- `ShouldAllowTargeting`：当前 0.106.1 本体未发现实际覆写，适合 Mod 阻止玩家把某类效果指定到受保护目标上。

---

## 20. 药水（Potion）系统钩子

### 20.1 PotionModel 结构

```csharp
// src/Core/Models/PotionModel.cs
public abstract class PotionModel : AbstractModel
{
    public override bool ShouldReceiveCombatHooks => true;  // 接收战斗钩子
    public abstract PotionRarity Rarity { get; }
    // ... 药水特定的使用逻辑
}
```

### 20.2 药水生命周期钩子

```csharp
// 使用前/后
BeforePotionUsed(IRunState, ICombatState?, PotionModel, Creature?)
AfterPotionUsed(IRunState, ICombatState?, PotionModel, Creature?)

// 获得/丢弃
AfterPotionProcured(IRunState, ICombatState?, PotionModel)
AfterPotionDiscarded(IRunState, ICombatState?, PotionModel)

// 守卫
ShouldProcurePotion(IRunState, ICombatState?, PotionModel, Player) → bool
```

**示例参考：**
- `BeforePotionUsed`：`SurroundedPower` 在药水使用前响应目标和药水信息。
- `AfterPotionUsed`：`BeltBuckle`、`ReptileTrinket` 在药水使用后触发额外效果。
- `AfterPotionProcured` / `AfterPotionDiscarded`：`BeltBuckle` 跟踪药水获得与丢弃。
- `ShouldProcurePotion`：`Sozu` 返回 `false` 阻止持有者获得药水。

### 20.3 示例：苏祖（Sozu）

> 中文效果：获得 1 能量。无法获得药水。

```csharp
// Sozu.cs — Ancient 遗物
public override decimal ModifyMaxEnergy(Player player, decimal amount) { ... +1 }

public override bool ShouldProcurePotion(PotionModel potion, Player player)
{
    return player != base.Owner;  // 持有者不能获得药水
}
```

**解析：** `ShouldProcurePotion` 是一个守卫钩子——返回 `false` 直接阻止药水进入玩家的药水槽。

---

## 21. 房间/事件/地图钩子

### 21.1 房间生命周期

```csharp
// Hook.cs
BeforeRoomEntered(IRunState, AbstractRoom)
AfterRoomEntered(IRunState, AbstractRoom)
```

**示例参考：** `BigMushroom`、`BronzeScales`、`DataDisk`、`EternalFeather`、`Girya` 等在 `AfterRoomEntered` 中根据进入的房间类型重置或结算房间级状态。当前 0.106.1 本体未发现 `BeforeRoomEntered` 的实际覆写，适合 Mod 在房间效果正式触发前做预处理。

**RoomType 枚举** (`src/Core/Rooms/RoomType.cs`)：
`Unassigned`, `Monster`, `Elite`, `Boss`, `Treasure`, `Shop`, `Event`, `RestSite`, `Map`

---

### 21.2 休息处钩子

```csharp
// 治疗/升级后
AfterRestSiteHeal(IRunState, Player, bool isMimicked)
AfterRestSiteSmith(IRunState, Player)

// 修改
ModifyRestSiteHealAmount(IRunState, Creature, decimal) → decimal
ModifyRestSiteHealRewards(IRunState, Player, List<Reward>, bool isMimicked)
    → IEnumerable<AbstractModel>
ModifyRestSiteOptions(IRunState, Player, ICollection<RestSiteOption>)
    → IEnumerable<AbstractModel>
ModifyExtraRestSiteHealText(IRunState, Player, IReadOnlyList<LocString>)
    → IReadOnlyList<LocString>

// 守卫
ShouldDisableRemainingRestSiteOptions(IRunState, Player) → bool
```

对应的 `AbstractModel` 覆写入口：

```csharp
public virtual bool TryModifyRestSiteHealRewards(Player player,
    List<Reward> rewards, bool isMimicked)
public virtual bool TryModifyRestSiteOptions(Player player,
    ICollection<RestSiteOption> options)
```

**示例：捕梦网（Dream Catcher）** — 在休息处治疗时额外获得卡牌奖励。使用 `TryModifyRestSiteHealRewards` 在治疗时添加卡牌奖励选项。

---

### 21.3 商人钩子

```csharp
AfterItemPurchased(IRunState, Player, MerchantEntry, int goldSpent)

// 修改
ModifyMerchantPrice(IRunState, Player, MerchantEntry, decimal) → decimal
ModifyMerchantCardCreationResults(IRunState, Player, List<CardCreationResult>)
ModifyMerchantCardPool(IRunState, Player, IEnumerable<CardModel>) → IEnumerable<CardModel>
ModifyMerchantCardRarity(IRunState, Player, CardRarity) → CardRarity

// 守卫
ShouldAllowMerchantCardRemoval(IRunState, Player) → bool
ShouldRefillMerchantEntry(IRunState, MerchantEntry, Player) → bool
```

**示例参考：**
- `AfterItemPurchased`：`MawBank` 在购买后处理金币银行状态。
- `ModifyMerchantPrice`：`MembershipCard`、`TheCourier` 修改商店价格。
- `ModifyMerchantCardCreationResults`：`FrozenEgg`、`MoltenEgg`、`ToxicEgg` 在商店卡牌生成后自动升级对应类型。
- `ModifyMerchantCardPool`：`CharacterCards` modifier 替换或扩展商店卡池。
- `ModifyMerchantCardRarity`：当前 0.106.1 本体未发现实际覆写，可用于 Mod 调整商店卡牌稀有度分布。
- `ShouldAllowMerchantCardRemoval`：`Hoarder` modifier 阻止商人删牌。
- `ShouldRefillMerchantEntry`：`TheCourier` 允许商店格子补货。

---

### 21.4 地图钩子

```csharp
AfterMapGenerated(IRunState, ActMap, int actIndex)

// 修改
ModifyGeneratedMap(IRunState, ActMap, int actIndex) → ActMap
ModifyGeneratedMapLate(IRunState, ActMap, int actIndex) → ActMap
ModifyUnknownMapPointRoomTypes(IRunState, IReadOnlySet<RoomType>) → IReadOnlySet<RoomType>
ModifyOddsIncreaseForUnrolledRoomType(IRunState, RoomType, float) → float

// 守卫
ShouldAllowFreeTravel(IRunState) → bool
ShouldProceedToNextMapPoint(IRunState) → bool
```

**示例参考：**
- `AfterMapGenerated`：`SpoilsMap` 在地图生成后标记任务点。
- `ModifyGeneratedMap` / `ModifyGeneratedMapLate`：`GoldenCompass`、`BigGameHunter`、`FurCoat`、`SpoilsMap` 修改地图结构或特殊点。
- `ModifyUnknownMapPointRoomTypes`：`GoldenCompass`、`JuzuBracelet`、`LanternKey` 控制未知点可能房间类型。
- `ModifyOddsIncreaseForUnrolledRoomType`：`DeadlyEvents` modifier 调整房间类型滚动概率。
- `ShouldAllowFreeTravel`：`WingedBoots`、`Flight` 允许自由移动。
- `ShouldProceedToNextMapPoint`：当前 0.106.1 本体未发现实际覆写，适合 Mod 阻止进入下一地图点直到自定义流程完成。

---

### 21.5 事件钩子

```csharp
ModifyNextEvent(IRunState, EventModel) → EventModel
ShouldAllowAncient(IRunState, Player, AncientEventModel) → bool
```

**示例参考：** `LanternKey` 使用 `ModifyNextEvent` 替换下一次事件。当前 0.106.1 本体未发现 `ShouldAllowAncient` 的实际覆写，适合 Mod 禁止或放行特定 Ancient 事件。

**奖励相关示例：黑星（Black Star）** — `TryModifyRewards` 在精英战额外添加遗物奖励。

---

## 22. 奖励与资源钩子

### 22.1 奖励系统

```csharp
BeforeRewardsOffered(IRunState, Player, IReadOnlyList<Reward>)
AfterRewardTaken(IRunState, Player, Reward)

// 修改
ModifyRewards(IRunState, Player, List<Reward>, AbstractRoom?) → IEnumerable<AbstractModel>
ModifyRewardsLate(IRunState, Player, List<Reward>, AbstractRoom?) → IEnumerable<AbstractModel>
AfterModifyingRewards(IRunState, IEnumerable<AbstractModel>)
TryModifyCardRewardOptions(IRunState, Player, List<CardCreationResult>,
    CardCreationOptions, out List<AbstractModel>) → bool
ModifyCardRewardAlternatives(IRunState, Player, CardReward,
    List<CardRewardAlternative>) → IEnumerable<AbstractModel>

// 守卫
ShouldGenerateTreasure(IRunState, Player) → bool
ShouldAllowSelectingMoreCardRewards(IRunState, Player, CardReward) → bool
ShouldForcePotionReward(IRunState, Player, RoomType) → bool
```

对应的 `AbstractModel` 覆写入口：

```csharp
public virtual bool TryModifyRewardsLate(Player player,
    List<Reward> rewards, AbstractRoom room)
public virtual bool TryModifyCardRewardOptions(Player player,
    List<CardCreationResult> cardRewardOptions,
    CardCreationOptions creationOptions)
public virtual bool TryModifyCardRewardOptionsLate(Player player,
    List<CardCreationResult> cardRewardOptions,
    CardCreationOptions creationOptions)
public virtual bool TryModifyCardRewardAlternatives(Player player,
    CardReward cardReward, List<CardRewardAlternative> alternatives)
public virtual bool ShouldForcePotionReward(Player player, RoomType roomType)
```

**示例参考：**
- `TryModifyRewardsLate`：`Driftwood`、`Midas`、`Vintage` 在奖励列表晚阶段增删奖励。
- `TryModifyCardRewardOptions` / `TryModifyCardRewardOptionsLate`：`FrozenEgg` 一类遗物可在卡牌奖励生成后升级特定类型卡牌。
- `TryModifyCardRewardAlternatives`：`PaelsWing` 添加卡牌奖励替代选项。
- `ShouldGenerateTreasure`：`SilverCrucible` 控制是否生成宝箱奖励。
- `ShouldAllowSelectingMoreCardRewards`：当前 0.106.1 本体未发现实际覆写，用于允许一次卡牌奖励中多选。
- `ShouldForcePotionReward`：当前 0.106.1 本体未发现实际覆写，用于强制指定房间类型给药水奖励。

---

### 22.2 金币/星能钩子

```csharp
// 金币（无 Should 守卫；阻止获得 = ModifyGoldGained 返回 0）
AfterGoldGained(IRunState, Player)
ModifyGoldGained(IRunState, ICombatState?, decimal amount, Player, out IEnumerable<AbstractModel>) → decimal
AfterModifyingGoldGained(IRunState, ICombatState?, IEnumerable<AbstractModel>, Player, decimal)

// 星能（STS2 新资源）
AfterStarsGained(ICombatState, int, Player)
AfterStarsSpent(ICombatState, int, Player)
ShouldGainStars(ICombatState, decimal, Player) → bool
ShouldPayExcessEnergyCostWithStars(ICombatState, Player) → bool
```

**示例参考：**
- `AfterGoldGained`：`BowlerHat`、`DragonFruit` 在金币获得后触发。
- `ModifyGoldGained`：`Ectoplasm` 返回 0 阻止持有者获得金币（`ShouldGainGold` 已于 v0.107.1 移除）。
- `AfterStarsGained`：`BlackHolePower` 在获得星能后响应。
- `AfterStarsSpent`：`GalacticDust`、`MiniRegent`、`ChildOfTheStarsPower` 在花费星能后响应。
- `ShouldGainStars` / `ShouldPayExcessEnergyCostWithStars`：当前 0.106.1 本体未发现实际覆写，是 Mod 控制星能获取和用星能补付能量费用的扩展点。

---

### 22.3 其他钩子

```csharp
// 锻造
AfterForge(ICombatState, decimal, Player, AbstractModel?)

// 召唤
AfterSummon(ICombatState, PlayerChoiceContext, Player, decimal)
ModifySummonAmount(ICombatState, Player, decimal, AbstractModel?) → decimal

// 洗牌
AfterShuffle(PlayerChoiceContext, Player)
ModifyShuffleOrder(ICombatState, Player, List<CardModel>, bool isInitialShuffle)

// 幕切换
AfterActEntered(IRunState)

// 手牌清空
AfterHandEmptied(PlayerChoiceContext, Player)

// 自动出牌
AfterCardAutoPlayed(ICombatState, CardModel, Creature?, AutoPlayType)
```

**示例参考：**
- `AfterForge`：`HammerTimePower` 在锻造后触发。
- `AfterSummon` / `ModifySummonAmount`：当前 0.106.1 本体未发现实际覆写，可用于 Mod 响应召唤或修改召唤数量。
- `AfterShuffle`：`TheAbacus`、`BiiigHug`、`StratagemPower` 在洗牌后触发。
- `ModifyShuffleOrder`：`PerfectFit` 附魔可把指定牌放到洗牌后牌堆顶部。
- `AfterActEntered`：`CursedRun` modifier 在进入新幕时处理状态。
- `AfterHandEmptied`：`UnceasingTop` 在手牌清空后触发抽牌。
- `AfterCardAutoPlayed`：当前 0.106.1 本体未发现实际覆写，可用于 Mod 统计或响应自动打出的卡牌。

---

## 23. 钩子执行顺序与阶段总结

### 23.1 钩子三阶段模式

Slay the Spire 2 的钩子系统广泛使用三阶段执行模式：

```
Early（最早） → Normal（正常） → Late（最晚）
```

部分钩子还有 **VeryEarly**（最早最早）阶段：

```
VeryEarly → Early → Normal → Late
```

**为什么需要多阶段？**

1. **避免竞争条件** — 先让系统级别的钩子执行（Early），再让用户级别的钩子执行（Normal），最后让汇总/清理钩子执行（Late）
2. **优先级控制** — 不需要在每个钩子上指定优先级数字，阶段划分简化了排序
3. **后处理能力** — Late 阶段可以看到所有 Normal 阶段修改后的结果

---

### 23.2 三种钩子类型对比

| 类型 | 返回 | 语义 | 迭代行为 | 典型示例 |
|------|------|------|----------|----------|
| **Modify** | 修改后的值 | 值变换链 | 全部迭代，链式传递 | `ModifyDamage`, `ModifyBlock`, `ModifyHpLostBeforeOsty` |
| **TryModify** | `bool` + `out` 值 | 短路修改 | 第一个返回 `true` 的停止迭代 | `TryModifyEnergyCostInCombat`, `TryModifyPowerAmountReceived` |
| **Should** | `bool` | 守卫/许可 | 全部迭代，任一 `false` 阻止 | `ShouldPlay`, `ShouldDie`, `ShouldClearBlock` |
| **After/Before** | `Task` | 副作用通知 | 全部迭代（异步） | `AfterCardPlayed`, `BeforeDamageReceived` |

**短路语义分析：**

```csharp
// Modify 模式 — 链式传递
decimal ModifyDamage(...)
{
    decimal d = original;
    foreach (var m in listeners)
        d = m.ModifyDamageAdditive(target, d, ...);  // 每个都执行
    foreach (var m in listeners)
        d = m.ModifyDamageMultiplicative(target, d, ...);  // 每个都执行
    return d;
}

// TryModify 模式 — 短路
bool TryModifyEnergyCost(CardModel card, decimal original, out decimal modified)
{
    foreach (var m in listeners)
    {
        if (m.TryModifyEnergyCostInCombat(card, original, out modified))
            return true;  // ← 找到一个就停止！
    }
    modified = original;
    return false;
}

// Should 模式 — 全部检查
bool ShouldDie(Creature creature, out AbstractModel? preventer)
{
    foreach (var m in listeners)
    {
        if (!m.ShouldDie(creature)) { preventer = m; return false; }
    }
    foreach (var m in listeners)
    {
        if (!m.ShouldDieLate(creature)) { preventer = m; return false; }
    }
    return true;
}
```

---

### 23.3 监听器收集规则

```csharp
// CombatState.IterateHookListeners()
// 用于战斗中的钩子
收集对象：
  → 所有存活玩家的 PowerModel
  → 所有存活玩家的 RelicModel（未熔化）
  → 所有存活玩家的 PotionModel
  → 所有存活玩家的 AfflictionModel / EnchantmentModel
  → 所有存活玩家的 OrbModel
  → 每个存活敌方生物的 PowerModel
  → 战斗 Modifiers

// IRunState.IterateHookListeners(combatState?)
// 用于全局范围的钩子（地图、奖励等）
收集对象：
  → 玩家的 RelicModel（所有，包括非战斗状态的）
  → 玩家的卡牌（牌组中）
  → 玩家的 PotionModel
  → 如果 combatState != null → 也委托给 CombatState.IterateHookListeners()
```

**`ShouldReceiveCombatHooks` 属性：** `PowerModel`、`RelicModel`、`PotionModel` 设置 `ShouldReceiveCombatHooks = true` 以接收战斗钩子。`CardModel` 仅在特定牌堆中时接收钩子（手牌、抽牌堆、弃牌堆）。

---

### 23.4 0.106.1 反射审计补遗

本节列出 2026-05-30 使用本地 `sts2.dll` 0.106.1 反射审计时发现、此前正文未单独覆盖的钩子。以下签名按运行时公开类型记录；nullable 标注请以反编译源码或 IDE 提示为准。

#### 自动打牌阶段

```csharp
// Hook.cs
Task AfterAutoPrePlayPhaseEntered(HookPlayerChoiceContext playerChoiceContext,
    ICombatState combatState, Player player)
Task AfterAutoPostPlayPhaseEntered(HookPlayerChoiceContext playerChoiceContext,
    ICombatState combatState, Player player)

// AbstractModel.cs
Task AfterAutoPrePlayPhaseEnteredEarly(PlayerChoiceContext choiceContext, Player player)
Task AfterAutoPrePlayPhaseEntered(PlayerChoiceContext choiceContext, Player player)
Task AfterAutoPrePlayPhaseEnteredLate(PlayerChoiceContext choiceContext, Player player)
Task AfterAutoPostPlayPhaseEntered(PlayerChoiceContext choiceContext, Player player)
```

`AfterAutoPrePlayPhaseEntered` 由 Hook 层统一调度 Early / Normal / Late 三段，适合响应自动打牌前阶段进入；`AfterAutoPostPlayPhaseEntered` 用于自动打牌后阶段进入。

**示例：自动打牌阶段进入时重置临时标记**

```csharp
public override Task AfterAutoPrePlayPhaseEntered(PlayerChoiceContext choiceContext,
    Player player)
{
    if (player != base.Owner.Player) return Task.CompletedTask;

    GetInternalData<Data>().AutoPlayedCardsThisPhase = 0;
    return Task.CompletedTask;
}
```

#### Flush / 保留 / 抽牌阻止

```csharp
// Hook.cs
bool ShouldFlush(ICombatState combatState, Player player)
Task AfterFlush(ICombatState combatState, Player player,
    PlayerChoiceContext playerChoiceContext,
    IReadOnlyCollection<CardModel> flushedCards,
    IReadOnlyCollection<CardModel> retainedCards)
Task AfterPreventingDraw(ICombatState combatState, AbstractModel modifier)

// AbstractModel.cs
bool ShouldFlush(Player player)
Task BeforeFlushLate(PlayerChoiceContext choiceContext, Player player)
Task AfterFlush(PlayerChoiceContext choiceContext, Player player,
    IReadOnlyCollection<CardModel> flushedCards,
    IReadOnlyCollection<CardModel> retainedCards)
Task AfterPreventingDraw()
```

`ShouldFlush` 返回 `false` 可阻止回合末 flush；`AfterFlush` 同时给出实际弃置与保留的卡牌集合。`AfterPreventingDraw` 会在 `ShouldDraw` 阻止抽牌后通知阻止者。

**示例：符文金字塔式保留整只手牌**

```csharp
public override bool ShouldFlush(Player player)
{
    if (player != base.Owner) return true;

    return false;
}
```

**示例：flush 后统计实际保留的牌**

```csharp
public override Task AfterFlush(PlayerChoiceContext choiceContext, Player player,
    IReadOnlyCollection<CardModel> flushedCards,
    IReadOnlyCollection<CardModel> retainedCards)
{
    if (player != base.Owner) return Task.CompletedTask;

    RetainedLastTurn = retainedCards.Count;
    return Task.CompletedTask;
}
```

**实际覆写参考：** `WellLaidPlansPower` 使用 `BeforeFlushLate`；`RunicPyramid`、`RingingTriangle`、`RetainHandPower` 使用 `ShouldFlush`；`Bookmark` 使用 `AfterFlush`；`Fiddle` 使用 `AfterPreventingDraw`。

#### 伤害给予与攻击段数

```csharp
// Hook.cs
decimal ModifyAttackHitCount(ICombatState combatState,
    AttackCommand attackCommand, int originalHitCount)
Task AfterDamageGiven(PlayerChoiceContext choiceContext, ICombatState combatState,
    Creature dealer, DamageResult results, ValueProp props,
    Creature target, CardModel cardSource)

// AbstractModel.cs
int ModifyAttackHitCount(AttackCommand attack, int hitCount)
Task AfterDamageGiven(PlayerChoiceContext choiceContext,
    Creature dealer, DamageResult result, ValueProp props,
    Creature target, CardModel cardSource)
```

`ModifyAttackHitCount` 修改一次 `AttackCommand` 的攻击段数；`AfterDamageGiven` 在攻击方造成伤害后触发，适合读取真实 `DamageResult` 做击杀、吸血或追击类效果。

**示例：造成攻击伤害后施加中毒**

```csharp
public override async Task AfterDamageGiven(PlayerChoiceContext choiceContext,
    Creature dealer, DamageResult result, ValueProp props,
    Creature target, CardModel cardSource)
{
    if (dealer != base.Owner) return;
    if (!props.IsPoweredAttack()) return;
    if (result.TotalDamage <= 0m) return;

    await PowerCmd.Apply<PoisonPower>(target, base.Amount, dealer, cardSource);
}
```

**自定义示例：让特定攻击多打一段**

```csharp
public override int ModifyAttackHitCount(AttackCommand attack, int hitCount)
{
    if (attack.Attacker != base.Owner) return hitCount;
    if (attack.CardSource?.Type != CardType.Attack) return hitCount;

    return hitCount + 1;
}
```

**实际覆写参考：** `EnvenomPower`、`HandDrill`、`ReaperFormPower` 等使用 `AfterDamageGiven`；当前 0.106.1 本体未发现 `ModifyAttackHitCount` 的实际覆写。

#### 充能球事件

```csharp
// Hook.cs
Task AfterOrbChanneled(ICombatState combatState,
    PlayerChoiceContext choiceContext, Player player, OrbModel orb)
Task AfterOrbEvoked(PlayerChoiceContext choiceContext,
    ICombatState combatState, OrbModel orb, IEnumerable<Creature> targets)
Task AfterModifyingOrbPassiveTriggerCount(ICombatState combatState,
    OrbModel orb, IEnumerable<AbstractModel> modifiers)

// AbstractModel.cs
Task AfterOrbChanneled(PlayerChoiceContext choiceContext, Player player, OrbModel orb)
Task AfterOrbEvoked(PlayerChoiceContext choiceContext,
    OrbModel orb, IEnumerable<Creature> targets)
Task AfterModifyingOrbPassiveTriggerCount(OrbModel orb)
```

这些通知钩子分别对应充能球被生成、被激发、被动触发次数被修改后的时点。`AfterModifyingOrbPassiveTriggerCount` 的 Hook 层会把参与修改的模型集合传给调度器。

**示例：充能球生成后闪光**

```csharp
public override Task AfterOrbChanneled(PlayerChoiceContext choiceContext,
    Player player, OrbModel orb)
{
    if (player != base.Owner) return Task.CompletedTask;

    Flash();
    return Task.CompletedTask;
}
```

**示例：镀金线缆式增加被动触发次数**

```csharp
public override int ModifyOrbPassiveTriggerCounts(OrbModel orb, int triggerCount)
{
    if (orb.Owner != base.Owner) return triggerCount;

    return triggerCount + 1;
}

public override Task AfterModifyingOrbPassiveTriggerCount(OrbModel orb)
{
    Flash();
    return Task.CompletedTask;
}
```

**实际覆写参考：** `Metronome` 使用 `AfterOrbChanneled`；`ThunderPower` 使用 `AfterOrbEvoked`；`GoldPlatedCables` 使用 `ModifyOrbPassiveTriggerCounts` 与 `AfterModifyingOrbPassiveTriggerCount`。

#### 奖励、休息处与费用晚阶段

```csharp
// Hook.cs
IEnumerable<AbstractModel> ModifyCardRewardAlternatives(IRunState runState,
    Player player, CardReward cardReward,
    List<CardRewardAlternative> alternatives)
bool ShouldForcePotionReward(IRunState runState, Player player, RoomType roomType)

// AbstractModel.cs
bool TryModifyCardRewardAlternatives(Player player,
    CardReward cardReward, List<CardRewardAlternative> alternatives)
bool TryModifyRewardsLate(Player player, List<Reward> rewards, AbstractRoom room)
bool TryModifyRestSiteOptions(Player player, ICollection<RestSiteOption> options)
bool TryModifyEnergyCostInCombatLate(CardModel card,
    decimal originalCost, out decimal modifiedCost)
bool ShouldForcePotionReward(Player player, RoomType roomType)
```

`TryModifyCardRewardAlternatives` 直接修改奖励候选列表并返回是否参与；`TryModifyRewardsLate` 是奖励列表的晚阶段修改；`TryModifyRestSiteOptions` 对休息处选项集合原地增删；`TryModifyEnergyCostInCombatLate` 在常规费用修改后提供短路式晚阶段覆盖。`ShouldForcePotionReward` 返回 `true` 可强制指定房间类型产出药水奖励。

**示例：添加一个卡牌奖励替代选项**

```csharp
public override bool TryModifyCardRewardAlternatives(Player player,
    CardReward cardReward, List<CardRewardAlternative> alternatives)
{
    if (player != base.Owner) return false;

    alternatives.Add(new CardRewardAlternative("take_gold", async () =>
        await PlayerCmd.GainGold(25m, player, wasStolenBack: false),
        PostAlternateCardRewardAction.EndSelectionAndCompleteReward));
    return true;
}
```

**示例：休息处增加一个自定义选项**

```csharp
public override bool TryModifyRestSiteOptions(Player player,
    ICollection<RestSiteOption> options)
{
    if (player != base.Owner) return false;

    options.Add(new DigRestSiteOption(player));
    return true;
}
```

**实际覆写参考：** `PaelsWing` 使用 `TryModifyCardRewardAlternatives`；`Girya`、`Shovel`、`MeatCleaver` 等使用 `TryModifyRestSiteOptions`；`Driftwood`、`Midas`、`Vintage` 使用 `TryModifyRewardsLate`；`BrilliantScarf` 与 `VoidFormPower` 使用费用短路修改。

#### 牌组、目标与能力移除

```csharp
// Hook.cs
bool ShouldPowerBeRemovedOnDeath(PowerModel power)

// AbstractModel.cs
Task AfterAddToDeckPrevented(CardModel card)
Task AfterTargetingBlockedVfx(Creature blocker)
bool ShouldPowerBeRemovedOnDeath(PowerModel power)
```

`AfterAddToDeckPrevented` 在 `ShouldAddToDeck` 阻止入牌组后触发；`AfterTargetingBlockedVfx` 用于目标选择被拦截时播放或响应拦截方表现；`ShouldPowerBeRemovedOnDeath` 是全局监听器对某个 `PowerModel` 死亡后是否移除的判断，和 `PowerModel.ShouldPowerBeRemovedAfterOwnerDeath()` 这个能力自身方法是两个入口。

**示例：目标被阻止时播放反馈**

```csharp
public override Task AfterTargetingBlockedVfx(Creature blocker)
{
    if (blocker != base.Owner) return Task.CompletedTask;

    Flash();
    return Task.CompletedTask;
}
```

**示例：保护幻象类能力不随死亡移除**

```csharp
public override bool ShouldPowerBeRemovedOnDeath(PowerModel power)
{
    if (power.Owner != base.Owner) return true;
    if (power is not IllusionPower) return true;

    return false;
}
```

**实际覆写参考：** `IllusionPower` 使用 `ShouldPowerBeRemovedOnDeath`。当前 0.106.1 本体未发现 `AfterAddToDeckPrevented` / `AfterTargetingBlockedVfx` 的实际覆写，它们更偏向 Mod 扩展点。

---

### 23.5 性能考虑与最佳实践

1. **选择正确的阶段：** 如果你的钩子不依赖其他钩子的结果，使用 Early 阶段。如果需要看到最终值，使用 Late 阶段。
2. **避免在 Modify 钩子中做重操作：** Modify 钩子可能每个伤害计算都被调用多次。
3. **Should 钩子应保持纯净：** 除了返回 `true/false`，不应有副作用。
4. **使用 `TryModify` 而非 `Modify` + 外部标志：** `TryModify` 的短路语义内置在框架中。
5. **Flash() 的时机：** 闪光应在 `After*` 通知钩子中触发，而非 `Modify*` 中（避免在预览计算时闪光）。

---

> **文档版本：** v4.2
> **最后更新：** 2026-05-30
> **覆盖钩子总数：** 130+ 个钩子，65+ 个实际示例
> **覆盖范围：** Hook.cs 0.106.1 关键静态方法 + AbstractModel.cs 虚方法 + CardModel.cs 方法/事件 + PowerModel.cs 能力系统 + RelicModel.cs 遗物系统 + PotionModel.cs 药水系统 + CombatManager 战斗循环 + Creature 死亡/格挡流水线 + 房间/地图/奖励钩子 + Mod 系统 API

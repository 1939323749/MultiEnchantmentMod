# MultiEnchantmentMod v2 发布说明

API 版本号为 2。下游 mod 应使用 `MultiEnchantmentApi.RequireApiVersion(2)` 检查运行时兼容性。

## v2.4.1 框架改进

### 术语统一："extra icon" → "marker"（原地改名）

所有 `*ExtraIcon*` 公开名称已统一改名为 `Marker*`。引用旧名的代码改名后重新编译即可；
**存档不受影响**（存储型 marker 按各 mod 自己的 `ModelId` 持久化，只改了框架基类与方法名）。

| 旧名称 | 新名称 |
|---|---|
| `ExtraIconEnchantmentModel` | `MarkerEnchantmentModel` |
| `ExtraIconDisplay` | `MarkerDisplay` |
| `ExtraIconDisplayContext` | `MarkerDisplayContext` |
| `ExtraIconDisplayProvider` / `ExtraIconDisplayPredicate` | `MarkerDisplayProvider` / `MarkerDisplayPredicate` |
| `ExtraIconPresentation` | `MarkerPresentation` |
| `ExtraIconRegistrationOptions` | `MarkerRegistrationOptions` |
| `ShownExtraIcon` | `ShownMarker` |
| `RegisterExtraIcon<T>`（×3 重载） | `RegisterMarker<T>` |
| `RegisterExtraIconDisplayProvider` | `RegisterMarkerDisplayProvider` |
| `RefreshExtraIcons(card)` / `RefreshExtraIcons()` | `RefreshMarkers(card)` / `RefreshMarkers()` |
| `GetShownExtraIcons` / `GetShownExtraIconDetails` | `GetShownMarkers` / `GetShownMarkerDetails` |
| `IsExtraIconShown` | `IsMarkerShown` |
| 参数 `includeExtraIcons` | `includeMarkers` |
| 能力查询字符串 `"ExtraIcon"` | `"Marker"` |

### 隐形附魔（Invisible Enchantment）
`[Enchantment(Invisible = true)]`（或 fluent `.Invisible()`）：完整玩法附魔，但不画徽章、
不占原版主槽、应用时无附魔闪光。描述文字、hover、发光、计数、存档、联机同步全部正常。
查询：`MultiEnchantmentApi.IsInvisibleEnchantment(type)`。示例见 Samples/35。

### 存储型 marker 友好增删改查
`GetOrAddMarker<T>` / `SetMarker<T>` / `AddMarkerAmount<T>` / `ModifyMarker<T>` / `RemoveMarker<T>`，
以及实例自我修改 `marker.SetAmount` / `marker.AddAmount` / `marker.NotifyChanged()` ——
自动从 ModelDb 解析实例、自动通知变更并刷新图标行。marker 类型必须注册进 ModelDb。

## v2.2 框架改进（本批次）

### 注册决议规则正式契约化
每个 `EnchantmentModel` 子类至多允许一条 **Definition entry** —— 即设置了 `Stack` / 任意 lifecycle hook (`OnApplied` / `OnRemoved` / `OnCombatStart` 等) / `WithScope` / `WhenActiveStatus` / `OnMergedDelta` / `OnMergedRefresh` / `Execution` / 任一 stacked async hook / 任一 vanilla 桥接 hook (`OnCardPlayed` / `OnSiblingApplied` / `OnAfterDamageReceived` / ...) 的 `Register<T>()` 调用。

第二次试图注册带 Definition 字段的 entry 会抛 `InvalidOperationException`（注解扫描路径会 log warn 并跳过）。`TrackKeyword` / `FormatExtraText` / `ModifyDynamicVar` / `ModifyEnergyCostInCombat` / `ModifyCardPlayCount` / `HistoryDisplay` / `HistoryText` / `VisualSlices` 等 **Contribution-only** entry 不受此限，可被多个 mod 各自追加。

迁移建议：如果你过去依赖"用第二次 Register 覆盖第一次"的行为，请把所有 Definition 字段聚合到单个 fluent 链里。

### 注册表生命周期
- 第一次 `Hook.BeforeCombatStart` 触发时自动 `Seal` —— 进入战斗后任何 `Register<T>()` / `ScanCallingAssembly` 都会被 log 拒绝。
- `MultiEnchantmentApi.SealRegistry()` 仍可手动调用（开发者 escape hatch）。
- registry 读取路径不再每次反射扫装配；所有显式扫描在 `Initialize` 末尾一次完成。

### Sidecar 存档键升级 v2 + 双读
- 新 enchantment key 基于稳定 GUID（`MultiEnchantmentInstanceId` SavedProperty），不再因 amount/upgrade 漂移。
- 新 card key 基于稳定 GUID（`MultiEnchantmentCardInstanceId` SavedProperty）；`SerializableCard.Id` 只是 card definition id，不能区分同名卡实例。
- 旧 key (`{Id}#u..#e..#f..`) 读取时仍能命中并迁移到 v2，迁移后写一行 info log；旧 key 计划在 v4 移除。

### `IEnchantmentRegistration` 能力接口拆分
为方便文档化和未来按能力扩展，把 `IEnchantmentRegistration` 拆成五个 super-interface：
- `IStackingRegistration`：Stack / scope / Execution / OnMergedDelta / OnMergedRefresh
- `ILifecycleRegistration`：OnApplied / OnRemoved / OnCombatStart 等所有 lifecycle 与 vanilla 桥接
- `IDynamicVarRegistration`：ModifyDynamicVar / ModifyEnergyCostInCombat / ModifyCardPlayCount
- `IPresentationRegistration`：TrackKeyword / FormatExtraText / VisualSlices / HistoryDisplay
- `IStackedHookRegistration`：OnPlayStacked / BeforeCardPlayedStacked 等 stacked async hooks

`IEnchantmentRegistration` 现在继承上述五个接口。所有方法签名、扩展方法、二进制兼容性都保持不变 —— 第三方代码无需任何修改。

## 一句话总结

v2 新增 `ModifyDynamicVar` API 让多个附魔可以协同修改同一个动态变量（伤害 / 格挡 / `{Times}` / 第三方自定义 key），让 `amount <= 0` 与原版一致按 1 处理，并加了第三方未注册附魔的 auto-detect 兜底。

---

## 新功能

### 1. `ModifyDynamicVar` 动态变量贡献

详见 [ENCHANTMENT_LIFECYCLE.md §11](ENCHANTMENT_LIFECYCLE.md)。

- 三种注册方式：Tier A 类上 `[ModifyDynamicVar]`、Tier B Definition 上 `[ModifyDynamicVar]`、Tier C `.ModifyDynamicVar(...)` fluent。
- Per-slice 调用：MergeAmount 下 N 个 stack = N 次调用，作者写"单次效果"公式即可。
- Case-insensitive：`"damage"` / `"Damage"` 都能匹配。

### 2. 第三方未注册 EnchantmentModel 的 auto-detect

如果某个第三方 `EnchantmentModel` 子类 override 了 `EnchantDamage*` / `EnchantBlock*` 但**未**调 `Register<...>()`，本 mod 看到时自动注册为 `MergeAmount + SharedAcrossStack`，并写一行 info log。

**作者 opt-out**：在 `[ModInitializer]` 里显式注册其它 stack behavior。例如：

```csharp
MultiEnchantmentApi.Register<MyEnchantment>()
    .Stack(StackBehavior.DisallowDuplicate, StatusAggregation.AnyInstanceCountsAsOne)
    .Commit();
```

---

## 行为变化（潜在 breaking）

### A. `CardCmd.Enchant(_, _, amount)` 的 `amount <= 0` 按 1 处理

旧行为：`amount <= 0` 时 `ApplyEnchantment` 抛 `ArgumentOutOfRangeException`，可能回退到 vanilla。
新行为：直接当作 `amount = 1` 处理。

理由：vanilla `CardCmd.Enchant` 不校验 amount，dev console `enchant <id>` 不带数字时默认传 0，原行为会让常见用法失败。

**影响**：仅当下游显式依赖"传 0 会抛"才有影响——这种依赖几乎不存在。

### B. `RequiresMultiEnchantmentLogic(card)` 多了一个返回 true 的条件

如果卡上任何附魔类型注册了 `ModifyDynamicVar` 贡献，即使卡只有一个附魔且无 merged stack，该函数也返回 true。原本这种情况会走 vanilla 快速路径。

**影响**：UpdateCardPreview prefix 在更多场景会跑——但是 `ApplyDynamicVarEnchantments` 在没注册任何贡献时立即返回，无副作用。对未升级到 v2 注册 API 的下游 mod 完全透明。

### C. 第三方未注册 EnchantmentModel 默认行为变了

旧行为：第三方 EnchantmentModel 子类未注册时按 `DisallowDuplicate` 处理。
新行为：如果 override 了 `EnchantDamage*` / `EnchantBlock*`，按 `MergeAmount` 处理；否则仍 `DisallowDuplicate`。

**影响**：第三方 mod 如果有 `EnchantDamageAdditive` 这种"硬编码 +5"且**没**调用 `Register<...>()`，现在能被堆叠了。如果不希望这样请显式注册——见上面 opt-out 示例。

---

## 不变的部分

- 现有 fluent / attribute / Definition 注册接口完全向后兼容。
- `OnMergedDelta` / `OnMergedRefresh` / `TrackKeyword` / `FormatExtraText` / `VisualSlices` / 生命周期 hook 全部不变。
- 内置 vanilla 附魔（Glam / Spiral / Sharp / Nimble 等）的行为不变。
- 存档格式不变。

---

## 升级步骤

1. 在初始化入口调用 `MultiEnchantmentApi.RequireApiVersion(2)`。
2. 想用 `ModifyDynamicVar` 的话照 §1 的三种风格之一写。
3. 重启游戏，看 `%APPDATA%/SlayTheSpire2/logs/godot.log`：
   - 你的附魔被 scanner 接收时会有 `[StackApi] Scanned N attribute-based enchantment registration(s)` 之类。
   - 任何 auto-registered 警告会在第一次该类型被用时打出，认领或 opt-out。

---

## Reference

- 详细 API 文档：[ENCHANTMENT_LIFECYCLE.md §11](ENCHANTMENT_LIFECYCLE.md)
- Samples：[14_DynamicVarComposition.cs](MultiEnchantmentMod.Samples/Samples/14_DynamicVarComposition.cs) 和 [15_UnregisteredAutoMerge.cs](MultiEnchantmentMod.Samples/Samples/15_UnregisteredAutoMerge.cs)
- 当前 API 版本：[MultiEnchantmentApiVersion.cs](Api/MultiEnchantmentApiVersion.cs) `Current = 2`

## v0.106 SavedProperty net ID 修复

v0.106 玩家日志中的 `SavedProperty name MultiEnchantmentMergedStackAmounts could not be mapped to any net ID!` 来自旧存档里残留的 `MultiEnchantment*` 字段。新版会在加载/保存路径把这些字段同步到 `multi_enchantment_save_sidecar.json`，并在本地 run save 写盘前从 vanilla 存档 DTO 中剥离，避免卸载 mod 后 vanilla 再序列化这些字段时报错。

### v0.106.x 原生签名变更提醒

当前本仓库按 0.106.1 系列 DLL 复核。直接 patch vanilla `Hook` 或重写 vanilla block 附魔方法时，请同步以下签名：

- `Hook.BeforeSideTurnStart(ICombatState, CombatSide, IReadOnlyList<Creature>)`
- `Hook.AfterSideTurnStart(ICombatState, CombatSide, IReadOnlyList<Creature>)`
- `Hook.BeforeTurnEnd(ICombatState, CombatSide, IEnumerable<Creature>)`
- `Hook.AfterTurnEnd(ICombatState, CombatSide, IEnumerable<Creature>)`
- `EnchantmentModel.EnchantBlockAdditive(decimal originalBlock)`
- `EnchantmentModel.EnchantBlockMultiplicative(decimal originalBlock)`

`EnchantDamageAdditive` / `EnchantDamageMultiplicative` 仍保留 `ValueProp` 参数。MultiEnchantmentMod 的 public lifecycle 回调仍保持稳定形状，例如 `.OnSideTurnStart((card, enchantment, side) => ...)`；只有直接面向 vanilla API 时才需要处理 `participants`。

迁移步骤：

1. 先安装新版 MultiEnchantmentMod。
2. 打开旧 run，确认附魔状态恢复。
3. 保存退出一次。此时 vanilla run save 会被清理，mod 数据转入 sidecar。
4. 只有完成这次保存后，才建议卸载 mod 并用 vanilla 继续加载该 run。

多人说明：本轮优先保持现有 `Props` packet 同步兼容，因此 modded 多人会话内仍注册 `MultiEnchantment*` SavedProperty net ID；清理只发生在本地 run save 写盘边界。

---

## 后续微调（v2 稳定化）

这一节追加于 v2 稳定化轮。所有改动都保留在 API v2 内（`Current = 2` 不变），但有少量源码层面影响——尤其是异常处理与错误日志的行为。

### A. 回调异常隔离（`SafeInvoker`）

下游编写的所有附魔回调（`OnApplied` / `OnRemoved` / `OnMergedDelta` / `OnMergedRefresh` / 各类 vanilla hook 桥 / `TrackKeyword`'s `amountFn` / `FormatExtraText` / `VisualSlices` / `GetScope`）现在统一通过 `Api.Internal.SafeInvoker.Run` 进入。如果回调抛异常：

- 异常被吞掉，日志写一行 `[MultiEnchantment] <FullTypeName> (assembly=<modAsm>) threw in <hookName>: <ex>`。
- 该次回调返回文档化的 fallback：`OnRemoved` / `OnShouldDie` → `true`（不否决），`GetVisualSliceAmounts` / `GetVisualSlices` → `null`，`FormatExtraText` → 保持默认文本，`AmountFn` → `0`。
- 同一 (type, hook) 重复抛异常会按节流策略压缩日志：前 3 次详细栈跟踪，之后只打 message，超过 50 次彻底静默直至 `OnCombatStart` 触发 `ResetThrottle`。

**影响**：旧代码如果依赖"附魔回调抛异常会让 vanilla 行为接管"必须显式处理；现在该附魔的当前回调被跳过，其它附魔继续。

### B. `StackDefinition.MaxInstances`

新增可选属性 `int? MaxInstances`（默认 null = 不限制）。仅对 `DuplicateInstance` / `ExistenceStack` 生效——这两种 behavior 下当卡上同类型实例数达到 `MaxInstances` 时，`CanStackOnto` 返回 false 并打一次聚合 warn log（按类型节流，进程内只报一次）。

用法（fluent）：
```csharp
MultiEnchantmentApi.Register<MyAura>()
    .Stack(StackBehavior.ExistenceStack, StatusAggregation.SharedAcrossStack)
    .Commit();
// 或者通过显式构造 StackDefinition：
new StackDefinition(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned) { MaxInstances = 3 }
```

### C. 作用域存档跨版本告警

`MultiEnchantmentScopeData` payload 增加了 `scope_kind` 字段。如果存档时 Scope 是 `MaxActivationsScope` 但加载时该附魔已改为别的 Scope（mod 改版），加载仍尊重存档中的 `ActivationCount` / `TurnsRemaining`，但打一行 warn 提醒作者状态可能不符合新 Scope 语义。

### D. 注册路径错误模型

- `ModifyDynamicVarAttribute(string)` 构造器不再对空字符串抛异常——空 key 现在由扫描器在 `BuildContributionsFor` 时记录 warn 并跳过该贡献。编译期由分析器 **MEM009** 捕获。
- `AssemblyScanner` 的所有日志行现在带 `(assembly=<modAsm>)` 注解，可一眼定位哪个 mod 的注册有问题。
- 注册表 seal 后调用 `Register<T>().Commit()` 不再注册，而是写一行 error log 并返回 no-op `IDisposable`。

### E. 分析器扩展

- **MEM007** 严重级别从 Info 提升到 Warning。
- **MEM009**：`[ModifyDynamicVar]` 标注的方法签名必须是 `decimal(EnchantmentStackSnapshot, decimal)`，不符合编译报 Error。
- **MEM011**：`[Enchantment(MaxActivations=N)]` 使用默认 `ActivationTrigger.OnPlay` 时 Warn（这可能不是作者意图）。`ActivationTrigger` 不是合法的 C# attribute named-argument 类型；需要非默认触发器时，用 fluent `.MaxActivations(N, trigger)` 或 `EnchantmentDefinition.Scope`。
- **MEM012**：Definition class override `OnMergedDelta` 但 Stack 不是 `MergeAmount` 时 Warn（override 不会被调用）。

### F. 热路径性能

`EnchantmentRegistry.HasContributionsFor` 现在读 lock-free 的 `FrozenSet<string>` 快照，DynamicVar Harmony 后缀的每帧开销降低。无对外行为变化。

### G. 公开 API 基线

新增 [docs/public-api.md](docs/public-api.md)，按字母排序列出当前 `Api/` 下所有公开签名。任何对 `Api/` 公开表面的改动都应在 PR 中同步更新这份文件。

### 升级清单

- 下游无需改任何源码（所有变更对源码兼容）。
- 检查 `godot.log`：以前被忽略的回调异常现在以 `[MultiEnchantment]` 前缀显式输出——可能新增日志，但不影响行为。
- 想用 `MaxInstances` 加防御就显式设置；不设保持原行为。
- 重新跑分析器，修掉 MEM007/009/011/012 的新告警。

---

## API v2 缺口完善（增量补丁）

这一节追加于 v2 缺口完善轮。所有改动都保留在 API v2 内（`Current = 2` 不变），且**纯加法**——已有签名一律向后兼容，新接口方法用 C# 8 默认实现避免破坏既有适配器/桥。

### H. `ModifyDynamicVar` 重入守护

`MultiEnchantmentSupport.ApplyDynamicVarEnchantments` 现在带一道 `[ThreadStatic]` 重入栈：同一帧内对同一个 `(card, varKey)` 的递归求值会被识别并跳过，写一行 warn `ModifyDynamicVar reentrancy detected for var=<key> on card=<id>`。

**为什么需要**：作者写的贡献回调如果间接读取了同卡同 key 的派生值（例如附魔 A 通过 `card.GetDynamicVar("damage")` 在自己的 `damage` 贡献里再次触发求值），递归栈会一路打穿。在没有守护时这会立刻崩溃，难以诊断；现在第二层求值返回当前 `baseValue`，不污染最终结果。

**影响**：正常代码完全无感；仅当下游逻辑有意构造递归时会看到 warn——大概率是 bug 而非设计。

### I. `MultiEnchantmentApi.NotifyPropsChanged(EnchantmentModel)`

`Api/MultiEnchantmentApi.cs` 新增静态方法 `NotifyPropsChanged(EnchantmentModel enchantment)`，等价于内部的 `RefreshDerivedStateFor`：让作者在非「应用路径」回调（`OnAfterDamageReceived` / `OnTurnEnd` / 自定义事件 etc.）中改写附魔字段后，显式重算 visual slices / extra-card-text / dynamic-var 缓存。

**何时需要**：

- 应用路径（`OnApplied` / `OnMergedDelta` / `OnMergedRefresh` / `RemoveEnchantment` / 升级/替换）内**不需要**调用——框架自带刷新。
- 非应用路径内改写了影响 `FormatExtraText` / `VisualSlices` / `[ModifyDynamicVar]` 输出的字段时**必须**调用，否则 UI 会停留在上一次刷新时的值。

参见 [Sample 22 — PropsChangeRefresh](MultiEnchantmentMod.Samples/Samples/22_PropsChangeRefresh.cs)。

### J. 广播版卡牌事件 `OnAnyCard*`

`IEnchantmentRegistration` 新增四个**显式 opt-in** 钩子：

| 钩子 | 触发时机 |
| :-- | :-- |
| `OnAnyCardPlayed(playedCard, selfCard, self)` | 战斗中**任何**卡被打出后 |
| `OnAnyCardDrawn(drawnCard, selfCard, self)` | 战斗中**任何**卡被抽到后 |
| `OnAnyCardExhausted(exhaustedCard, selfCard, self)` | 战斗中**任何**卡被消耗后 |
| `OnAnyCardDiscarded(discardedCard, selfCard, self)` | 战斗中**任何**卡被弃掉后 |

与现有 per-card 版（`OnCardPlayed` 等，仅持有附魔的卡触发）相对应。`selfCard` 永远等于 `self.Card`，方便闭包内取用。同样受 `IsActive` 过滤——休眠的 ConditionalActive 不会收到广播。

**为什么显式 opt-in**：广播的派发成本与卡数 × 附魔数线性相关。多数附魔只关心自己的卡，因此默认不订阅；只有覆写了对应 virtual / fluent 接口才会被加入广播订阅表。

`EnchantmentDefinition<T>` 也加了 `protected virtual void OnAnyCardPlayed(...)` 等四个虚方法，Tier B 写法和 per-card 版一致。

参见 [Sample 19 — OnAnyCardPlayedBroadcast](MultiEnchantmentMod.Samples/Samples/19_OnAnyCardPlayedBroadcast.cs)。

### K. 同卡邻居事件 `OnSiblingApplied` / `OnSiblingRemoved`

`IEnchantmentRegistration` 新增两个钩子用于「同卡兄弟附魔」联动：

```csharp
OnSiblingApplied(Action<CardModel, EnchantmentModel /*self*/, EnchantmentModel /*newSibling*/>)
OnSiblingRemoved(Action<CardModel, EnchantmentModel, EnchantmentModel, RemovalReason>)
```

- `OnSiblingApplied` 在新邻居**已挂载**之后触发——回调内调用 `MultiEnchantmentApi.GetSiblings(card)` 能立刻看到它。
- `OnSiblingRemoved` 在邻居**即将被取下**之前触发，但**只在** `OnRemoved` veto 链放行后才发——被否决的移除不会广播。
- **不会自激**：同名实例的应用/移除不会通过该钩子回送给自己。
- 受 `IsActive` 过滤——休眠的接收者不会被通知。

`EnchantmentDefinition<T>` 同步加了 `protected virtual` 两个方法。

参见 [Sample 20 — SiblingAwareCombo](MultiEnchantmentMod.Samples/Samples/20_SiblingAwareCombo.cs)。

### L. `ScopeRuntimeStateView` 与 `EnchantmentStackSnapshot.ScopeStates`

`Api/ScopeRuntimeStateView.cs`：新公开 record，对 `ScopeRuntimeState` 内部状态做不可变快照——

```csharp
public sealed record ScopeRuntimeStateView(
    EnchantmentScope Scope,
    int ActivationCount,
    int TurnsRemaining)
{
    public bool IsExpired      { get; }   // LingerForTurns 已用完
    public bool IsLimitReached { get; }   // MaxActivations 已达上限
}
```

`EnchantmentStackSnapshot` 增加可选字段 `IReadOnlyDictionary<EnchantmentModel, ScopeRuntimeStateView>? ScopeStates`，以及便捷方法 `StateOf(EnchantmentModel)`。`MultiEnchantmentStackSupport.GetSnapshot` 自动填充该字典——`FormatExtraText` / `VisualSlices` / `[ModifyDynamicVar]` 等表现型钩子能直接读取剩余次数 / 剩余回合而无需触碰内部 `ScopeRuntimeState`。

老调用点（未指定该参数的 snapshot 构造）依旧合法——`ScopeStates` 默认 `null`，`StateOf` 返回 `null`。

参见 [Sample 21 — ScopeStateInPresentation](MultiEnchantmentMod.Samples/Samples/21_ScopeStateInPresentation.cs)。

### M. 高级查询 API（`MultiEnchantmentApi`）

`Api/MultiEnchantmentApi.cs` 新增三个 `[EditorBrowsable(Advanced)]` 静态方法：

| 方法 | 用途 |
| :-- | :-- |
| `ScopeRuntimeStateView? GetScopeState(EnchantmentModel)` | 返回当前 scope 状态快照；无 scope 时 `null`。 |
| `bool IsActive(EnchantmentModel)` | 当前 IsActive 求值结果（ConditionalActive、scope 限制综合判断）。 |
| `IReadOnlyList<EnchantmentModel> GetSiblings(CardModel?, EnchantmentModel? excludingSelf = null)` | 同卡所有附魔，可排除自身。 |

主要面向工具 / 调试 / 高阶组合。普通玩法路径仍走 snapshot / 内部回调。

### N. `StackDefinition.OnOverflow`：替换政策

§B 引入了 `MaxInstances` 但仅会拒绝超额应用（`Reject`）。本轮把 `OnOverflow` 加成枚举：

```csharp
public enum StackOverflowPolicy
{
    Reject,         // 默认：拒绝新应用，已有实例不变。
    ReplaceOldest,  // 移除最旧实例（按附魔顺序），再挂上新的；保持总数等于 MaxInstances。
    ReplaceNewest,  // 移除最新实例，再挂上新的；保持「最近的总是最新的」语义。
}
```

`StackDefinition.OnOverflow` 默认 `Reject`，行为与之前完全一致；只有显式设置才会触发驱逐。被驱逐的实例 `OnRemoved` 拿到 `RemovalReason.OverflowEvicted`（新增枚举值）。

主槽 `card.Enchantment` 永远不会被驱逐——升级管线持有它的所有权，只在 extras 列表内做 FIFO/LIFO。

`IEnchantmentRegistration.Stack(StackDefinition)` 新增重载，可一次性指定全套字段（用 default interface method 提供回退实现，老适配器兼容）：

```csharp
StackDefinition def = new(
    StackBehavior.DuplicateInstance,
    StatusAggregation.PerInstanceOwned)
{
    MaxInstances = 5,
    OnOverflow = StackOverflowPolicy.ReplaceOldest,
};

MultiEnchantmentApi.Register<MyAura>()
    .Stack(def)
    .Commit();
```

参见 [Sample 23 — StackOverflowReplace](MultiEnchantmentMod.Samples/Samples/23_StackOverflowReplace.cs)。

### O. 公开 API 基线增量

[docs/public-api.md](docs/public-api.md) 同步更新：新增以下签名

- `MultiEnchantmentApi.NotifyPropsChanged(EnchantmentModel)`
- `MultiEnchantmentApi.GetScopeState(EnchantmentModel)`
- `MultiEnchantmentApi.IsActive(EnchantmentModel)`
- `MultiEnchantmentApi.GetSiblings(CardModel?, EnchantmentModel?)`
- `IEnchantmentRegistration.Stack(StackDefinition)`
- `IEnchantmentRegistration.OnAnyCardPlayed/Drawn/Exhausted/Discarded`
- `IEnchantmentRegistration.OnSiblingApplied/Removed`
- `EnchantmentDefinition<T>.OnAnyCardPlayed/Drawn/Exhausted/Discarded`
- `EnchantmentDefinition<T>.OnSiblingApplied/Removed`
- `ScopeRuntimeStateView`、`StackOverflowPolicy`、`StackDefinition.OnOverflow`
- `RemovalReason.OverflowEvicted`、`RemovalReason.ConditionMet`
- `EnchantmentStackSnapshot.ScopeStates`、`EnchantmentStackSnapshot.StateOf(EnchantmentModel)`

### P. 单实例 Scope 覆盖

新增 `MultiEnchantmentApi.Enchant(card, enchantment, amount = 1, scopeOverride = null)`：在普通 v2 应用管线里直接给这一次附魔传入实例级作用域。注册时的 `WithScope(...)` / `EnchantmentDefinition<T>.Scope` 仍是默认值；具体实例优先使用 `OverrideScope ?? registryScope`。

允许作为覆盖值的 scope 仅限可持久化、无谓词类型：

- `EnchantmentScope.Permanent`
- `EnchantmentScope.UntilCombatEnds`
- `EnchantmentScope.UntilTurnEnds`
- `EnchantmentScope.LingerForTurns(N)`
- `EnchantmentScope.MaxActivations(N, trigger)`

`ConditionalActive` / `RemoveWhen` 携带 `Func<>` 谓词，无法可靠存档 / 联机同步，因此作为实例覆盖会被拒绝：`Enchant(...)` 返回 `null`，`SetScopeOverride(...)` 返回 `false`，并写一行 warn。

同时新增追溯 API：

```csharp
MultiEnchantmentApi.SetScopeOverride(card, enchantment, EnchantmentScope.UntilTurnEnds);
MultiEnchantmentApi.SetScopeOverride(card, enchantment, null); // 清除覆盖，回到注册默认
```

追溯修改计数规则：

- 改成 `LingerForTurns(N)`：`TurnsRemaining = N`。
- 改成 `MaxActivations(N, trigger)`：`ActivationCount = 0`。
- 改成其它 scope：保留已有计数。
- 清除覆盖：保留已有计数，之后按注册默认 scope 解释。

覆盖信息随既有 `MultiEnchantmentScopeData` 一起持久化；`ScopeRuntimeStateView` 新增 `HasOverride`，tooltip / debug UI 可直接显示该实例是否使用了覆盖值。

参见 [Sample 24 — PerInstanceScope](MultiEnchantmentMod.Samples/Samples/24_PerInstanceScope.cs)。

### 升级清单（单实例 Scope 覆盖）

- 想让同一种附魔在不同来源下有不同生命周期时，优先用 `MultiEnchantmentApi.Enchant(..., scopeOverride: ...)`，不用再拆成多个 EnchantmentModel 类型。
- 已挂上的实例需要临时变更生命周期时，用 `SetScopeOverride(card, enchantment, scope)`；传 `null` 清除覆盖。
- 不要把带谓词的 `ConditionalActive` / `RemoveWhen` 当作实例覆盖；这类逻辑仍应注册在类型级 scope 上。

---

## v2.3.0 新增（附魔主题卡组支撑）

这一轮为下游「注入 / 附灵 / 觉醒 / 激发 / 再次注入」卡组动词补齐 API，**纯加法**，向后兼容。

### Q. 异步应用与「被附魔时」通知

| API | 用途 |
| :-- | :-- |
| `Task<EnchantmentModel?> EnchantAsync(PlayerChoiceContext?, card, enchantment, amount = 1, scopeOverride = null)` | `Enchant` 的异步版，把 `PlayerChoiceContext` 透传给应用后钩子，并 await 它们——可在附魔落定后安全地发命令 / autoplay。 |
| `IDisposable AfterCardEnchanted(AfterCardEnchantedHandler)` | 订阅卡级「被附魔」通知，用于 keyword/marker 系统（如 **激发**：被附魔时把这张牌打出），无需把 marker 建模成附魔。 |
| `AfterSiblingAppliedStacked` （stacked 钩子 + `StackedAfterSiblingAppliedContext` / `StackedAfterSiblingAppliedHandler`） | 按附魔类型粒度反应「同卡又挂了一个附魔」，携带 `EnchantmentStackSnapshot`。 |

**关键契约（务必注意）：**

- `AfterCardEnchanted` **只在异步路径**（`EnchantAsync` / `CopyEnchantmentAsync`）触发；同步 `Enchant` / `CopyEnchantment` 及 vanilla 附魔路径**不触发**。激发 / autoplay 必须走 `EnchantAsync`。
- `AfterSiblingAppliedStacked` 在**同步和异步路径都触发**，但同步路径以 `.GetAwaiter().GetResult()` 阻塞分发——处理器**不得** await 真正的游戏命令，否则可能卡死游戏线程。命令式逻辑请用 `AfterCardEnchanted` + `EnchantAsync`。

> docs/public-api.md「Reacting to "this card just got enchanted": which hook?」一节给了二者的决策表。

### R. 复制 / 再次注入

| API | 用途 |
| :-- | :-- |
| `EnchantmentModel? CopyEnchantment(target, source, scopeOverride = null)` | clone 一个 live 附魔实例（保留 Amount/Props/自定义字段）并通过 v2 管线重新挂到 `target`，重置运行时 scope 计数。 |
| `Task<EnchantmentModel?> CopyEnchantmentAsync(PlayerChoiceContext?, target, source, scopeOverride = null)` | 异步版，重置 scope 后再发 `AfterCardEnchanted` 通知。 |
| `EnchantmentModel? GetMostRecentlyAppliedEnchantment(card)` | 当前 live、最近一次被应用 / 合并的附魔实例。 |
| `EnchantmentModel? GetMostRecentlyAppliedEnchantmentThisTurn(card)` | **本回合**内最近被应用的附魔；每个玩家回合开始时重置，本回合没注入则返回 `null`。对应卡面「本回合你最后注入过的附魔再次注入」。瞬时状态，不进存档。 |

「再次注入」典型写法：`GetMostRecentlyAppliedEnchantmentThisTurn(source)` 取到本回合最后注入的附魔，再 `CopyEnchantment(target, last)` 注到另一张手牌。

参见 [Sample 30 — InjectInfuseAwaken](MultiEnchantmentMod.Samples/Samples/30_InjectInfuseAwaken.cs)。

### S. 公开 API 基线增量（v2.3.0）

[docs/public-api.md](docs/public-api.md) 同步新增：`EnchantAsync`、`AfterCardEnchanted` / `AfterCardEnchantedContext` / `AfterCardEnchantedHandler`、`CopyEnchantment` / `CopyEnchantmentAsync`、`GetMostRecentlyAppliedEnchantment` / `GetMostRecentlyAppliedEnchantmentThisTurn`、`AfterSiblingAppliedStacked` / `StackedAfterSiblingAppliedContext` / `StackedAfterSiblingAppliedHandler`。

### T. 附魔卡集缺口补全（条件判定 / 级联守护 / 动词标签 / 移动）

针对火/灼烧 + 附魔扩展卡集的缺口排查补齐，**纯加法**。

- **条件判定**：`HasAnyEnchantment(card)`、`GetEnchantmentCount(card)` 默认只统计 gameplay 附魔，排除 `ExtraIconEnchantmentModel` 这类纯 UI marker；需要把 marker 也算进去时传 `includeExtraIcons: true`。覆盖「若这张牌有附魔」「所有手牌都有附魔才能打出」「每有一张有附魔的牌造成 X」「费用最低的有附魔的牌」等。
- **级联守护**：`AfterCardEnchantedContext` 增 `CascadeDepth`——顶层 `0`，由某个 `AfterCardEnchanted` 处理器内再次附魔触发的嵌套回调为 `>0`。旧神咏唱 / 焚心契约 / 灵魂火炉 等「附魔时再附魔」卡用 `if (ctx.CascadeDepth > 0) return;` 一行防无限递归。`DispatchAfterCardEnchanted` 用 `[ThreadStatic]` 计数器实现（沿用 ModifyDynamicVar 重入栈的单线程假设）。
- **移动**：`MoveEnchantment(source, target, e)` = 保留 scope 进度复制后从 source 移除；`CopyEnchantment(..., preserveScopeProgress: true)` 保留剩余回合/激活数（默认仍重置，用于「再次注入」）。灵纹转写 / 万象同调逐个调用。

> **边界说明**：「注入 / 附灵 / 觉醒」是下游火/附魔卡包的主题词，**不进**前置 API。前置只提供通用的 `EnchantAsync` + `AfterCardEnchanted`（含 `Card` / `AppliedEnchantment` / `ScopeOverride` / `CascadeDepth`）。动词命名、统计、专属事件由下游自己包一层（例如 `EmberVerbs.Inject/Infuse/Awaken` 转发 `EnchantAsync(scope)` 后派发下游自己的 `AfterVerbEnchanted` 事件）。

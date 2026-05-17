# MultiEnchantmentMod v2 发布说明

API 版本号为 2。下游 mod 应使用 `MultiEnchantmentApi.RequireApiVersion(2)` 检查运行时兼容性。

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

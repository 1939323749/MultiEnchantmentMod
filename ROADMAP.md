# MultiEnchantmentMod 线路图

> 更新于 2026-06-11。已完成项会移到文末"已完成"。优先级依据：API 缺口分析（对比游戏 Hook 全集与已桥接面）+ 第三方 issue（[API Gap 模板](.github/ISSUE_TEMPLATE/api-gap.yml)）反馈。
> 约束提醒：所有公开 API 变更必须**纯加法**（新增重载/新接口方法），不得原地改签名（下游 mod 会 MissingMethodException）。

## API 缺口（按价值排序）

### 1. 克隆钩子扩展到牌组层遗物复制
`OnCardCloned` 目前覆盖战斗内 `CardModel.CreateClone` 与休息处 Clone 选项。牌组层的 `RunState.CloneCard` 调用方（多莉的镜子、蛋类遗物、BingBong、Glitter、LavaLamp、SilverCrucible、SilkenTress、FresnelLens、WingCharm、Hoarder、Specialized、Reflections 事件等）只做附魔继承、不触发钩子——因为 `RunState.CloneCard` 同时被牌组升级预览 UI（`NDeckUpgradeSelectScreen`、出战斗时的 `NUpgradePreview`）使用，直接 patch 会让作者钩子误触发到预览克隆上。需要先解决预览判别（候选：patch 预览 UI 方法设置抑制标志）。

### 2. 升级迁移辅助（OnUpgradeMigrate）
`OnCardUpgraded` / `OnCardDowngraded` 已有，但"卡升级时我的加成参数也成长"仍需作者手写。可提供声明式辅助（如 `.OnUpgradeScale(amountPerUpgrade)` 或带新旧状态的迁移上下文）。

### 3. 堆位流转拦截（result pile）
缺"卡牌打出后将进入哪个堆"的修改点（回手 / 改进消耗堆类 mechanic），目前只有事后通知 `OnCardChangedPiles`。游戏侧入口：`GetResultPileType`。

### 4. 守卫系列补全
只桥接了 `OnShouldDie`；`ShouldDraw` / `ShouldClearBlock` / `ShouldGainGold` / `ShouldEtherealTrigger` 等 veto 链没有附魔版。按需求实现（等 issue）。

### 5. 能量链
`ModifyEnergyGain` / `ModifyMaxEnergy` 贡献、`AfterEnergySpent` 通知。能量是核心资源，但与"卡牌附魔"的归属关系较弱（多为玩家级事件），需要设计好"哪张卡的附魔有资格贡献"的语义。

### 6. 遗物 / 药水事件
`OnAnyRelicObtained`、`OnRelicFlashed`、`OnAnyPotionUsed` 等。较冷门，等 issue 模板有真实需求再做。

### 7. Power Received 方向
本轮只桥接了 Given（卡牌给出）。Received（生物接收）是生物级事件，若有"宿主卡的拥有者受到能力时…"类需求再设计。

## 多人模式

- **玩家上下文注入**：Hook 层不区分"我的卡 / 队友的卡"，回调缺玩家身份信息，"队友合作"类附魔写不出来。小步方案：给现有广播钩子 context 增加 owner/player 字段（加字段二进制安全）。
- **Co-Op 专用广播钩子**：`OnCoOpFriendlyCardPlayed` 等，依赖上一项。
- 存档侧已就绪（SP/MP sidecar 分离、GUID 实例 key）。

## 工具链与质量

- **Analyzer 扩展**：现有诊断只覆盖 Definition 冲突、兼容性 attribute、`ModifyDynamicVar` 签名三类。候选新诊断：在 stacked hook 中同步调用 `Enchant` 而非 `EnchantAsync`、fluent 链忘记 `.Commit()`、同一附魔同时覆写 vanilla 数值虚方法又注册同名贡献通道（双重计数）。
- **文档英文化**：docs 下除 marker-wiki 外全是中文，NexusMods 国际用户无法阅读 v2 API wiki。
- **遥测反哺**：用 Supabase 数据（`deck_at_enchantment` 视图、run journey、crash_version_snapshot）做定期报表：附魔组合使用率（指导缺口优先级）、旧存档 key 迁移日志出现频率（决定 v4 移除时机）、按游戏版本切片的崩溃趋势。
- **仓库清理**：`dll_backup_2026-06-05/`、`.ilspy_tmp_thequeen/`、Samples/Analyzers 的 `obj/` 产物清理或补 `.gitignore`。

## 存档格式

- **v4：移除旧 sidecar key**（`{Id}#u..#e..#f..` 读取迁移路径，MIGRATION_V3 已预告）。动手前先用遥测统计旧 key 迁移日志的残余出现率。

## 已完成

- **2026-06-11（缺口完善第二轮）**：Power 链桥接（`ModifyPowerAmountGiven` contribution + `OnCardAppliedPower` 通知；patch `Hook.ModifyPowerAmountGiven` / `Hook.AfterPowerAmountChanged`，签名 0.106.x / 0.107.0 双版本一致）；变形/克隆生命周期钩子（`OnCardTransformed` 桥接 `CardCmd.Transform` 的 `AfterTransformedFrom/To` 配对，含 SovereignBlade 覆写补丁；`OnCardCloned` 桥接 `CardModel.CreateClone` + 休息处 Clone 选项，UI 预览不触发）。样例 33 / 34。
- **缺口完善第一轮（v2.x）**：广播钩子 `OnAnyCard*`、邻居事件 `OnSiblingApplied/Removed`、`ModifyDynamicVar` / `ModifyEnergyCostInCombat` / `ModifyCardPlayCount` / `ModifyHandDraw`、`MaxActivations` + `StackOverflowPolicy`、`ScopeRuntimeStateView`、单实例 scope 覆盖。

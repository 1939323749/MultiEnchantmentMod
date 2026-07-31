# 游戏版本更新适配工作流

STS2 每次更新后，把这个 mod 迁到新版的标准流程。核心判断：**编译器是最好的检测器，
但它有三个盲区**——本工作流只在盲区上花自动化，其余交给 `dotnet build`。

| 断裂类型 | 谁能发现 | 表现 |
|---|---|---|
| 签名/类型变化（直接引用的成员） | ✅ 编译器，~130 个 CS0246/CS0117 | 响亮、精确 |
| 字符串反射目标改名（`AccessTools.Field(typeof(X), "_f")`） | ❌ 编译器看不见 | 运行时静默 null |
| 手抄的原版方法体逻辑变了（`// Base-game source:`） | ❌ 编译器看不见 | 静默执行上个版本的规则 |
| 被 patch 方法签名没变但语义变了 | ❌ 编译器看不见 | 行为漂移、联机 desync |

后三行就是 `scripts/adapt/surface-diff.py` 的全部存在理由。

---

## Phase 1 · 定位（~2 min）

```bash
bash scripts/adapt/version-status.sh
```

一次列出**所有钉着版本号的地方**：装的游戏版本 + DLL 哈希、语料库有哪些版本、
`.ci/dll-version.txt`（CI 编译目标）、manifest 的 `version` / `min_game_version`、
当前分支、已部署到游戏和 ModUploader 的 DLL 时间戳、订阅中的创意工坊副本。

判断要点：

- **先确认 Steam 分支**。`public` 与 `public-beta` 经常差一到两个版本；master 若已适配
  beta 版，在 `public` 上编译会直接撞 CS0246 墙。开工前把 Steam 切到目标分支。
- `release_info.json` 只是构建时写入的文件，切分支后可能与二进制不一致。真正的身份是
  `sha256`（`refresh-corpus.sh` 会把它写进语料目录的 `.dll-sha256`，status 脚本自动比对）。
  存疑时直接探符号：

  ```bash
  python -c "d=open(r'<game>/data_sts2_windows_x86_64/sts2.dll','rb').read();print(d.count(b'GetResultLocationForCardPlay'))"
  ```

  某版本新增/删除的方法名在 DLL 的字符串堆里能直接数出来，比任何元数据都可信。

## Phase 2 · 刷新语料（~1 min）

```bash
bash scripts/adapt/refresh-corpus.sh          # --force 可重做
```

反编译到 `sts2_mods/sts2_decompiled/<version>/`，写入 `.dll-sha256`，并打印整包 churn
（新增/删除/变更类型数）。**这个数字只用来感知更新规模，不要照着它排查**——
v0.107.0→v0.107.1 全包 1482 个类"有变化"，而实际影响本 mod 的成员是 **0** 个，差别全是
`///` 注释和成员重排噪声。

## Phase 3 · 差异面分诊（~5 min，本流程的核心）

```bash
python scripts/adapt/surface-diff.py v0.108.0 v0.109.0 --show-diff
```

脚本从**本 mod 自己的源码**里抽出补丁面（130+ 个成员）：

- `[HarmonyPatch(typeof(X), nameof(X.Y))]` 与 `[HarmonyPatch(typeof(X), "Y")]`
- `AccessTools.Method/Field/Property(typeof(X), "Y")` ← 静默类
- `// Base-game source: X.Y` 注释标注的手抄原版方法 ← 静默类

再拿这份清单去 diff 两版语料，输出三级：

| 级别 | 含义 | 处理 |
|---|---|---|
| `BREAK` | 类型或成员没了（改名/删除） | 必修。标 `SILENT` 的是编译器抓不到的，优先看 |
| `SIGNATURE` | 声明变了（参数/返回类型） | 必看。手抄副本要同步改，反射查找要更新 |
| `BODY` | 签名没变、方法体变了 | **最危险的一类**，看 `--show-diff` 判断语义是否影响本 mod |

`SILENT` / `compiler-checked` 标签直接告诉你：这条不修的话，是构建时炸，还是玩家那里静默出错。
退出码：有任何 `SILENT` 命中返回 1（可当 gate 用）。

**实测有效性**（用适配前的历史提交回放验证）：

| 迁移 | 全包变更类型 | 本工具命中 | 当时真实断裂 |
|---|---|---|---|
| v0.107.0 → v0.107.1 | 1482 | **0** | 0 |
| v0.107.1 → v0.108.0 | — | 12（含全部 4 处） | `Hook.AfterTurnEnd` 改名、`ModifyDamage` 加参、`GetResultPileTypeForCardPlay` 改名、`MysticLighter.ModifyDamageAdditive` |
| v0.108.0 → v0.109.0 | 251 | **5**（含全部 4 处） | `SavedPropertiesTypeCache` 合并、`GetResultLocationForCardPlay` 改名、`OnPlayWrapper` 加 `IsDead` 短路、`BeforeFlush` |

其中 `GetResultPileType*ForCardPlay` 与 `SavedPropertiesTypeCache._netIdToPropertyNameMap`
是纯反射目标，**编译器永远不会报**；`OnPlayWrapper` 的 `CardLocation` /
`BranchingPlayerChoiceContext` 改动只在方法体里，也不会报。

## Phase 4 · 改（时间取决于命中数）

- **反射目标改名**：改名的同时**必须**同步 `MultiEnchantmentSupport.ValidateReflectionTargets()`
  的清单——那是运行时的第二道网，漏登记等于白做。
- **要不要留旧名回退**（`AccessTools.Method(新名) ?? AccessTools.Method(旧名)`）：只有在
  确实要同时支持两个游戏版本时才写。单版本发布就直接改，回退分支会在下次更新变成噪声
  （v0.109.0 适配时就删掉了 v0.108.0 留的那个回退）。
- **手抄的原版方法体**：拿 `--show-diff` 的 diff 逐段过，判断新增逻辑是否落在本 mod 改写的
  路径上。v0.109.0 那次 `OnPlayWrapper` 新增的"生物已死就停止后续 play"，对应到本 mod 就是
  给 `DispatchStackedHook` 加 `shouldStop`——签名没变，但不跟就是行为分叉。
- **改动涉及卡牌运行时状态/关键字时，先想联机**：lockstep + XxHash32 会把两端差异变成崩溃，
  见 `[[project-mp-checksum-keyword-desync]]`。

## Phase 5 · 编译门

```bash
dotnet build MultiEnchantmentMod.csproj -c Release   # 必须是 clean/全量
```

- ⚠️ **增量构建会说谎**：v0.108.0 那次第一遍只报了 1 个错，全量重建才暴露出全部 4 处。
  报错数明显偏少时，先 `rm -rf obj bin` 再来一遍。
- 编译通过 ≠ 适配完成。Phase 3 里 `SILENT` 的条目编译器不会提醒第二次。

## Phase 6 · 运行门

```bash
dotnet publish MultiEnchantmentMod.csproj   # build 不部署，publish 才拷进 mods/
```

启动游戏后看日志：

1. `ValidateReflectionTargets` 无 missing 报错。
2. `[VanillaCopyGuard]` 无 `Could not resolve` / `DRIFT`（`ExpectedHashes` 目前为空 = 只记录
   IL 哈希，不比对；Phase 3 的静态 diff 已覆盖同一盲区且不用启动游戏，这里当兜底看）。
3. `Intercepting…` 行存在 = mod 真的生效了。**没有这行先查 mod 是否在游戏设置里被禁用**，
   别把"跑的是原版行为"当 bug 调。
4. 本地构建可能被创意工坊副本盖掉——测试前退订/禁用工坊版本。

游戏内冒烟（覆盖本 mod 的主要面）：附魔一张卡 → 再附魔 → 存档退出重进（额外附魔要还在）
→ 打一场（触发 OnPlay / 回合钩子）→ 看牌库界面与历史记录 → 若改动涉及状态同步，开一局联机对拖。

## Phase 7 · 发版

走 `sts2-mod-release` skill。版本适配特有的两步别漏：

- `MultiEnchantmentMod.json`：`version` bump + `min_game_version` 提到新版（如果用了新版独有 API，
  这个构建就是**只能**在新版跑，必须提）。
- `.ci/dll-version.txt` 改成新版号 → 强制 add `.ci/gamedlls/{sts2,0Harmony}.dll` → 提交推送 →
  跑 `seed-cache.yml` → 绿了之后 `git rm --cached` 掉 DLL。缓存 key 是
  `hashFiles('.ci/dll-version.txt')`，不 reseed 的话 release workflow 会直接失败。

---

## 工具的已知盲区（别当成全覆盖）

`surface-diff.py` 抽的是**本 mod 显式提到的成员**，抓不到：

- 直接字段/属性访问的类型变化（如 v0.108.0 的 `Mod.assembly` → `Mod.assemblies`）——编译器会抓。
- 新增的钩子/新增内容（新卡、新遗物）带来的**机会**，不是断裂——看 Phase 2 的 churn 概览。
- 被 patch 方法的**调用方**变了（时序/调用次数变化）——只能靠冒烟测试和 diff 阅读。

维护约定：**新写一处手抄原版逻辑或字符串反射，就要留 `// Base-game source: 类型.成员` 注释**，
否则它不在追踪面里，下次更新静默失效。

## 成本预期

按已有三次迁移的实际数据：定位+语料+分诊 ≈ 10 分钟，改动 0.5–3 小时（取决于命中数），
冒烟 20 分钟，发版 15 分钟。约每两次游戏更新会有一次真的需要改代码。

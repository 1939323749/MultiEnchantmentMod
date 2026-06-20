# 遥测后端迁移方案：Supabase/PostgREST → PostHog

> 背景：2026-06-19，700+ 并发玩家把 Supabase（Free 套餐）打到 API 全 522。根因是
> `ref_*` 目录每会话全量重传（占 ~60% 请求）+ `SendRowsAsync` 逐行重试风暴。
> 决定彻底换后端到 PostHog。本文是执行蓝图，分「Mod 代码侧」和「PostHog 侧」两块。

---

## 0. 为什么 PostHog 能从根上解决

| 问题（Supabase/PostgREST） | PostHog 下的结果 |
|---|---|
| 每个写入占一个 Postgres 连接槽，700 并发打满连接池 → 522 | `/batch/` 摄入端为海量并发事件设计，无"连接槽"瓶颈，fire-and-forget |
| `ref_cards/ref_relics/ref_powers` 每会话全量上传（~60% 负载） | **整套概念删除**：事件里直接带 `card_id` 字符串，无需引用表、无需 upsert |
| upsert 失败 → 纯 INSERT → 逐行 700+ POST 的放大风暴 | 单条 `/batch/` 请求带多事件；失败直接丢弃，不放大 |
| 关系表 schema、迁移、RLS、on_conflict | schemaless，事件属性任意 JSON，无迁移负担 |

**核心收益**：`ref_*` 上传从"每会话全量"变成"根本不存在"。这是最大的一刀。

**主要代价**：PostHog Cloud 按**事件条数**计费（不是按请求）。批量不省钱，省的是请求数不是事件数。详见 §6，这是上线前必须先想清楚的一点。

---

## 1. 架构对照：表 → 事件

当前唯一与后端通信的代码是 `Telemetry/TelemetryReporter.cs`，`TelemetryCollector` 只负责构造匿名对象并调用 `SendCombat(...)` 等。这个接缝很干净，改造集中在一个文件。

| 现 Supabase 表 | PostHog 事件名 | 说明 |
|---|---|---|
| `telemetry_sessions` | `session_started` | 每会话一次。低频。 |
| `telemetry_environments`（按 hash 去重的大 JSONB） | `mod_environment` | **复用已落盘的 hash 门控**：仅 environment_hash 变化时发。罕见。 |
| `telemetry_mod_catalog`（按 hash 去重） | `mod_catalog` | 同上，catalog_hash 门控。罕见。 |
| `telemetry_combats` | `combat_completed` | 主事件流。PostHog 原生扛得住。见 §6 体量控制。 |
| `telemetry_runs` | `run_ended` | 低频高价值。保留。 |
| `telemetry_card_rewards` | `card_reward` | 中频。保留。 |
| `telemetry_crashes` | `mod_crash`（或 `$exception`） | 保留。 |
| `ref_cards` / `ref_relics` / `ref_powers` | **删除** | 不再由客户端上传。见 §3。 |

`session_id` / `run_id` / `installation_id` 等现有字段全部作为事件 `properties` 保留，分析时用它们做 filter/breakdown，等价于原来的外键关联。

---

## 2. PostHog 事件 payload 形态

单条（`POST {host}/capture/`）或批量（`POST {host}/batch/`，推荐）：

```jsonc
// POST https://us.i.posthog.com/batch/
{
  "api_key": "<project_api_key>",      // 写入专用 key，可安全内嵌客户端（同现 anon key 模型）
  "batch": [
    {
      "event": "combat_completed",
      "timestamp": "2026-06-19T12:34:56.789Z",
      "properties": {
        "distinct_id": "<installation_id GUID>",   // 等价于现在的 installation_id
        "$process_person_profiles": false,          // 匿名事件：不建 person、更便宜、更干净
        "session_id": "...",
        "run_id": "...",
        "combat_won": true,
        "character": "...",
        "total_enchant_applications": 12
        // …… 直接就是现在 SendCombat 里那个匿名对象的全部字段
      }
    }
    // …… 同一请求可带多条不同事件
  ]
}
```

- `distinct_id` = `installation_id`（已是随机 GUID，无 PII）。
- `$process_person_profiles: false` → **匿名事件**：不创建 person 档案，计费更低、隐私更干净。纯遥测不需要用户画像，强烈建议开。
- 现有字段命名（snake_case）原样作为属性名即可，PostHog 属性是任意 JSON。注意别用 `$` 前缀字段（PostHog 保留），现有字段无冲突。

---

## 3. `ref_*` 彻底删除（最大的一刀）

`ref_cards/relics/powers` 存在的唯一理由是：关系型遥测里要把卡牌元数据归一化进引用表，避免每条 combat 行重复存标题/描述。PostHog 不需要这个——事件里带 `card_id` 字符串就够，分析时用一张**静态映射**补全人类可读名。

执行：
- `TelemetryCollector` 里 `CollectCardCatalog` / `CollectRelicCatalog` / `CollectPowerCatalog` 三个方法及其调用**全部删除**（连同 `ComputeReferenceCatalogHash`、`SendRowsAsync` 整条路径）。省掉客户端每次启动的大量反射扫描 CPU，也省掉上传。
- 卡牌/遗物/能力的 id→标题 映射，改由**维护者每个游戏版本一次性**导出成静态 JSON（放仓库 / PostHog 的一个 definitions 事件 / 分析层字典），**不再由 700 个客户端各传一遍**。这才是"减少无用数据"的本质。
- 已落盘的 hash 门控缓存（`telemetry_catalog_cache.json`）保留用于 `mod_environment`/`mod_catalog` 这两个重事件的"仅变化时发"。

---

## 4. Mod 代码改造清单

> 现有改动（hash 缓存落盘、删掉 SendRowsAsync 风暴）先保留——若 PostHog 上线前还要发一版续命 Supabase 仍有用，且 hash 门控概念可平移。

1. **`TelemetryConfig`**：把 `SupabaseUrl`/`AnonKey`（来自 `TelemetrySecrets.g.cs` ← `.env.props`/CI）换成 `PostHogHost`（如 `https://us.i.posthog.com`）+ `PostHogProjectKey`。同步改 `.env.props.template`、`MultiEnchantmentMod.csproj` 的密钥注入、`.github/workflows/release.yml` 的 CI secret。
2. **`TelemetryReporter`** 重写为 PostHog 摄入：
   - 删 PostgREST 那套（url 拼 `/rest/v1/{table}`、`apikey`/`Authorization` header、`Prefer: resolution=merge-duplicates`、`on_conflict`、`SendRowsAsync` 全删）。
   - 新增 `CaptureAsync(string eventName, object properties)`：包成 `{event, timestamp, properties:{distinct_id, $process_person_profiles:false, ...properties}}`，POST `{host}/batch/`。
   - 现有 `SendSession/SendCombat/SendRun/SendCrash/SendCardReward(object data)` 改为映射到对应事件名调用 `CaptureAsync`。realtime 队列可顺带**攒批**：把短时间内的事件合进一个 `/batch/` 请求，进一步降请求数。
   - `SendStartupDataAsync` 去掉 `refCards/refRelics/refPowers` 参数，只留 session/environment/catalog 三个 hash 门控事件。
3. **`TelemetryCollector`**：删 §3 列的目录收集；`SendStartupDataAndUpdateCacheAsync` 去掉 ref_* 分支；`StartupUploadResult` 去掉 `RefCards/RefRelics/RefPowersUploaded`。
4. **骨架**（示意，非最终）：
   ```csharp
   private static async Task<bool> CaptureAsync(string eventName, object properties)
   {
       if (!TelemetryConfig.IsEnabled) return false;
       var evt = new Dictionary<string, object?>(/* from properties */) {
           ["distinct_id"] = TelemetryConfig.InstallationId,
           ["$process_person_profiles"] = false,
       };
       var payload = new { api_key = TelemetryConfig.PostHogProjectKey,
                           batch = new[] { new { @event = eventName,
                                                 timestamp = DateTimeOffset.UtcNow,
                                                 properties = evt } } };
       // POST {PostHogHost}/batch/  —— 200 即成功，失败直接丢，不重试放大
   }
   ```

---

## 5. PostHog 侧设置（在带 connector 的会话执行）

1. 选 **region**：US（`https://us.i.posthog.com`）或 EU（`https://eu.i.posthog.com`）。玩家全球分布选 US 一般延迟可接受；有欧盟合规诉求选 EU。
2. 选 **Cloud vs 自托管**：Cloud 免运维但按量计费；自托管（Docker + ClickHouse）零边际成本但要自己扛运维。给 mod 遥测**建议 Cloud + §6 体量控制**。
3. 创建 project，拿 **Project API Key**（写入专用，可内嵌客户端）。
4. 项目设置里确认**匿名事件**策略，配合客户端 `$process_person_profiles:false`。
5. （可选）建几个 insight/dashboard：附魔类型分布、combo 计数、胜率 by character、card_reward 选取率——等价于原来对 Supabase 表跑的分析。
6. 核对**当前定价与端点**（我的知识截止 2026-01，务必现场确认）。

---

## 6. 成本与体量控制（上线前必须决定）

PostHog Cloud 按事件条数计费（典型：每月前 100 万事件免费，超出按档计价，匿名事件更便宜——**以官网当前价为准**）。删掉 ref_* 后，主体量来自 `combat_completed`。粗估：几千 run/天 × 每 run 15–50 combat ≈ 每月数百万事件，**很可能超免费额度**。这是相对 Supabase Free（$0）的新变量，先想清楚。

控制杠杆（按需组合）：
- **匿名事件**（`$process_person_profiles:false`）——默认就开，最省。
- **按领域过滤**：本 mod 关心的是附魔。可只在 `total_enchant_applications > 0` 时发 `combat_completed`，无附魔活动的战斗信号弱、可不发——直接砍掉大头且符合"减少无用数据"。
- **采样**：对 combat 事件按 installation_id 哈希取 X% 上报。
- 保留 `run_ended`/`card_reward`/`session_started`（低频高价值），重点只压 combat。

---

## 7. 割接与 Supabase 退役

- **硬割接**：下一版 mod 直接全量改发 PostHog（不做双写——双写要同时维护两套且 Supabase 正在着火）。
- **旧客户端**：在新版铺开前，旧客户端会继续往 Supabase 发并持续 522。这是无害的（遥测永不崩游戏），但会一直压着 Supabase。
- **Supabase 处置**：既然要弃用，最干净是上线 PostHog 版后**暂停（pause）Supabase 项目**——暂停后连接被立即拒绝，旧客户端快速失败（比 30s 超时还更友好），你也不用再操心它。
- **历史数据（可选）**：若想留存旧分析数据，需要先有一个能连上的窗口导出（`pg_dump`/CSV）。现在连不上——要么临时 `REVOKE INSERT ON ref_* FROM anon` 甩负载腾出窗口，要么临时升 Pro，导完再 pause。不在乎历史就直接 pause。

---

## 8. 执行 checklist

PostHog 侧（connector 会话）：
- [ ] 选 region / Cloud；创建 project；拿 Project API Key
- [ ] 确认匿名事件 + 定价档位；定好体量控制策略（§6）
- [ ] （可选）建 dashboard/insight

Mod 代码侧：
- [ ] `TelemetryConfig` + `.env.props`/CI secret 换成 PostHog host+key
- [ ] 重写 `TelemetryReporter` 为 `/batch/` 摄入；删 PostgREST 路径
- [ ] 删 `TelemetryCollector` 的 `ref_*` 收集与上传；裁 startup 路径
- [ ] 决定并实现 combat 事件过滤/采样
- [ ] 本地验证发件成功（PostHog Activity 里能看到事件）
- [ ] bump 版本 + publish（走 CI 发布流程）

Supabase 退役：
- [ ]（可选）导出历史数据
- [ ] PostHog 版铺开后 pause Supabase 项目
```

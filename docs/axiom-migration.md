# 遥测事件写入迁移：PostHog → Axiom

> 背景：继 Supabase → PostHog（见 `posthog-migration-plan.md`）之后，事件写入后端再次切换到
> [Axiom](https://axiom.co)。接缝依旧是 `Telemetry/TelemetryReporter.cs` 的 `CaptureAsync`
> 一个方法——`TelemetryCollector` 只构造匿名对象并调用 `SendCombat(...)` 等，无需改动。

## 1. 端点与鉴权

Axiom REST ingest（US 区；EU 区把域名换成 `https://api.eu.axiom.co`）：

```
POST {AxiomDomain}/v1/datasets/{AxiomDataset}/ingest
Authorization: Bearer {AxiomToken}
Content-Type: application/json
```

- `AxiomDomain` 默认 `https://api.axiom.co`
- `AxiomDataset` 目标数据集名（默认 `multienchantmentmod`，需先在 Axiom UI 建好）
- `AxiomToken` 带该数据集 ingest 权限的 API token（`xaat-...`，可安全内嵌客户端）

> Axiom 也提供 edge ingest 形态 `https://{region}.aws.edge.axiom.co/v1/ingest/{dataset}`；
> 两种形态等价，本 mod 用上面的 REST 形态。**已用真 token 实测**两种形态均返回
> `HTTP 200 {"ingested":1,"failed":0}`，事件确认落库（验证脚本 `scripts/axiom-smoke-test.sh`）。

三者在构建期由 `MultiEnchantmentMod.csproj` 的 `GenerateTelemetrySecrets` target 生成进
`TelemetrySecrets.g.cs`，来源是本地 `.env.props` 或 CI 的 `-p:` 覆盖。见 `.env.props.template`。

## 2. 事件 payload 形态

与 PostHog 的「`properties` 嵌套袋」不同，Axiom 采用**扁平一行**（APL 查询最顺手）。
ingest body 是 JSON 数组，每个元素一行事件：

```jsonc
[
  {
    "_time": "2026-06-21T12:34:56.789Z",  // Axiom 自动识别为事件时间戳；缺省则用接收时间
    "event": "combat_completed",           // 事件类型判别字段（原 PostHog 的 event 名）
    "distinct_id": "<installation_id GUID>",
    "session_id": "...",
    "run_id": "...",
    "total_enchant_applications": 12
    // …… SendCombat 里那个匿名对象的全部字段，snake_case 原样平铺到顶层
  }
]
```

- 数据对象字段在 `JsonSerializer.SerializeToNode(data, JsonOptions)` 时已转 snake_case；
  随后注入的 `_time`/`event`/`distinct_id` 为字面键名，命名策略不会二次改写已建好的 `JsonNode`。
- 去掉了 PostHog 专属的 `$process_person_profiles`（Axiom 无 person 概念）。
- fire-and-forget 语义不变：状态码 `<400` 即成功，失败直接丢弃、绝不重试放大。

## 3. 仍需在 Axiom 侧完成的 provisioning（非代码）

代码已就绪并编译通过，但实际上报前还需：

1. 在 Axiom 建一个 dataset（名字与 `AxiomDataset` 一致，默认 `multienchantmentmod`）。
2. 建一个 **API token**（不是 PAT），授予该 dataset 的 ingest 权限。
3. CI：在 GitHub `nexusmods` 环境设置 secrets `AXIOM_TOKEN`（必填）、可选 `AXIOM_DATASET`、
   `AXIOM_DOMAIN`（缺省走默认值）。`release.yml` 的 build job 已读取这三个。
4. 本地验证：填好 `.env.props` 后构建运行，到 Axiom 的 dataset Stream 视图确认事件落库。

## 4. 体量控制

与 PostHog 阶段一致的策略仍适用（匿名上报、按 `total_enchant_applications > 0` 过滤、
按 `installation_id` 哈希采样）。Axiom 按 ingest 字节量计费，扁平行比嵌套略省。

# 观测与日志系统设计（Observability & Logging）

> **文档性质**：已落地的 L0/L1 规格（随 CLI 实现维护）
> **实现位置**：`native/src/bin/pi_unity/logging.rs` + `native/src/bin/pi_unity/main.rs`（不进入 Unity `cdylib`）
> **目标**：为「分析运行时日志 → 优化工具 → 持续进化」建立数据基础
> **范围**：本阶段只做 L0（采集）+ L1（存储）；L2 聚合分析、L3 进化闭环另行立项
> **硬约束**：纯客户端改动，零协议变更，不动 broker / Unity 包

## 一、现状盘点

- **已有**：broker 侧 Action Timeline（`<UnityProject>/Temp/PiUnityHarness/ActionTimeline/*.jsonl`，5MB 轮转，含 `requestType / durationMs / success / errorType`，脱敏有单元测试保障）。**保持不动**，本设计与其通过 `requestId` 关联。
- **缺口**：客户端身份缺失；CLI 侧过程数据（发现/连接耗时、退出码、截断、重连）不落盘；`Temp/` 易失且按项目分散，无法跨项目纵向分析；无 session / skill 维度。

## 二、设计原则

1. **统一用户级存储**：所有新日志只有一个家 `~/.pi-unity/`。关键理由：bridge 发现失败（exit 2）时没有项目根可写，而这类失败恰是最该分析的；跨项目分析免汇聚。
2. **CLI 收口**：CLI 是唯一外部入口（pi 扩展已薄封装走 CLI），在 CLI 打点 = 全 Agent 覆盖。
3. **健康零成本**：常态每次调用只写一行（~200B）；详细过程日志仅失败或显式开启时落盘。
4. **best-effort**：日志写失败绝不影响 CLI 主流程与退出码。
5. **隐私红线**：不落 code、参数值、token、错误消息原文（只记 `errorType` 类别）；项目路径只记 `projectHash`（SHA256 前 6 字节 hex，与 pipe 名同思路）。

## 三、L1 存储布局

```text
~/.pi-unity/                      # %USERPROFILE%\.pi-unity；PI_UNITY_LOG_DIR 可覆盖
├── logs/
│   ├── events-YYYY-MM.jsonl      # 统一事件流（call / session / skill 事件）
│   └── traces/YYYY-MM-DD/        # 详细过程日志（仅失败或 --trace）
│       └── <ts>-<pid>-<subcommand>.log
└── sessions/
    └── current.json              # 粘性会话注册表 {sessionId, agent?, task?, startedAtMs}
```

- **轮转**：events 按月分文件，单文件超 50MB 加序号滚动；traces 保留 7 天，CLI 启动时惰性清理。
- **并发**：多 CLI 进程对同一 events 文件「单行追加」——`OpenOptions.append` + 单次 `write_all` 整行，OS 保证落尾不覆盖（与 broker ActionTimeline 同款模式）。极端撕裂行由分析端 skip 容错，不加锁。

## 四、L0 采集

### 4.1 调用事件（always-on，每次调用一行）

```json
{"v":1,"kind":"call","tsUtc":"2026-09-02T03:00:00Z","version":"0.1.0","client":"cli|pi-ext","pid":1234,
 "sessionId":"…|null","hostSessionId":"…|null","subcommand":"compile","projectHash":"3f9a…|null",
 "exitCode":0,"durationMs":81234,
 "phases":{"discoverMs":80,"connectMs":12,"requestMs":81000},
 "flags":{"json":false,"truncated":false,"reconnects":0},
 "errorType":"…|null","requestIds":["cli-…"],"traceId":"…|null"}
```

要点：**发现失败（exit 2、无 projectHash）也必须落行**——统一全局存储的独特价值。

### 4.2 过程 trace（按需）

- 步骤事件只写内存 ring buffer（~50 条），调用成功即丢弃。
- **失败自动落盘**（exit != 0）；`--trace` 全局参数或 `PI_UNITY_TRACE=1` 时成功也落盘。
- 内容：发现链每步命中/未中、bridge.json 加载、pipe 连接耗时、帧发送、断连/重连、compile 每轮 poll、截断事件、最终退出码（eval code 等敏感内容只记长度/hash）。
- 摘要行 `traceId` ↔ trace 文件名互相关联；`requestIds` 是与 broker ActionTimeline 的 join key。

### 4.3 会话追踪

- 新增子命令：`pi-unity session start [--task "..."]`（铸 sessionId 写 `sessions/current.json`）、`pi-unity session end`（关闭）；TTL 12h 自动失效。
- 每行日志的 sessionId 解析优先级：① env `PI_UNITY_SESSION_ID`（宿主注入，可为宿主原生 id）→ ② 粘性注册表 → ③ `null`。
- 语义：sessionId 是**本侧的任务级分组键**；宿主原生 session id 存 `hostSessionId` 做映射，不依赖。

### 4.4 skill 事件与归因

- 新增轻命令：`pi-unity mark --skill <name> --event used`，只写一条 `{"kind":"skill.used",…}` 事件。
- 每条 `skills/pi-unity-*/SKILL.md` 末尾有约定一句：「使用本 skill 时先运行 `pi-unity mark --skill <name> --event used`」。这是软信号，agent 不会每次都跑，**不能当真实用量**。
- client 归因：pi 扩展 `runPiUnityCli` 注入 env `PI_UNITY_CLIENT=pi-ext`；herdr 启动 agent 时可注入 `PI_UNITY_AGENT` / `PI_UNITY_SESSION_ID`。
- 粘性 session 是用户级单文件 `sessions/current.json`：多 agent 并行会互相覆盖，只适合单任务分组，不是多 agent 锁。
- `session start` 必须把注册表写成功才算成功；events 行仍是 best-effort。
- 说明：skill 的「加载」（agent 读文件）无法从外部硬观测，靠 mark 软约定 + 命令序列行为推断（分析期做）。

## 五、明确不做（本阶段）

- L2 `pi-unity analyze` 聚合命令、L3 AHE 进化闭环——数据先积累，分析后做。
- broker 请求帧加 `client` 字段（CLI 日志已覆盖归因，保持零协议变更）。
- 宿主 transcript 导入器、宿主原生 hooks（除扩展一行 env 注入外）。

## 六、验收标准

1. 无 Unity 环境执行任意命令（含发现失败 exit 2），events 文件都有对应行且字段完整。
2. 日志目录设为只读后，CLI 主流程行为与退出码完全不变（best-effort 验证）。
3. 两个进程并发各写 100 行，events 无撕裂行（每行均可 JSON 解析）。
4. `session start` → 若干调用 → `session end`：调用行正确携带 sessionId；关闭或 TTL 后回到 null。
5. 失败调用生成 traces 文件且摘要行 `traceId` 匹配；成功调用无 traces 文件；`--trace` 时成功也生成。
6. `mark` 命令落 `skill.used` 事件；经 pi 扩展发起的调用行 `client = "pi-ext"`。
7. `cargo test` 通过；logging 模块有纯单元测试（轮转命名、脱敏、行格式、session 解析优先级）。

## 七、实现对照

- `native/src/bin/pi_unity/logging.rs`：路径、轮转、session、mark、call 事件、trace recorder。
- `native/src/bin/pi_unity/main.rs`：`main` / `handle_exit` 统一落行（含发现失败）；`--trace` 全局参数。
- `.pi/extensions/pi-unity-harness/index.ts`：`runPiUnityCli` 的 env 加 `PI_UNITY_CLIENT=pi-ext`。
- `skills/pi-unity-*/SKILL.md`：每条末尾有 mark 约定（软信号）。
- `README.md` / `README.en.md`：可观测性小节。

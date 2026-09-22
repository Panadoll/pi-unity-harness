# pi-unity-harness 架构加固 Spec

> 状态：Implemented v1
> 实施提交：S1 `fc6f770`；S2 `0d7f8da`；S3 `3f44223`；S4 `b57f003`；S5 `b00d8f0`；S6 `0bb4cc8`；S7 `3c7c6ac`；S8 `43c63e8`；S9 `c8172c5`
> 适用范围：`native/`、`unity/com.pi.unity-harness`、`.pi/extensions/pi-unity-harness`、`docs/`
> 原则：不做大重写；所有 P0 项不改变外部可观察行为（CLI 输出、exit code、pipe 帧格式向后兼容）。

## 0. 目的与非目标

**目的**：把目前以隐式约定存在的三样东西——跨层协议、生命周期状态、模块职责边界——升格为有测试守护的一等概念。

**非目标**（本 spec 明确不做）：

- 多客户端写入 / 多 Agent 共享一个 Editor 的控制面。
- 重组 `com.pi.pipeline.compat` 目录结构。
- 为 Pipeline 命令引入 `version` / `idempotency` / `timeoutClass` / `confirmation` 元数据。
- 用 JSON Schema 代码生成替换各层手写序列化。
- 停止在 git 中跟踪 `pi_unity_harness_native.dll`。

## 1. 现状基线（事实，作为验收对照）

| 项 | 现状 |
| --- | --- |
| 请求帧 | `{ id, type, token, timeoutMs?, payload }`（`client.rs:340-346`） |
| 响应帧 | `{ reply_to, ok, result }` 或 `{ reply_to, ok:false, error, error_type? }`（`imp.rs:787-806`，`PiUnityJsonHelper.cs:14-36`） |
| mux 回复 | `{ id, ok, result?, error?, error_type?, exitCode?, help?, truncated?, savedScratchPath?, text? }`（`index.ts:499-519`） |
| 协议版本 | Rust `NATIVE_PROTOCOL_VERSION = 1`（`native/src/lib.rs:5`），C# `NativeProtocolVersion = 1`（`PiUnityBridge.cs:21`）；CLI 握手不等即断开（`client.rs:194-238`） |
| managed 状态 | `initializing/ready/reloading/quitting`（`lib.rs:1-4`，`PiUnityBridge.cs:22-25`） |
| editorStatus | 分号串 `editing;focus=…;window=…[;mainThreadStale=1]`（`PiUnityBridge.cs:134`，`imp.rs:1885-1895`） |
| 心跳 | `HEARTBEAT_TIMEOUT_MS = 5000`；超时给 in-flight 发 `managed_heartbeat_timeout` |
| Broker 错误码 | `invalid_json, missing_id_or_type, unauthorized, request_too_large, request_timeout_before_dispatch, request_timeout_in_flight, managed_heartbeat_timeout, client_disconnected, managed_not_ready, managed_reloading, managed_quitting` |
| Managed error_type | `usage, compile_error, runtime_error, diagnostic, busy, cancelled, command_error, compilation_failed, playmode_exit_timeout` |
| CLI 合成 error_type | `execution_failed, bridge_not_found, timeout, usage, other, busy`；TS 另有 `mux_crash` |
| Session 存储 | `~/.pi-unity/sessions/current.json`，字段 `sessionId, agent?, task?, startedAtMs`，全用户单文件（`logging.rs:199-209`） |
| projectHash | `compute_project_hash`：小写+去尾斜杠路径的 SHA-256 前 6 字节 hex（`logging.rs:162-174`），已用于 pipe 名 |
| DLL 构建 | `scripts/build-native.ps1/.sh`：`cargo build --release` 后复制；无 `build.rs`，无嵌入 git rev；Cargo 与 package.json 均为 `0.1.0` |
| 命令目录 | `command_list`：每命令 `name, description, mainThreadRequired, runtimeOnly, schema(draft-07 + x-command-metadata), parameters`；TS 按类型名子串推断 TypeBox 类型（`helpers.ts:59-66`） |
| 跨域重载 | `PiUnityBridge.OnAfterReload` 硬编码调用 `PiUnityCompileCoordinator.ResumeAfterReload(CompleteCompileResult)` 与 `PiUnityTestCoordinator.ResumeAfterReload(CompleteJson)`（`PiUnityBridge.cs:211-227`） |
| 测试入口 | Rust `cd native && cargo test`；TS `cd .pi/extensions/pi-unity-harness && npm test`；C# Unity Test Runner 过滤 `Pi.UnityHarness` |

## 2. 交付项总览

| ID | 名称 | 优先级 | 依赖 | 是否改变外部行为 |
| --- | --- | --- | --- | --- |
| S1 | 协议 golden fixtures + 三层 Contract Test | P0 | — | 否 |
| S2 | 生命周期与请求投递状态机 | P0 | S1 | 否（只新增字段） |
| S3 | Session 按 projectHash + agentId 隔离 | P0 | — | 否（保留兼容读路径） |
| S4 | Native DLL / protocol / package 版本一致性 | P0 | — | 否（只新增字段） |
| S5 | Pipeline 命令安全元数据（mutability / thread / runtime） | P0 | S1 | 否（只新增字段） |
| S6 | 文档：threat model、compat 上游边界 | P0 | — | 否 |
| S7 | `IReloadAwareOperation` 抽取 | P1 | S2 | 否 |
| S8 | `imp.rs` 叶子模块拆分 | P1 | S2 | 否 |
| S9 | MuxClient 拆分 transport / queue / restart policy | P1 | S1 | 否 |
| S10 | State plane 正式化为只读通道 | P2 | S2, S4 | 是（新增只读客户端能力） |

---

## S1. 协议 golden fixtures 与 Contract Test

### 目标

同一批 fixture 文件被 Rust、C#、TypeScript 三侧测试共同消费，任何一层对 envelope 字段的语义变更都必须先改 fixture。

### 权威定义

`docs/protocol.md` 继续作为人读的权威文档；`protocol/fixtures/` 作为机器可验证的权威样本。两者不一致视为 bug。

### 目录与文件格式

```text
protocol/
  README.md                  # 字段规则（见下）、fixture 格式说明、如何新增
  fixtures/
    request/                 # 客户端 -> broker 的 pipe 请求帧
    response/                # broker -> 客户端的 pipe 响应帧（含 managed 透传）
    status/                  # status / bridge_capabilities 结果
    mux/                     # mux stdout 行（CLI 合成 envelope）
    normalized/              # 各层归一化后的期望值（见下）
```

`response/` 与 `mux/` 是两个不同的 wire surface，fixture 不得混用：pipe 响应帧的关联字段固定为 `reply_to`，mux 行的关联字段固定为 `id`。`id` 不是 pipe 响应帧的别名，`reply_to` 也不是 mux 行的别名。

单个 fixture：

```json
{
  "description": "managed 侧编译错误，error_type 优先于 broker error",
  "direction": "response",
  "wire": { "reply_to": "r1", "ok": false, "error": "CS1002: ; expected", "error_type": "compile_error" },
  "expect": { "ok": false, "errorType": "compile_error", "errorCode": null, "message": "CS1002: ; expected", "exitCode": 1 }
}
```

pipe 响应 fixture 的 `direction` 必须是 `response`，并且只能用 `wire.reply_to` 关联请求；mux fixture 单独放在 `mux/`，例如：

```json
{
  "description": "mux 成功行，CLI 合成字段不属于 pipe 响应",
  "direction": "mux",
  "wire": { "id": "job-1", "ok": true, "result": null, "text": "done" },
  "expect": { "ok": true, "errorType": null, "errorCode": null, "message": null, "exitCode": 0 }
}
```

mux fixture 只能用 `wire.id` 关联 job；`wire.reply_to` 出现在 mux 行或 `wire.id` 出现在 pipe 响应中都属于 fixture 错误。

`expect` 是 **归一化结果模型**，三层都必须能从 `wire` 推出同一个 `expect`：

```text
NormalizedResult {
  ok: bool
  errorType: string | null     // 有 error_type 时取之；否则由 errorCode 映射（映射表冻结自 client.rs:52-62）
  errorCode: string | null     // broker 级错误码（closed set，见 §1）
  message: string | null
  exitCode: int                // 冻结自 main.rs 现有 exit code 表
}
```

### 字段规则（写入 `protocol/README.md`，并回填 `docs/protocol.md` §3.2 / §5）

1. **未知字段**：所有层 MUST 忽略未知字段，不得报错、不得写入日志以外的地方。
2. **缺失字段**：
   - 响应缺 `ok` → 视为 `ok:false`，`errorCode = "malformed_response"`。
   - pipe 响应缺 `reply_to` → 丢弃并记录，不得匹配到任何 in-flight 请求；pipe 响应不得用 `id` 替代。
   - mux 行缺 `id` → 丢弃并记录，不得匹配到任何 queued/in-flight job；mux 行不得用 `reply_to` 替代。
   - `ok:true` 但缺 `result` → `result = null`，不视为错误；该规则同样适用于 mux 的成功 envelope。
3. **错误字段优先级**：`error_type` > `errorCode`（broker `error` 字段属 closed set 时）> `execution_failed`。pipe 与 mux 仅在关联字段命名上不同，错误归一化规则相同。
4. **closed set**：Broker 错误码与 managed `error_type` 都是 closed set；新增值必须同时新增 fixture 与 `docs/protocol.md` 条目。
5. **mux 合成字段**：`exitCode / help / truncated / savedScratchPath / text` 是 CLI 层合成字段，MUST 在 `docs/protocol.md` 增加 §3.5 "CLI 合成字段"，并明确它们不出现在 pipe 帧中。
6. **版本策略**：`protocolVersion` 不匹配 → 客户端 MUST 断开并给出 `error_type = "protocol_mismatch"`（新增值；当前是 `CliError::Other` 文本，需迁移）。这是本 spec 唯一新增的 error_type，需同步三层与 fixture。

### 必须覆盖的 fixture 集合（最小集）

| 目录 | fixture |
| --- | --- |
| request | `eval_minimal`、`eval_with_timeout`、`status`、`bridge_capabilities`、`missing_token`、`unknown_extra_field` |
| response | `ok_eval_with_timing`、`ok_result_null`、`err_compile_error`、`err_runtime_error`、`err_usage`、`err_cancelled_reload`、`err_managed_reloading`、`err_managed_not_ready`、`err_request_timeout_in_flight`、`err_request_timeout_before_dispatch`、`err_managed_heartbeat_timeout`、`err_client_disconnected`、`err_unauthorized`、`err_protocol_mismatch`、`missing_ok`、`missing_reply_to`、`unknown_extra_field` |
| status | `ready_idle`、`ready_modal_present`、`reloading`、`heartbeat_timed_out`、`capabilities_v1`、`capabilities_v2_mismatch` |
| mux | `ok_result`、`ok_text_only`、`ok_truncated_with_scratch`、`err_with_help`、`err_missing_id`、`crash_synthesized` |

### 三层测试落点

| 层 | 文件 | 加载方式 |
| --- | --- | --- |
| Rust | `native/tests/protocol_fixtures.rs` | `CARGO_MANIFEST_DIR/../protocol/fixtures`；response 类走 `client.rs` 的解析函数，request 类走 `imp.rs::handle_line`（`mock_pipe.rs` 已有基础） |
| C# | `unity/com.pi.unity-harness/Tests/Editor/Protocol/ProtocolFixtureTests.cs` | 从 `PackageInfo.FindForAssembly` 的 `resolvedPath` 向上查找 `protocol/` 目录；找不到（包从 registry 安装）则 `Assert.Ignore` |
| TS | `.pi/extensions/pi-unity-harness/protocol.test.ts` | 相对路径 `../../../protocol/fixtures`；加入 `package.json` `test` 脚本 |

C# 侧只测它实际产出/消费的方向：`PiUnityJsonHelper` 的响应序列化输出 MUST 与 `response/*` 的 `wire` 字节级等价（忽略键序与空白）。

### 验收标准

- [ ] 三层测试都读取同一目录，删除任一 fixture 三层至少各有一个测试失败。
- [ ] `docs/protocol.md` 新增 §3.5，并在 §5 列出完整 closed set。
- [ ] `protocol_mismatch` error_type 在 Rust CLI、TS、`docs/protocol.md` 同步落地；它是本 spec 唯一新增的 `error_type`。pipe fixture 使用 `reply_to`，mux fixture 使用 `id`，不得以另一 surface 的字段满足测试。
- [ ] CI（`.github/`）新增 job 依次跑 `cargo test`、`npm test`；C# 测试保持手动/Unity CI 触发。

---

## S2. 生命周期与请求投递状态机

### 目标

用三个正交维度描述 Bridge 健康度，替代把语义挤进 `managedState + editorStatus + heartbeatTimedOut` 的做法；同时把请求投递生命周期定义为显式状态机。对外新增一个 `lifecycle` 对象，旧字段全部保留。

### 状态定义

```text
managed   := initializing | ready | reloading | quitting          # 来源：C# 通过 pi_unity_set_managed_state
transport := disconnected | connected                              # 来源：Broker pipe 客户端连接
mainThread:= responsive | stale | blocked_modal | unknown          # 来源：Broker 派生
```

`mainThread` 派生规则（在 `imp.rs` 状态快照构建处集中实现，唯一来源）：

| 条件（按顺序判定） | mainThread | reason |
| --- | --- | --- |
| `modalObservation.present` | `blocked_modal` | `modal_dialog` |
| `managed == ready && heartbeatAge > HEARTBEAT_TIMEOUT_MS` | `stale` | `heartbeat_timeout` |
| `managed == ready` | `responsive` | `null` |
| 其它 | `unknown` | `managed_<state>` |

status 结果新增字段（旧字段不动）：

```json
"lifecycle": {
  "managed": "ready",
  "transport": "connected",
  "mainThread": "blocked_modal",
  "reason": "modal_dialog",
  "generation": 3
}
```

`editorStatus` 中的 `mainThreadStale=1` 保留，但标注为 deprecated，推荐消费者改读 `lifecycle.mainThread`。

### 请求投递状态机

```text
queued ──poll──▶ in_flight ──complete──▶ completed
  │                  │
  │ timeout          │ timeout        ──▶ request_timeout_in_flight
  ▼                  │ heartbeat lost ──▶ managed_heartbeat_timeout
request_timeout_     │ reload         ──▶ cancelled（managed 侧 error_type）
before_dispatch      │ pipe closed    ──▶ client_disconnected（仅审计，无响应可发）
  │
  │ managed != ready 时入队 ──▶ managed_not_ready | managed_reloading | managed_quitting（立即拒绝）
```

规则：

1. 每个终态对应且仅对应一个 broker 错误码或 managed `error_type`，表格进入 `docs/protocol.md` §5。
2. `reloading` 期间 broker MUST 立即拒绝新 managed 请求（现状），MUST NOT 让请求在 `pending` 中跨越 reload；`compile` 与 `run_tests` 的跨 reload 恢复属于 managed 侧 `SessionState` 机制，不经过 broker 队列（现状，需在文档中明确）。
3. `in_flight` 请求在 reload 开始时由 C# 侧统一以 `cancelled` 完成（现状 `CancelAllPendingAsyncEvals`），broker 不得再对其超时二次回包。

### 落点

- Rust：`imp.rs` 新增 `LifecycleSnapshot` 结构与 `derive_lifecycle()`，`status` / state plane / `bridge_capabilities` 三处统一调用；`#[cfg(test)]` 覆盖上表每一行。
- TS：`index.ts` 读取 `lifecycle` 优先，缺失时回退旧字段（兼容旧 DLL）。
- 文档：`docs/protocol.md` §6.1 增加 `lifecycle`，新增 §5.4 "请求投递状态机"。
- fixture：S1 的 `status/*` 全部补上 `lifecycle` 字段。

### 验收标准

- [ ] 任一 status 输出中 `lifecycle.mainThread` 与 `heartbeatTimedOut` / `modalObservation.present` 不矛盾（fixture 断言）。
- [ ] 旧版 TS 扩展对新 DLL 的 status 输出解析不报错（未知字段忽略）。
- [ ] 请求投递每个终态在 `imp.rs` 单测中各有至少一个用例。

---

## S3. Session 按 projectHash + agentId 隔离

### 目标

多 Agent / 多项目并行时 sticky session 不互相覆盖。

### 设计

```text
~/.pi-unity/sessions/
  current.json                       # 兼容：只写不读（见规则 3）
  <projectHash>/
    <agentId>.json                   # 实际存储
```

- `projectHash`：复用 `compute_project_hash`；无法解析项目时使用 `_noproject`。
- `agentId`：`PI_UNITY_AGENT_ID` 环境变量；缺省 `default`。文件名做 `[A-Za-z0-9_-]` 白名单清洗。
- 文件内容与现有 `current.json` 一致：`{ sessionId, agent?, task?, startedAtMs }`，保留 12h TTL。
- 解析优先级：`PI_UNITY_SESSION_ID` env → `sessions/<hash>/<agentId>.json` → `null`。**不再回退读全局 `current.json`**。
- 规则 3：继续写 `current.json`，内容改为指针 `{ "pointer": "sessions/<hash>/<agentId>.json", "sessionId": "...", "startedAtMs": ... }`，供旧脚本读取；两个大版本后移除。
- 日志（`logs/events-YYYY-MM.jsonl`）本阶段保持全局；每条事件已含/必须含 `projectHash` 与 `agentId` 字段，供查询过滤。

### 落点

- `native/src/bin/pi_unity/logging.rs`：session 路径解析、写入、TTL 清理（旧目录下过期文件顺带清理）。
- `docs/`（README 与日志相关文档）：删除"parallel agents 会互相覆盖"限制说明，改为环境变量说明。

### 验收标准

- [ ] Rust 单测：两个不同 `PI_UNITY_AGENT_ID` 在同一项目下写入不同文件；两个不同项目路径在同一 agent 下写入不同文件。
- [ ] `current.json` 仍存在且为合法 JSON。
- [ ] 无 `PI_UNITY_AGENT_ID` 时行为与旧版单 agent 等价（`default` 槽位）。

---

## S4. Native DLL / protocol / package 版本一致性

### 目标

任何时刻都能回答"当前加载的 DLL 是哪份源码构建的、协议版本几、和 Unity 包版本是否配套"，且 CI 能机械校验。

### 设计

**嵌入构建信息**（`native/build.rs`，新增）：

当前 DLL 不含任何可供 CI 扫描的嵌入构建信息；本节新增的是一个稳定的字节标记，而不是可复现构建或二进制 hash 契约。

- 生成 `PI_UNITY_GIT_REV`（`git rev-parse --short HEAD`，失败则 `unknown`）、`PI_UNITY_GIT_DIRTY`（`git status --porcelain` 非空）、`PI_UNITY_BUILD_UTC`。
- `lib.rs` 暴露 `pub const BUILD_INFO_JSON: &str`，并以固定前缀写入一个 `#[used] static` 字节串：`PIUH_BUILD_INFO:{"crate":"0.1.0","gitRev":"abc1234","dirty":false,"protocol":1,"builtUtc":"..."}`，便于 CI 直接扫描 DLL 字节。
- `PIUH_BUILD_INFO:` 是 CI 的字节扫描锚点；CI MUST 校验标记后的 JSON 字段，不得比较 DLL hash 或要求构建可复现。

**运行时暴露**：

- `bridge_capabilities` 与 `status` 结果新增 `native: { crateVersion, gitRev, dirty, protocolVersion }`。
- 新增 FFI `pi_unity_build_info(buffer, len, &required) -> i32`，语义同 `pi_unity_poll_request` 的缓冲协议；C# `PiUnityBridge.Native.cs` 增加对应 DllImport。
- C# 在 `pi_unity_init` 成功后读取 build info，与 `PackageInfo.FindForAssembly(typeof(PiUnityBridge).Assembly).version` 比较 `crateVersion`：不一致 → `Debug.LogWarning` 一次，并在 `editorStatus` 追加 `nativeVersionMismatch=1`；不阻断启动。
- `pi-unity status --full` 与 `pi-unity version --json` 输出 `native` 对象（CLI 自身的 `CARGO_PKG_VERSION` 与 DLL 的 `crateVersion` 分列）。

**CI 校验**（`scripts/check-versions.ps1`，新增；`.github/` 调用）：

1. `native/Cargo.toml` version == `unity/com.pi.unity-harness/package.json` version，否则失败。
2. 扫描已跟踪 DLL 中 `PIUH_BUILD_INFO:` 后的 JSON：`crate` 必须等于 Cargo 版本；`protocol` 必须等于 `NATIVE_PROTOCOL_VERSION`（从 `lib.rs` 正则提取）。
3. 若 PR diff 触碰 `native/src/**` 或 `native/Cargo.*` 但未触碰 DLL → 失败并提示运行 `scripts/build-native.ps1`。
4. 反向（只改 DLL 不改源码）→ 警告，不失败。

不做二进制 hash 比对（构建不可复现，会产生噪音）；`PIUH_BUILD_INFO:` 的存在与内容校验取代 hash 比对。

### 验收标准

- [ ] `pi-unity version --json` 能区分 CLI 版本与 DLL 版本。
- [ ] 人为改 `Cargo.toml` 版本不重建 DLL 时，`check-versions.ps1` 失败。
- [ ] C# 在 DLL 版本不匹配时 Editor Console 出现一次警告，Bridge 仍可用。
- [ ] `docs/protocol.md` §10 写明 `native` 对象与 `nativeVersionMismatch` 语义。

---

## S5. Pipeline 命令安全元数据

### 目标

Agent 在调用前能从命令目录得知"会不会改场景/资产、是否需要主线程、是否仅运行时可用"，而不是依赖文档自觉或名称猜测。

### 元数据定义

```json
{
  "name": "scene_get_data",
  "policy": {
    "mutability": "read",          // read | write | destructive
    "thread": "main",              // main | any        （映射自现有 mainThreadRequired）
    "runtime": "editor",           // editor | runtime | both（映射自现有 runtimeOnly）
    "source": "attribute"          // attribute | sidecar | default
  }
}
```

- `mutability` 缺省 **`write`**（保守）；`destructive` 用于删除资产/场景对象、覆盖文件、清空集合等不可撤销操作。
- `thread` / `runtime` 直接由现有 `CommandInfo.MainThreadRequired` / `RuntimeOnly` 派生，不新增来源；compat fork 中已有 `CliCommandAttribute.MainThreadRequired` / `RuntimeOnly`，不得重复新增或修改。
- 现有顶层字段 `mainThreadRequired` / `runtimeOnly` 保留；`schema["x-command-metadata"]` 同步写入 `policy`。

### 来源与优先级（不修改 compat fork）

1. **attribute**：harness 包新增 `[PiCommandPolicy(Mutability = ...)]`（定义在 `com.pi.unity-harness`，非 compat），其唯一新增 policy 元数据是 `Mutability`，供 harness 自有命令使用。
2. **sidecar**：`unity/com.pi.unity-harness/Editor/Pipeline/command-policy.json`，`{ "<commandName>": { "mutability": "read" } }`，覆盖 compat/上游命令；文件随包发布。若同一 harness 命令同时有 attribute 与 sidecar，attribute 优先；sidecar 主要为 compat/上游命令补充 mutability。
3. **default**：均无 → `write`，`source = "default"`。thread/runtime 仍分别从现有 `MainThreadRequired` / `RuntimeOnly` 派生，不产生新的来源字段。

`PiUnityPipelineCommandExecutor` 在构建 `command_list` 时合并三层来源。sidecar 缺失或 JSON 非法 → 日志警告一次，按 default 处理。

### 类型推断收敛

- C# 侧在 `parameters[]` 每项新增 `jsonType: "boolean" | "integer" | "number" | "string" | "array" | "object"`，由 `System.Type` 精确映射（`bool→boolean`；`byte/sbyte/short/ushort/int/uint/long/ulong→integer`；`float/double/decimal→number`；`IEnumerable<T>`→array；其它→string）。
- TS `helpers.ts::parameterToTypeBox` 优先读 `jsonType`，仅在缺失时回退现有子串匹配。
- 对 TS 侧 typed tool 注册：`mutability != "read"` 的命令在 tool description 前缀 `[WRITE]` / `[DESTRUCTIVE]`；`destructive` 命令默认不注册为 typed tool，需通过配置 `PIPELINE_TOOL_ALLOW_DESTRUCTIVE` 显式开启（复用 `PIPELINE_TOOL_EXCLUDE` 所在配置机制）。

### 落点

- C#：`PiUnityPipelineCommandExecutor.cs`（合并 policy、`jsonType`）、新增 harness 侧 `PiCommandPolicyAttribute.cs`、`command-policy.json`、`PipelineCommandDiscoveryTests.cs` 补断言；不得修改 `unity/com.pi.pipeline.compat/` 中的 attribute 或命令实现。
- Rust：`schema.rs` 透传 `policy`，`pi-unity pipeline list` 表格新增 `mutability` 列。
- TS：`helpers.ts`、`index.ts` 注册逻辑；`helpers.test.ts` 补 `jsonType` 优先级用例。
- 文档：`docs/safety-and-mutations.md` 改为引用 `policy` 字段，并列出 sidecar 初始条目。
- fixture：S1 新增 `status/command_list_with_policy`。

### sidecar 初始条目要求

首批至少标注所有名称含 `delete` / `remove` / `clear` / `destroy` / `overwrite` 的命令为 `destructive`，所有 `get_` / `list_` / `find_` / `query_` 前缀命令为 `read`；其余保持 default。标注结果在 PR 中以表格形式列出供审阅。

### 验收标准

- [ ] `command_list` 每条命令都有 `policy` 且 `source` 正确。
- [ ] `PipelineCommandDiscoveryTests` 断言：无任何来源时 `mutability == "write"`；sidecar 可覆盖 attribute 之外的命令；attribute 优先于 sidecar。
- [ ] TS 在 `jsonType` 存在时不走子串匹配（单测用 `typeFullName` 故意误导验证）。
- [ ] `destructive` 命令默认不出现在 typed tool 列表。

---

## S6. 文档：Threat Model 与 compat 上游边界

### S6a `docs/threat-model.md`（新增）

必须包含以下章节，每节回答对应问题并给出代码定位：

1. **信任边界**：trusted = 本机 Agent 进程、Unity Editor 进程；untrusted = 同用户其它进程、畸形 CLI 输入、过期 `bridge.json`、过期 pipe 客户端、项目内提供的 Pipeline 命令。
2. **认证**：token 生成位置（`PiUnityBridge.Session.cs`）、长度、存放（`Library/PiUnityHarness/bridge.json`）、生命周期（每次 Editor 启动/domain reload 是否轮换）、同用户其它进程能否读取（能，属已接受风险，需写明）。
3. **传输**：named pipe 的 ACL 现状（默认 DACL）、是否限制为本机与当前用户。
4. **执行面**：`eval` 任意 C#、Pipeline 命令、compile、input、YOLO safe-auto 各自的破坏半径；哪些命令被 `PIPELINE_TOOL_EXCLUDE` 或 forbidden 列表拦截（列出定义位置）。
5. **YOLO safe-auto 白名单**：`resolve_yolo_button` 当前启发式逐条列出，标注哪一条可能触发数据丢失（`Don't Save`）。
6. **错误连接**：`--project-path` 与 `bridge.json` 发现顺序（`discovery.rs:134-155`）；如何避免连到错误项目（建议：CLI 输出中回显 `project`，mux 启动时校验）。
7. **无 UI / CI 环境**：eval 与 modal probe 在 batchmode 下的行为。
8. **已接受风险清单**与**未来工作**（仅列出，不承诺）。

### S6b compat 上游边界

在 `unity/com.pi.pipeline.compat/` 根目录新增：

- `UPSTREAM_VERSION`：一行，上游 Unity Pipeline 仓库/包的版本或 commit。
- `PATCHES.md`：表格 `| 本地文件 | 上游文件 | 修改类型（新增/改写/删除） | 原因 | 关联 issue/commit |`；另有"完全本地新增文件"清单与"Unity 版本兼容矩阵"（至少覆盖当前 CI 使用的版本）。
- 规则：之后任何对 compat 目录的修改，PR 必须同步更新 `PATCHES.md` 对应行；由 CI 脚本检查"diff 触碰 compat 但未触碰 PATCHES.md"→ 失败。

不重组目录，不引入 `Upstream/`、`PiExtensions/` 分层。

### 验收标准

- [ ] 两份文档存在且每个必需章节非空。
- [ ] `PATCHES.md` 覆盖 `ScriptInterpreter.cs`、`HostBinding.cs`、`BasePipelineServer.cs`、`InPlaceReloadProcessor.cs` 四个最大文件的来源说明。
- [ ] CI 对 compat 目录的 PATCHES.md 联动检查生效。

---

## S7. `IReloadAwareOperation` 抽取（P1）

### 目标

`PiUnityBridge.OnBeforeReload / OnAfterReload` 不再硬编码具体 coordinator；跨域重载恢复成为可注册、可测试的统一契约。这是 `PiUnityBridge` 组合化的第一步，也是本 spec 内唯一承诺的 Bridge 接口抽取。

### 接口

```csharp
internal interface IReloadAwareOperation
{
    string Name { get; }                 // 用于日志与 status 诊断
    bool HasPendingRequest();            // 从 SessionState 判定，必须在静态构造后可调用
    void BeforeReload();                 // 把内存态落到 SessionState；不得抛出
    void ResumeAfterReload();            // 从 SessionState 恢复并重挂事件；回调在 Register 时注入
}

internal static class PiUnityReloadOperationRegistry
{
    static void Register(IReloadAwareOperation op);
    static void BeforeReloadAll();       // 逐个 try/catch，记录失败但继续
    static void ResumeAfterReloadAll();
    static IReadOnlyList<string> PendingNames();  // 供 status 诊断输出
}
```

### 迁移

- `PiUnityCompileCoordinator`、`PiUnityRecompileGuard`、`PiUnityTestCoordinator` 各自实现该接口；现有 `ResumeAfterReload(callback)` 签名统一改为无参。Compile coordinator 当前的 `Action<string, bool, string, string>` 与 Test coordinator 当前的 `Action<string, string>` 不强行统一到接口层，分别在 `PiUnityBridge.Start()` 首次注册时通过构造/Init 注入并由各自 operation 闭包保存。
- 回调注入发生在 `Register` 之前，且只用于 operation 完成原请求；`IReloadAwareOperation.ResumeAfterReload()` 本身不得接收 callback 参数。这样 registry 只管理无参生命周期契约，不跨 coordinator 传递不同签名的完成回调。
- `PiUnityBridge.OnBeforeReload` 中 `CancelAllPendingAsyncEvals` 之后调用 `BeforeReloadAll()`；`OnAfterReload` 中 `Start()` 之后调用 `ResumeAfterReloadAll()`。
- `editorStatus` 在 `PendingNames()` 非空时追加 `resuming=<name1,name2>`，与 S2 的 `lifecycle.reason` 互补。

### 验收标准

- [ ] `PiUnityBridge.cs` 不再直接引用任何 coordinator 类型。
- [ ] `EvalAsyncTaskTests` 同级新增 `ReloadOperationRegistryTests`：注册两个 fake op，一个 `BeforeReload` 抛异常，另一个仍被调用。
- [ ] 现有 `pi-unity compile` / `pi-unity test` 跨 reload 行为不变（手动回归清单写入 PR）。

---

## S8. `native/src/imp.rs` 叶子模块拆分（P1）

### 目标

先切与 Broker 核心状态耦合最少的四块，使 `imp.rs` 回到只含 Broker 协调逻辑；不在本阶段拆 `request_queue` / `lifecycle` / `transport`。

### 目标结构

```text
native/src/
  imp.rs                 # Broker struct、handle_line、poll/complete、status 快照、derive_lifecycle（S2）
  imp/
    windows.rs           # 现 93–119 行的 unsafe extern "system" 声明 + Handle/Bool 类型
    state_plane.rs       # 现 149–243 行；StatePlane + 布局常量；文件头注释写明内存布局与可见性保证
    modal_probe.rs       # 现 293–305、473–518 行；ModalWindowInfo/ModalObservation/refresh；不持有 Broker 引用
    yolo.rs              # 现 56–84、531–597 行；YoloMode/resolve_yolo_button/click_button_by_text
    audit.rs             # 现 1158–1336 行；append_*/query_timeline/audit_input；输入为 &ManagedRequest 与 &[u8]，不触碰 pipe
```

### 边界规则

- `modal_probe` 输出 `ModalObservation`，由 `imp.rs` 决定是否调用 `yolo::try_auto_dismiss`；probe 不直接读写 `yolo_mode` / `last_auto_click_ms`。
- `yolo::try_auto_dismiss(mode, last_click_ms, now, windows) -> Option<clicked_at_ms>`：纯函数式输入输出，Broker 负责写回原子量。
- `audit` 只依赖 `ManagedRequest` 的只读字段与文件路径，不得 `use` `Broker`。
- `state_plane.rs` 顶部 doc comment 必须包含 `docs/protocol.md` §8 的偏移表与"双槽提交顺序"说明。
- 现有 `#[cfg(test)]`（1849–2254 行）按归属迁入各模块；跨模块的 Broker 级测试留在 `imp.rs`。

### 验收标准

- [ ] `cargo test` 全绿，测试数量不减少。
- [ ] `imp.rs` 行数 ≤ 1200。
- [ ] `modal_probe.rs`、`yolo.rs`、`audit.rs` 中不出现 `Broker` 标识符。
- [ ] `cargo clippy -- -D warnings` 在新模块无新增告警。

---

## S9. MuxClient 拆分（P1）

### 目标

`index.ts` 中 `MuxClient`（现 521–1046 行）拆为三个可独立测试的单元，重启策略从隐式条件变为显式策略对象。

### 结构

```text
.pi/extensions/pi-unity-harness/
  mux/
    transport.ts        # spawn / stdin 写入 / stdout 按行分帧 / exit & stderr tail 采集
    queue.ts            # MuxJob、FIFO、单 in-flight、timeout/abort 定时器、finishJob
    restart-policy.ts   # RestartDecision = restart | fail_inflight_keep_queue | fail_all
    client.ts           # 组合器：MuxClient 对外 API 不变
  index.ts              # 仅 import MuxClient
```

`restart-policy.ts` 的输入输出：

```ts
interface RestartContext { restartsSinceStable: number; budget: number; inflightDispatched: boolean; queueLength: number; lastExit: {code:number|null; signal:string|null} }
type RestartDecision = { kind: "restart" } | { kind: "fail_inflight_keep_queue" } | { kind: "fail_all"; reason: string };
function decide(ctx: RestartContext): RestartDecision;
```

现有行为冻结为 fixture：`MUX_RESTART_BUDGET = 1`；`inflightDispatched === true` 时 in-flight 一律以 `mux_crash` 失败且 `retryAllowed: false`。

### 验收标准

- [ ] `mux.test.ts` 现有用例零修改通过（对外 API 不变）。
- [ ] 新增 `restart-policy.test.ts`，覆盖上表每个 `RestartDecision` 分支。
- [ ] `index.ts` 行数减少 ≥ 400。

---

## S10. State plane 正式化为只读通道（P2，仅定义方向）

- 只读客户端（status / timeline / lifecycle）优先从 `Local\PiUnityHarnessState_<hash>` 读取，不占用 pipe；pipe 保留给控制面。
- 需要先完成 S2（快照含 `lifecycle`）与 S4（快照含 `native` 版本，读者可校验布局版本）。
- 布局版本字段（偏移 4 的 u16）升级策略、读者对 `seq == 0` 半写状态的重试规则、与 `pi-unity status` 的 `--source pipe|state-plane|auto` 参数，待 S2/S4 落地后另立 spec。

---

## 3. 实施顺序与依赖

```text
S1 fixtures ──▶ S2 lifecycle ──▶ S7 IReloadAwareOperation
   │               │
   │               └──▶ S8 imp.rs 拆分
   ├──▶ S5 command policy
   └──▶ S9 MuxClient 拆分
S3 session 隔离（独立）
S4 版本一致性（独立） ──┐
S6 文档（独立）          └──▶ S10（P2）
```

建议批次：

1. 批次 A（可并行）：S1、S3、S4、S6。
2. 批次 B：S2、S5（依赖 S1 fixture 就位）。
3. 批次 C：S7、S8、S9（重构类，依赖状态机与 fixture 作为回归网）。

每个 S 项独立 PR；PR 描述必须引用本 spec 的验收清单并逐项勾选。

## 4. 全局约束

1. **向后兼容**：pipe 帧、mux 行、CLI exit code、`status` 既有字段在本 spec 全部项完成后仍与 §1 基线一致；只允许新增字段与新增 `protocol_mismatch` 一个 error_type。
2. **协议版本不升级**：`NATIVE_PROTOCOL_VERSION` 保持 1。若任何实现需要破坏性变更，必须先提交独立的 v2 迁移 spec。
3. **测试先行**：S1 fixture 合入前，S2/S5 不得开始实现。
4. **不新增外部依赖**：Rust/TS/C# 均使用现有依赖完成（`build.rs` 只用 `std::process::Command`）。
5. **文档同步**：任何新增字段必须在同一 PR 内更新 `docs/protocol.md`。

## 5. 开放问题

| # | 问题 | 影响项 | 建议默认 |
| --- | --- | --- | --- |
| Q1 | `protocol_mismatch` 是否应携带 `expected/actual` 结构化字段（`result: {expected:1, actual:2}`）而非仅文本 | S1 | 携带，放在 `result` 而非 `error` |
| Q2 | `mutability` 缺省为 `write` 是否会让 TS typed tool 描述大面积出现 `[WRITE]` 噪音 | S5 | 接受；促使尽快补 sidecar |
| Q3 | `PI_UNITY_AGENT_ID` 缺省是否应由 pi 扩展自动注入（例如 pi session id）而非 `default` | S3 | 扩展侧注入，CLI 保持 `default` 回退 |
| Q4 | S4 的"改源码必须改 DLL"CI 规则对纯注释/测试改动是否过严 | S4 | 排除 `native/src/**/*test*` 与 `native/tests/**` 路径 |
| Q5 | S7 是否同时抽 `INativeTransport` | S7 | 否；本 spec 仅承诺 reload 契约 |

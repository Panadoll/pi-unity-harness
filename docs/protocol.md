# pi-unity-harness 通信协议规范（v1）

> 本文档是 pi-unity-harness 客户端 ↔ Unity 进程通信协议的**唯一事实来源**。
> 实现依据：`native/src/lib.rs`（Rust native broker）、`unity/com.pi.unity-harness/Editor/PiUnityBridge.cs`（C# managed worker）、`native/src/bin/pi_unity.rs`（原生 CLI 客户端）、`.pi/extensions/pi-unity-harness/index.ts`（pi 薄封装扩展）。
> 任何实现（CLI、pi 扩展、其他语言客户端）都必须遵循本文档，不得自行扩展或改变字段语义。


## 1. 总体架构

```text
Client (任何语言)                    Unity Editor 进程
  |  named pipe, JSON line             |
  |  ------------------------------->  |  Rust native broker (cdylib)
  |                                    |    - pipe server，域重载期间不销毁
  |                                    |    - 认证、排队、超时、审计、状态平面
  |                                    |  C# managed worker
  |                                    |    - EditorApplication.update 主线程执行
  |  <-------------------------------  |    - eval / compile / snapshot / pipeline
```

- 传输：Windows Named Pipe，byte mode，UTF-8，JSON line（`\n` 分隔，每条消息一行）
- 生命周期：native broker 随 Unity 进程启动，域重载（Domain Reload）期间 pipe 不换名、连接不断
- 单客户端语义：pipe 同一时刻只服务一个客户端连接；客户端断开后 pending 请求全部以 `client_disconnected` 失败
- 客户端发现入口：`<UnityProject>/Library/PiUnityHarness/bridge.json`

## 2. bridge.json（客户端发现）

Unity Editor 启动、broker 初始化后写入 `<UnityProject>/Library/PiUnityHarness/bridge.json`（UTF-8，可能带 BOM）。

```json
{
  "project": "/path/to/UnityProject",
  "pid": 12345,
  "pipe": "\\\\.\\pipe\\pi_unity_<sha256前6字节hex>",
  "token": "0123456789abcdef0123456789abcdef",
  "generation": 3,
  "statePlaneName": "Local\\PiUnityHarnessState_<hash>"
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `project` | string | Unity 项目绝对路径 |
| `pid` | number | Unity Editor 进程 ID |
| `pipe` | string | 完整 pipe 路径（`\\.\pipe\` 前缀），客户端 `net.createConnection(pipe)` 直接使用 |
| `token` | string | 认证令牌，32 位 hex；由 Editor 会话持久化（跨域重载不变） |
| `generation` | number | managed 域代次，每次 Domain Reload +1 |
| `statePlaneName` | string | 共享内存状态平面名（`Local\` 前缀），pipe 不可达时的降级状态源 |

pipe 名为 `pi_unity_` + 项目路径 SHA-256 前 6 字节（12 位 hex）；`statePlaneName` 由归一化项目路径的 64 位 FNV-1a hash 派生。二者对同一项目多次启动结果稳定。客户端应以 `bridge.json` 的 `pipe` 字段为唯一真相，禁止按公式推导。

## 3. 帧格式

### 3.1 请求帧（Client → Broker）

```json
{
  "id": "req-1",
  "type": "execute_code",
  "token": "0123...",
  "timeoutMs": 20000,
  "payload": { "...": "..." }
}
```

| 字段 | 必填 | 说明 |
|------|------|------|
| `id` | 是 | 请求 ID，非空；响应帧用 `reply_to` 回指。建议客户端生成 uuid |
| `type` | 是 | 请求类型，见第 4 节 |
| `token` | 是 | 必须等于 bridge.json 的 token，否则响应 `unauthorized` |
| `timeoutMs` | 否 | 客户端期望的超时预算（毫秒）。native 侧实际超时 = `requested + 5000`，clamp 到 `[1000, 600000]`。默认 60000 |
| `payload` | 视类型 | 各请求类型的参数，见第 4 节 |

### 3.2 响应帧（Broker → Client）

成功：

```json
{"reply_to": "req-1", "ok": true, "result": { "...": "..." }}
```

失败：

```json
{"reply_to": "req-1", "ok": false, "error": "unauthorized", "error_type": "auth"}
```

| 字段 | 说明 |
|------|------|
| `reply_to` | 回指的请求 ID |
| `ok` | 成功标志 |
| `result` | 成功负载（对象） |
| `error` | 错误码或错误信息（字符串） |
| `error_type` | 可选；managed 侧失败时附加的错误分类（见 5.2） |

### 3.3 事件帧（Broker → Client，单向）

```json
{"type": "event", "event": "some_event", "payload": "..."}
```

客户端应忽略不认识的事件。当前 broker 保留该帧类型（C# 侧 `pi_unity_emit_event`），暂无已发布事件。

### 3.4 传输级规则

- 请求和响应都是单行 JSON，`\n` 结尾；不允许行内嵌入未转义的换行
- 请求帧最大 1 MiB（`REQUEST_BUFFER_LIMIT`），超出响应 `request_too_large`
- 客户端空闲 15 秒且无 in-flight 请求时，broker 主动断开（`CLIENT_HEARTBEAT_TIMEOUT_MS`）
- 建议客户端在无请求时以 5 秒间隔发送 `ping` 保持连接（参考客户端行为）；broker 不强制 ping，但会因 15 秒静默断连
- 客户端断开后重新连接即可继续使用；in-flight 请求不会恢复，需客户端自行重试

## 4. 请求类型

### 4.1 Broker 直接处理（不经过 managed，无域重载依赖）

| type | payload | result | 说明 |
|------|---------|--------|------|
| `ping` | 无 | `{"pong": true}` | 保活 / 连通性探测 |
| `status` | 无 | 状态负载（见 6.1） | broker 级状态；不依赖 managed ready |
| `timeline` | `{limit?, requestType?, action?, success?}` | 审计查询结果（见 7） | `limit` 1–200 默认 50；`success` = `all`/`success`/`failure`；查询本身不入审计 |
| `set_yolo` | `{mode: "off"\|"detect"\|"safe-auto"}` | `{"yoloMode": "..."}` | 设置 Win32 模态弹窗自动处理策略，默认 `off`；`safe-auto` 只点白名单按钮（Scene 弹窗点 Don't Save/Save，Import 弹窗点 Apply，通用 OK/Yes）。模态是否存在以 `status.modalObservation` 为准 |
| `bridge_capabilities` | 无 | `{protocolVersion, capabilities[]}` | 协议版本与能力列表 |

### 4.2 Managed 转发（需要 `managedState == ready`）

managed 处于 `initializing` / `reloading` / `quitting` 时，此类请求立即失败（`managed_not_ready` / `managed_reloading` / `managed_quitting`）。请求先入 broker FIFO 队列，C# worker 在 `EditorApplication.update` 主线程逐个取出执行。

| type | payload | result | 说明 |
|------|---------|--------|------|
| `execute_code` | `{code}` | eval 结果（见 6.2） | 主线程执行 C# 代码（Mono.CSharp evaluator） |
| `execute_file` | `{filePath}` | eval 结果 | 从项目内文件读代码执行；相对路径相对项目根，支持 `.repl`/`.cs` |
| `validate_execute_code` | `{code}` | eval 结果 | 先编译校验再执行（pi 扩展默认路径） |
| `validate_execute_file` | `{filePath}` | eval 结果 | 先编译校验再执行文件 |
| `validate_code` | `{code}` | `{output, typeName}`（校验通过文本） | 仅编译校验，不执行；失败帧无 `error_type` |
| `validate_file` | `{filePath}` | `{output, typeName}`（校验通过文本） | 仅编译校验文件；失败帧无 `error_type` |
| `recompile` | 无 | `{"output": "compilation_succeeded"}` 或 compile_error | 触发脚本编译并等待完成（阻塞至多 120s） |
| `context_snapshot` | `{maxDepth, maxNodes, logLimit, logLevel, includeComponents}` | 上下文快照（Editor 状态 + 场景层级 + 选择 + 近期日志） | 有界返回，默认 maxDepth=3 / maxNodes=500 / logLimit=50 / logLevel=error |
| `list_commands` | 无 | `{typeName, pipelineAvailable, commands[], count}` | 发现 com.unity.pipeline 已注册 `[CliCommand]`；未装 pipeline 时 `pipelineAvailable=false` |
| `command` | `{name, parametersJson}` | `{output, ...}` | 执行 pipeline 命令；`parametersJson` 是参数对象的 JSON 字符串 |

`filePath` 解析规则：非绝对路径按 `<UnityProject>/<filePath>` 拼接后再 `GetFullPath`；文件不存在响应 `file_not_found: <absolute path>`（`error_type=usage`）。

## 5. 错误

### 5.1 Broker 级错误（`ok:false`，`error` 为错误码）

| error | 触发条件 |
|-------|---------|
| `invalid_json` | 请求行不是合法 JSON |
| `missing_id_or_type` | 缺少 `id` 或 `type` |
| `unauthorized` | token 不匹配 |
| `request_too_large` | 请求行超过 1 MiB |
| `managed_not_ready` / `managed_reloading` / `managed_quitting` | managed 未就绪时收到转发类请求 |
| `request_timeout_before_dispatch` | 排队超时（未送达主线程） |
| `request_timeout_in_flight` | 主线程执行超时（heartbeat 正常时按 `timeoutMs` 判） |
| `managed_heartbeat_timeout` | 主线程卡死（heartbeat 超过 5s 未更新且请求在途超 5s） |
| `client_disconnected` | 客户端断开导致在途请求失败 |

`request_timeout_*` 与 `managed_heartbeat_timeout` 的判定基于 heartbeat：managed 每 <5s 上报一次 heartbeat；heartbeat 超时才用 `managed_heartbeat_timeout`，否则按客户端 `timeoutMs` 判 `request_timeout_in_flight`。

### 5.2 Managed 级错误（附加 `error_type`）

| error_type | 示例 error |
|------------|-----------|
| `usage` | `empty_code`、`empty_file_path`、`file_not_found: <path>`、`unsupported_request_type`、`request_parse_failed: ...` |
| `compile_error` | 校验失败信息（含代码扫描提示） |
| `runtime_error` | 执行异常信息 |
| `busy` | `coroutine queue full` |
| `cancelled` | 异步 eval 被取消（域重载前清理等） |

## 6. 结果负载

### 6.1 `status` / state plane 状态负载

```json
{
  "project": "/path/to/UnityProject",
  "processId": 12345,
  "pipe": "\\\\.\\pipe\\...",
  "statePlaneName": "Local\\PiUnityHarnessState_<hash>",
  "observedAtMs": 1750000000000,
  "connected": true,
  "managedState": "ready",
  "managedGeneration": 3,
  "lastHeartbeatMs": 1750000000000,
  "heartbeatAgeMs": 12,
  "heartbeatTimedOut": false,
  "editorStatus": "editing;focus=background;window=minimized",
  "focusState": "background",
  "windowState": "minimized",
  "pending": 0,
  "inFlight": 0,
  "capabilities": ["native-broker", "state-plane-v1", "..."],
  "modalObservation": { "present": false, "detectedAtMs": 0, "windows": [] },
  "yoloMode": "off"
}
```

| 字段 | 说明 |
|------|------|
| `managedState` | `initializing` / `ready` / `reloading` / `quitting` |
| `connected` | 当前是否有客户端连接（state plane 中用作"被占用"标志） |
| `editorStatus` | 分号分隔状态串，第一段为状态（`editing` / `playing` / `blocked` / `reloading` / `quitting`），后接 `key=value`：`focus=foreground|background`、`window=normal|minimized`、`mainThreadStale=1`（主线程停摆）。Win32 模态弹窗以 `modalObservation.present` 为准，不再依赖 `modal=1` |
| `capabilities` | 能力列表：`native-broker`、`direct-status`、`reload-stable-pipe`、`state-plane-v1`、`background-runner`、`focus-state`、`heartbeat-timeout`、`request-timeout`、`client-heartbeat-timeout`、`context-snapshot-v1`、`action-timeline-v1`、`modal-probe-v1` |
| `modalObservation.windows[]` | `{hwnd, title, class, buttons[]}`；`class` 为 Win32 窗口类（弹窗为 `#32770`） |

### 6.2 eval 结果

```json
{
  "output": "42",
  "typeName": "string",
  "timing": { "evalMs": 3.2, "totalMs": 5.1 }
}
```

- `typeName`：`string` / `void` / `nested_coroutine`（返回 `IEnumerator` 时跨帧执行）/ 其他 CLR 类型名
- 失败时 `ok:false`，`error_type` 见 5.2；`error_type=compile_error` 时可能附加 `hint`（修复建议）与 `pattern_violation`（错误分类）
- `validate_code` / `validate_file` 成功返回校验通过文本；失败帧只带 `error` 字符串，不带 `error_type`

### 6.3 `context_snapshot` 结果

包含：项目路径、Unity 版本与平台、PlayMode/暂停/编译/更新状态、活动场景元数据、主相机、当前选择、有界场景层级（`{name, path, active, components?}`）、近期日志（`{level, message}`）。字段以 C# 侧实现为准，客户端不应假设字段全集。

## 7. Action Timeline（操作审计）

- 存储：`<UnityProject>/Temp/PiUnityHarness/ActionTimeline/YYYY-MM-DD.jsonl`，单文件 5 MB 轮转
- 每条 JSONL 为一个事件；`started` 与 `completed` 成对，按 `actionId` + `requestId` 关联
- `started`：`{schemaVersion, event, actionId, requestId, requestType, action, timestampMs, timestampUtc, input}`
- `completed`：`{schemaVersion, event, actionId, requestId, requestType, action, timestampMs, timestampUtc, durationMs, success}` + 成功时 `result`（截断+脱敏）或失败时 `errorType`/`error`
- 脱敏：`token` / `accesstoken` / `refreshtoken` / `apikey` / `password` / `secret` / `authorization` / `credential(s)` 等键递归替换为 `[REDACTED]`
- eval 类请求只记录 `codeLength`（不落盘代码）；`context_snapshot` 参数记录但同样脱敏
- `timeline` 查询返回 `{schemaVersion, capturedAtMs, capturedAtUtc, directory, count, actions[]}`，`actions[]` 按 `completedAtMs`（无则 `startedAtMs`）倒序

## 8. State Plane（共享内存降级状态）

pipe 不可达（域重载窗口、Editor 未响应）时，客户端可读共享内存获取最近一次状态快照。

- 名字：`bridge.json` 的 `statePlaneName`（`Local\` 前缀，进程本地）
- 布局（小端）：

| 偏移 | 大小 | 内容 |
|------|------|------|
| 0 | u32 | magic `0x48554950`（"PIUH"） |
| 4 | u16 | 版本 = 1 |
| 6 | u16 | slot 数 = 2 |
| 8 | u32 | slot 大小 = 65536 |
| 12 | u32 | 写入进程 PID |
| 16 | u64 | writer 序号（每次发布 +1） |
| 24 | u64 | 创建时间 ms |
| 64 | — | slot[0]（64 KiB） |
| 64+65536 | — | slot[1]（64 KiB） |

- slot 布局：`seq u64 @0`、`payloadLen u32 @16`、payload JSON 从 @24 开始
- 写入策略：ping-pong 双缓冲，写 `(writerSeq-1) % 2` 号 slot；读端须校验 slot.seq == writerSeq 且写入前后一致，防撕裂
- payload 与 6.1 状态负载同构，其中 `connected:true` 表示 pipe 已被某客户端占用（多 session 互斥依据）

## 9. 参考客户端行为（pi 扩展）

pi 扩展 `.pi/extensions/pi-unity-harness/index.ts` 是协议的事实参考实现，值得沿用的约定：

1. 发现：扫描运行中的 `Unity.exe`（排除 AssetImportWorker / `-batchMode`），读对应项目 `bridge.json`；`statePlaneName` 存在且 `connected=true` 视为被占用
2. 连接：TCP 连 pipe（Windows named pipe 对 Node 即 `\\.\pipe\...` 路径），行缓冲解析；`connect` 后启动 5s 心跳 `ping`
3. 超时：客户端侧计时器按 `timeoutMs` 到期时先销毁 socket，再探测模态弹窗（`probeModalStatus` 一次 `status` 请求），有弹窗则报 `EDITOR_MODAL` 错误提示人工处理
4. eval 文件约定：多行 C# 写入 `<project>/Temp/PiUnityHarness/AgentScratch/*.repl`，首行 `// #repl-mode: top-level|class|auto`；inline 代码超过 512 KiB 自动改走文件通道
5. 编译后（recompile 触发域重载）连接会短暂断开重建，客户端应等待 `status` 恢复 `ready` 再继续

## 10. 版本与兼容性

- `NATIVE_PROTOCOL_VERSION = 1`；managed 侧 `pi_unity_init` 传入协议版本，不匹配时 broker 仅告警不拒绝（当前向前兼容策略）
- 增加请求类型、响应字段为兼容变更；改变帧格式、字段语义、错误码为破坏性变更，必须递增协议版本
- 本文档与代码不一致时以本文档为准，但应先修代码或文档，不允许长期分歧

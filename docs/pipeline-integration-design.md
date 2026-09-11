# com.unity.pipeline 集成设计与开发计划

> 状态：实现稿 v1（Phase 0-3 已落地，Phase 4 可选未排期）
> 目标包版本：`com.unity.pipeline@0.6.0-exp.1`（experimental，API 可能变动；asmdef 兼容范围 `[0.2.0-exp.2,0.7.0)`）
> 验证项目：本地 Unity 工程（Unity 6000.5.0f1，已安装 embedded `com.unity.pipeline@0.6.0-exp.1`）

## 1. 背景与目标

`pi-unity-harness` 与 Unity 官方 `com.unity.pipeline` 能力互补：

| 维度 | com.unity.pipeline | pi-unity-harness |
|---|---|---|
| 传输 | HTTP（Editor 7800-7849 / Player 7900-7949），**域重载时 server 销毁** | Rust native broker + named pipe，**跨域重载存活** |
| Unity 版本 | 官方仅 6000.0+；非 6 用仓库内 `com.pi.pipeline.compat` | 2021.3+ |
| 命令体系 | `[CliCommand]` 属性 + TypeCache 自动发现 + 参数 schema | 硬编码 switch（eval / recompile / status / ping） |
| 独有能力 | 测试运行、play mode 控制、热重载（in-place ILPostProcessor + override）、dev Player 控制 | 阻塞式跨重载 recompile、后台消息泵唤醒、主线程 eval + validate + coroutine pump |

> **compat fork**：`unity/com.pi.pipeline.compat` 派生自 `com.unity.pipeline@0.6.0-exp.1`（Companion License）。
> `/unity-install` 按工程 Unity 主版本选择：`>=6000` → 官方；否则 → 把 compat 复制为 embedded `Packages/com.unity.pipeline`
> （Unity 2022 的 versionDefines 只认 embedded/registry 包，不认 file: 指向工程外的 local 包）。
> 两包程序集名同为 `Unity.Pipeline`，**不可同装**。compat 在 Unity 6+ 完整保留官方全部特性（含 Roslyn eval / `run_script`、In-place HotReload、ILPostProcessor CodeGen、IlInterpreter 等）；在 Unity 2021.3/2022.x 下通过条件编译抹平 API 差异并提供兼容降级。

> ⚠️ **平台边界**：harness 当前仅支持 **Windows x64 Editor**——native broker 为
> Windows 条件编译（`native/src/lib.rs` `#[cfg(windows)]`），插件仅提供
> `Editor/Plugins/x86_64/` 的 DLL。本文所有 pipe 集成能力均以此为前提；
> pipeline 的 HTTP 通道本身跨平台，但不在本设计的传输路径上。

**目标**：不重复实现 pipeline 已有的命令能力（测试、play mode、热重载等），
让这些命令通过 harness 的 reload-stable pipe 执行——**传输归 harness，能力归 pipeline，
pi 扩展做统一门面**。pipeline 作为可选增强：检测到就桥接，检测不到退回现有最小闭环。

**非目标**：
- 不把官方 Unity 6 pipeline 源码当默认依赖进仓库（Unity 6 仍装 registry/embedded 官方包）
- 不替代 pipeline 的 HTTP server（外部 CI/CLI 用户仍可走 HTTP）
- dev Player（运行中的 development build）控制不在前三阶段范围内——broker 只活在 Editor 进程

**例外（已落地）**：为非 Unity 6 维护 `com.pi.pipeline.compat` fork（派生自 0.6.0-exp.1），仅用于 2021.3/2022 命令面；再分发需自行合规 Companion License。

## 2. 架构总览

```text
pi TS extension (.pi/extensions/pi-unity-harness/index.ts)
  │  启动时: request("list_commands") → 动态注册 unity_* 工具
  │  调用时: request("command", { name, parametersJson })
  ▼  named pipe (跨域重载存活)
Rust native broker (native/src/lib.rs, 无需改动)
  ▼  poll_request / complete_request
PiUnityBridge (C# worker, EditorApplication.update 主线程)
  ├─ 现有: execute_code / validate_* / recompile / ping / status
  └─ 新增: list_commands / command
        │  #if PI_UNITY_PIPELINE (asmdef Version Defines)
        ▼
     PiUnityPipelineCommandExecutor (新文件)
        │  CommandRegistry.DiscoverCommands() → 查 CommandInfo
        │  参数绑定 (JSON → 类型化 object[]) → Method.Invoke
        │  Task/Task<T> 结果 unwrap → JSON 序列化
        ├─ run_tests 阻塞语义 → PiUnityTestCoordinator (SessionState 跨重载恢复)
        ▼
     Unity.Pipeline / Unity.Pipeline.Editor (官方包, 可选依赖)
```

要点：

- **broker 与 Rust 侧零改动**——`command` 对 native 层只是又一种透传的请求类型
- **主线程假设天然吻合**：harness worker 本来就在主线程执行，pipeline 命令
  `MainThreadRequired` 的场景无需再经过它的 `Dispatcher`
- **绕过 pipeline 的 HTTP token**：进程内直接调用，安全边界由 harness 自己的
  pipe token（`bridge.json`）保障，与现状一致

## 3. 关键设计

### 3.1 可选依赖机制（asmdef + Version Defines）

harness 必须在**未安装** pipeline 的项目（含 2021.3）继续工作，因此不能硬引用。

方案：给 `unity/com.pi.unity-harness/Editor/` 添加 `Pi.UnityHarness.Editor.asmdef`：

```jsonc
{
  "name": "Pi.UnityHarness.Editor",
  "includePlatforms": ["Editor"],
  // 缺失时被 Unity 静默忽略；Newtonsoft 引用是必须的——pipeline 自己的 asmdef
  // 也直接引用 Unity.Nuget.Newtonsoft-Json，executor 使用 JObject/JsonConvert 同理
  "references": ["Unity.Pipeline", "Unity.Pipeline.Editor", "Unity.Nuget.Newtonsoft-Json"],
  "versionDefines": [
    // 注意：裸版本 "0.2" 在 Unity 版本表达式里意为 >= 0.2.0，会匹配未来 0.3/1.0，
    // 重新暴露 exp API 破坏风险。用半开区间锁定到已验证的版本线：
    { "name": "com.unity.pipeline", "expression": "[0.2.0-exp.2,0.7.0)", "define": "PI_UNITY_PIPELINE" }
  ]
}
```

所有 pipeline 相关代码用 `#if PI_UNITY_PIPELINE` 包裹，集中在新文件
`PiUnityPipelineCommandExecutor.cs` 中；`PiUnityBridge` 的分发处仅留最小挂钩。

⚠️ **前置风险**：Editor 目录当前没有 asmdef。新增 asmdef 会改变代码所属程序集
（影响 `Mono.CSharp` 等引用的解析方式），必须先做一次纯回归验证（Phase 0）。

### 3.2 协议扩展（pipe 请求类型）

沿用现有 NDJSON 行协议，新增两种 `type`：

**`list_commands`** — 枚举可用命令及参数 schema：

```jsonc
// 请求
{ "id": "1", "type": "list_commands", "token": "..." }
// 响应 —— 每个命令同时返回 schema（JsonSchemaGenerator 输出，TS 映射 TypeBox 的
// 唯一数据源）与 parameters（人类可读的降级/展示信息）
{ "reply_to": "1", "ok": true, "result": { "typeName": "command_list", "commands": [
  { "name": "editor_play", "description": "Enter Unity Editor play mode",
    "mainThreadRequired": true, "runtimeOnly": false,
    // 注意：GenerateCommandSchema() 返回的是 string（官方 /api/commands 也按字符串
    // 字段序列化），executor 需 JObject.Parse(schemaString) 后作为对象内联输出，
    // TS 侧才能免二次 parse 直接消费
    "schema": { /* JObject.Parse(JsonSchemaGenerator.GenerateCommandSchema(...)) */ },
    "parameters": [ { "name": "...", "type": "String", "required": true,
                      "description": "...", "defaultValue": null } ] }
] } }
// pipeline 未安装时
{ "reply_to": "1", "ok": true, "result": { "typeName": "command_list", "commands": [],
  "pipelineAvailable": false } }
```

**`command`** — 按名执行：

```jsonc
// 请求 —— 注意 parameters 编码为 JSON 字符串（parametersJson），原因见下。
// 空值契约：无参命令 TS 侧始终发 "{}"；executor 侧把 null/缺失/空白 归一化为 {}
// （双向防御，任一侧遗漏都不会 JObject.Parse 失败）
{ "id": "2", "type": "command", "token": "...", "timeoutMs": 30000,
  "payload": { "name": "run_tests",
               "parametersJson": "{\"mode\":\"editor\",\"filter\":\"MyFixture\"}" } }
// 成功
// 通用命令 (typeName=pipeline_command) 仅返回 output / typeName。
// run_tests 协调器有额外字段：command、valueTypeName（硬编码 "Unity.Pipeline.TestExecutionResponse"，
// 不论实际 value 是否为合并 JSON）、value（单段或双段合并的 test_status 结果）。
{ "reply_to": "2", "ok": true, "result": { "output": "<命令返回值 JSON 序列化>",
  "typeName": "pipeline_command" } }
// 失败（error_type: command_not_found | command_forbidden | parameter_error |
//        command_error | pipeline_unavailable | usage | timeout | cancelled | busy）
// 注意：usage / timeout / cancelled / busy 由 executor / coordinator 在特定路径生成，
// 不在 §3.2 的 command 响应主路径中，但仍是合法的 error_type 值。
{ "reply_to": "2", "ok": false, "error_type": "command_error", "error": "..." }
```

**为什么是 `parametersJson` 字符串而不是嵌套对象**：`PiUnityBridge` 用
`JsonUtility.FromJson<NativeRequest>` 解析请求，`payload` 是固定形状的可序列化类
（现为 `code`/`filePath` 字段），JsonUtility 不支持任意 JSON / `Dictionary`。
把命令参数编码为字符串字段即可零改动地通过现有解析层：`ExecutePayload` 增加
`name` 与 `parametersJson` 两个 string 字段，executor 内部再用 Newtonsoft
`JObject.Parse(parametersJson)` 展开（此路径在 `#if PI_UNITY_PIPELINE` 内，
Newtonsoft 必然可用）。备选方案（保留原始请求行、对其二次 `JObject.Parse`）
侵入更大，仅在字符串转义成为可维护性问题时再考虑。

### 3.3 命令执行器（PiUnityPipelineCommandExecutor）

pipeline 的 `CommandRegistry` / `CommandInfo` / `CommandParameterInfo`（注意：类型名
不是 `ParameterInfo`，勿与 `System.Reflection.ParameterInfo` 混淆，文件名叫
`ParameterInfo.cs` 但类叫 `CommandParameterInfo`）是 public 可直接复用，
但 `BasePipelineServer` 的参数绑定与执行逻辑是 private，需要在执行器里重写一份精简版：

1. **Unity 侧强制过滤（不依赖 TS 层）**：`list_commands` 与 `command` 共用同一份
   过滤规则——`RuntimeOnly == true` 的命令不列出且拒绝执行（pipeline 自己的
   `ExecuteCommandByName` 按名执行、不做 RuntimeOnly 过滤，pipe 请求又可被直接
   手工构造，所以必须在这里设防）；路由黑名单（`eval`、`recompile`、
   `recompile_status`，见 §3.4）同样在此拒绝，返回 `command_forbidden`。
   拒绝提示按命令给出替代路径：`eval` → 原生 `execute_code`（工具 `unity_eval`）；
   `recompile` → 原生 `recompile`（工具 `unity_recompile`，阻塞语义）；
   `recompile_status` → **无对应原生请求**（阻塞式 recompile 不需要轮询），
   提示"结果随 unity_recompile 直接返回，编译器状态可用 unity_status 观察"
2. `CommandRegistry.DiscoverCommands()` 按名查找 `CommandInfo`
3. 参数绑定：`parametersJson` 为 null/缺失/空白时先归一化为 `"{}"`，再
   `JObject.Parse` → `object[]`（按 `CommandParameterInfo.ParameterType` 转换，
   缺省用 `DefaultValue`，必填缺失报 `parameter_error`）
4. `command.Method.Invoke(null, parameters)` —— worker 已在主线程直接调用。
   **`MainThreadRequired == false` 的命令也在主线程执行**（Phase 1 决策：
   简化模型，避免引入后台线程与 Unity API 误用风险）；代价是重 CPU 的自定义
   命令会阻塞 Editor update，记入风险表，必要时后续为标记
   `MainThreadRequired=false` 的命令加 `Task.Run` 通道
5. 结果 unwrap：返回值是 `Task` / `Task<T>` 时不能阻塞主线程等待，
   转入 `PiUnityCoroutinePump` 式的延迟完成（挂到 `EditorApplication.update`
   轮询 `Task.IsCompleted`）。完成后需处理三种终态：
   - fault：展开 `AggregateException.InnerException` 作为 `command_error` 返回
   - 成功：`Task<T>` 经反射读 `Result` 属性；非泛型 `Task` 返回空结果
   - 超时：managed executor 侧 OnUpdate 轮询（默认 30s）先于 native broker 到期；
     native broker 到时已 reap 该请求并向客户端发过超时错误，managed 侧
     完成后照常调 `complete_request`，broker 对未知 id 的响应直接丢弃
6. 序列化：`string` 原样，其余 `JsonConvert.SerializeObject`
7. `list_commands` 每个命令**同时输出** `schema` 和 `parameters`（简单投影）。
   `GenerateCommandSchema()` 返回 **string**（`/api/commands` 也按字符串字段
   序列化），executor 须 `JObject.Parse(schemaString)` 后作为对象内联进响应，
   解析失败时该命令 `schema` 置 null（降级信号）。契约固定：TS 以 `schema`
   （已是 JSON object，无需二次 parse）为映射 TypeBox 的唯一数据源，
   `parameters` 仅用于 `schema` 为 null 时的降级注册，避免 Phase 2 实现分叉

### 3.4 长生命周期命令的路由策略

按域重载行为将命令分为三类：

| 类别 | 命令示例 | 策略 |
|---|---|---|
| 同步快、不触发重载 | `editor_status` `editor_play/stop/pause` `editor_focus` `set_autotick` `list_tests` | Phase 1 直接透传 |
| 触发一次重载 | `recompile` / `recompile_status` | **不透传**：`recompile` 映射到现有 `PiUnityCompileCoordinator`（阻塞语义优于官方 trigger-then-poll）；`recompile_status` 无原生对应（阻塞语义下无需轮询），直接拒绝并提示（§3.3 第 1 点） |
| 跨多次重载 | `run_tests`（playmode 进/出各重载一次） | Phase 3 新建 `PiUnityTestCoordinator`，其余先透传 pipeline 的 async 模式（`async_tests` + `test_status` 轮询）作为过渡 |


### 3.5 PiUnityTestCoordinator（run_tests 跨重载阻塞语义）

问题：`run_tests` 的生命周期跨越 1~2 次域重载，C# 静态状态（含"待回复的 request id"
和挂在 `TestRunnerApi` 上的回调对象）每次重载都会被清空，pipe 客户端只能等到超时。

**关键事实：pipeline 自己已经解决了"测试运行跨重载"的下半场**。
`PipelineTestRunner` 内置持久化与恢复机制：

- 请求/状态落盘：`Temp/pipeline_test_request.json` / `Temp/pipeline_test_status.json`
- 域重载后 `[InitializeOnLoad]` 触发 `ReattachResultCollector()` 重挂
  `TestRunnerApi` 回调、`CheckForPendingTests()` 续跑 pending run
- `test_status` 命令随时可查。**终态词以 0.2.0-exp.2 实际写盘为准**：
  `completed`（成功）/ `error` / `cancelled`；进行中为 `running`（状态文件缺失
  但请求文件存在时也视作 running），无运行时为 `no_tests`

因此 coordinator **不重写 TestRunnerApi 状态机**，只做"pipe 请求 ↔ pipeline
async 测试运行"之间的薄映射。需要我们自己持久化（SessionState）的状态：

| 键 | 用途 |
|---|---|
| `PendingTestRequestId` | 待回复的 pipe request id（回复义务） |
| `OriginalParametersJson` | 原始请求参数（重载后恢复 filter/timeout 等上下文，及启动第二段） |
| `CurrentSegment` | `single` / `editor` / `playmode`——`mode=all` 两段串行的进度指针 |
| `EditorSegmentResultJson` | `mode=all` 时 editor 段的完整汇总（playmode 段会跨重载，内存态必丢，须落 SessionState） |
| `DeadlineUtcTicks` | harness 侧阻塞等待的绝对截止时间；native 超时后 managed 侧仍会主动取消 pipeline run 并清理孤儿 SessionState |

```text
command run_tests（阻塞语义）:
  ├─ SessionState 写入: PendingTestRequestId / OriginalParametersJson / CurrentSegment
  ├─ 调 pipeline 的 run_tests（async_tests=true，立即返回，落盘由 pipeline 负责）
  └─ 挂 EditorApplication.update 轮询 test_status

每次域重载后（PiUnityBridge.OnAfterReload）:
  └─ 读 SessionState，若有 PendingTestRequestId → 重新挂起 test_status 轮询
     （pipeline 侧的 ReattachResultCollector/CheckForPendingTests 已自动恢复运行本身；
      CurrentSegment/EditorSegmentResultJson 一并恢复，两段语义不因重载丢进度）

test_status 轮询的状态处理（显式穷举）:
  ├─ running            → 继续轮询
  ├─ completed          → 单段或最后一段: 合并 EditorSegmentResultJson（若有）→
  │                       CompleteJson(PendingTestRequestId) → 清全部 SessionState 键
  │                       editor 段（mode=all 第一段）: 汇总写入 EditorSegmentResultJson →
  │                       CurrentSegment=playmode → 用 OriginalParametersJson 启动第二段
  ├─ error / cancelled  → CompleteError（携带 message 和 errorType，注明失败段）→ 清全部 SessionState 键
  │                       响应格式同 command error：{ "reply_to": "...", "ok": false, "error_type": "...", "error": "..." }
  └─ no_tests / 状态卡死超过阈值（run 消失）→ CompleteError 补发失败响应
```

**mode 语义约束**：pipeline 的 `run_tests` 默认 `mode=all`，而
`ExecuteAllModesAsync` 对 `async_tests=true` 显式返回错误（"Async mode is not
supported for 'all' mode"）。coordinator 统一走 async 包装，因此：

- `mode=editor` / `mode=playmode`：直接包装 `async_tests=true` + 轮询
  （主路径，`CurrentSegment=single`）
- `mode=all`（或缺省）：coordinator 拆为**两段串行**——先
  `run_tests(mode=editor, async_tests=true)` 轮询到终态，汇总落
  `EditorSegmentResultJson` 后再启动 `run_tests(mode=playmode, async_tests=true)`
  轮询到终态，最后合并两段汇总作为单一响应返回；任一段 error/cancelled 则
  立即以失败响应结束。段间进度与第一段结果全部走 SessionState
  （见上表），playmode 段的域重载不会丢失
- 段切换是重载敏感点：editor 段 completed 后若紧接编译/重载，
  `CurrentSegment=playmode` 已先落盘，`OnAfterReload` 恢复时据此判断
  "第二段是否已启动"（查 `test_status` 是否 running），未启动则补启动
- TS 侧 `unity_run_tests` 的 `mode` 参数默认值与 pipeline 对齐（`all`），
  语义差异对调用方透明

设计要点：

- **运行状态归 pipeline，回复义务归 harness**——职责边界与 §2 的
  "传输归 harness，能力归 pipeline" 一致
- `OnAfterReload` 的恢复逻辑与 compile coordinator 并列挂在现有
  `PiUnityBridge.OnAfterReload` 中，模式保持一致
- 超时兜底：managed executor 侧 OnUpdate 轮询（默认 30s）提供第一层超时保护；
  native broker 已有 request timeout 作为兜底；TS 侧 `run_tests` 默认给大超时
  （如 300s，与 pipeline 默认一致），Unity 侧检测到 run 异常中止（如用户手动退出
  play mode）时主动补发失败响应
- 同一时刻只允许一个 pending test run（与 compile coordinator 相同的单飞约束；
  启动前先查 `test_status`，已有运行则拒绝并提示）
- 备选（不采用）：自建 TestRunnerApi 回调 + 结果增量落盘的完整状态机。
  仅当 pipeline 的 async 机制被证实不满足需求（如结果粒度不足）时再升级

### 3.6 pi 扩展侧（TS）

1. **动态工具注册**：扩展激活后（或首次连上 bridge 时）发 `list_commands`，
   对每个命令注册 pi 工具，参数 schema 由响应中的 `schema`（JSON Schema）
   映射为 TypeBox 定义（§3.3 第 7 点的固定契约；`parameters` 仅作降级展示）
2. **命名与冲突策略**：工具名 `unity_<command>`（如 `unity_editor_play`、
   `unity_run_tests`）。以下命令**不注册**透传版，保留 harness 原生实现：
   - `eval` → 已有 `unity_eval`（validate + coroutine pump 更完善）
   - `recompile` / `recompile_status` → 已有 `unity_recompile`（阻塞语义）
   - `editor_status` → 已有 `unity_status`（含 broker/pipe 维度）
   - `runtimeOnly: true` 的命令（Player 专属）不注册
3. **懒加载与降级**：`list_commands` 返回空或 `pipelineAvailable: false` 时
   不注册任何透传工具，现有工具面不变；`unity_discover` 输出中附带
   pipeline 安装状态提示
4. **pipeline 可用性的数据来源**：TS 的 `client.status()`（`type:"status"`）被
   native broker 直接截获应答，**不会**到达 C# 分发层，所以不能靠改 C# 的
   `Status()` 透出 pipeline 状态。TS 侧以 `list_commands` 的（缓存）结果作为
   pipeline 可用性来源，`unity_status` / `unity_discover` 输出时合并该缓存
5. **工具卸载策略**：pi 扩展 API 有 `registerTool()` 但没有 unregister，只有
   `setActiveTools()`。因此扩展需**记录本次会话动态注册的工具名集合**；
   重连后（经由 `session_start` / `/unity-connect` / `unity_discover` 事件驱动）重新 `list_commands`，若命令集缩减（如切到未装 pipeline 的项目），
   对失效工具调用处理函数直接返回 "pipeline unavailable" 错误（工具句柄保留、
   行为降级），并在 `setActiveTools()` 可用的宿主上将其移出活跃集
6. **安装辅助**：`/unity-install` 增加可选步骤——检测项目 Unity 版本 ≥ 6000.0 且
   未安装 pipeline 时，**询问用户**是否向 `Packages/manifest.json` 添加
   `com.unity.pipeline@0.6.0-exp.1`（固定版本，exp 包 API 不稳定；asmdef 用半开区间 `[0.2.0-exp.2,0.7.0)` 兼容 0.2/0.3/0.4/0.5/0.6 线）

### 3.7 与官方 HTTP server 的共存

pipeline 的 `EditorPipelineServer` 照常运行（`[InitializeOnLoad]` 自启），
两条通道互不干扰：外部 CI/CLI 继续走 HTTP，pi agent 走 pipe。
`CommandRegistry` 是无状态静态发现，进程内多客户端调用安全性与
HTTP handler 并发调用等同。已知共享状态（autotick 设置、test run 单飞）
由使用者自行避免双通道同时驱动同一长任务。

## 4. 开发计划

### Phase 0 — 前置：asmdef 化与回归（0.5~1 天）

| # | 任务 | 交付物 |
|---|---|---|
| 0.1 | 给 `Editor/` 添加 `Pi.UnityHarness.Editor.asmdef`（含 §3.1 的 references + versionDefines） | asmdef + .meta |
| 0.2 | 验证 `Mono.CSharp`（evaluator 依赖）在 asmdef 下仍可解析；必要时补 precompiled reference 配置 | 编译通过 |
| 0.3 | 回归：在未安装 pipeline 的项目跑通 ping / status / eval / recompile 全链路 | 手动验收记录 |
| 0.4 | 在装了 pipeline 的 Unity 6 项目验证 `Unity.Nuget.Newtonsoft-Json` 引用生效（`#if PI_UNITY_PIPELINE` 内 `JObject.Parse` 冒烟） | 手动验收记录 |

**验收**：现有功能零回归；`PI_UNITY_PIPELINE` 在装了 pipeline 的 Unity 6 项目中被正确定义（`#if` 内打日志验证）。

**风险**：无 asmdef → 有 asmdef 的迁移可能暴露隐式程序集依赖；这是本阶段唯一目的，失败则回退并改用纯反射方案（不引用 asmdef，`Type.GetType("Unity.Pipeline.Commands.CommandRegistry, Unity.Pipeline")` 探测）。

### Phase 1 — Unity 侧命令桥（1~2 天）

| # | 任务 | 交付物 |
|---|---|---|
| 1.1 | `ExecutePayload` 增加 `name` / `parametersJson` string 字段（JsonUtility 兼容，§3.2） | PiUnityBridge.cs 修改 |
| 1.2 | 新建 `PiUnityPipelineCommandExecutor.cs`（`#if PI_UNITY_PIPELINE`）：命令查找、参数绑定（含 `parametersJson` 空值归一化）、Invoke、Task unwrap（含 fault/`Task<T>.Result` 处理）、结果序列化、schema 复用 `JsonSchemaGenerator`（string → `JObject.Parse` 内联，§3.3 第 7 点） | 新文件 |
| 1.3 | `PiUnityBridge` 分发新增 `list_commands` / `command` 两个 case。未定义 `PI_UNITY_PIPELINE` 时的降级行为分开：`list_commands` 返回 `ok:true` + 空命令表 + `pipelineAvailable:false`（成功降级，供 TS 探测）；`command` 返回 `ok:false` + `error_type: pipeline_unavailable` | PiUnityBridge.cs 修改 |
| 1.4 | Unity 侧强制过滤：`RuntimeOnly` 命令不列出且拒绝执行；黑名单（`eval`/`recompile`/`recompile_status`）返回 `command_forbidden`；`run_tests`（非 async）暂拒绝并提示用 `async_tests`（Phase 3 解除） | 同上 |
| 1.5 | Task 型返回值接入延迟完成机制（复用/参照 `PiUnityCoroutinePump` 的挂起-轮询模式） | 同上 |

**验收**：在装好 pipeline 的 Unity 6 项目中，通过 pipe 手工发包验证
`list_commands` 返回完整命令表；`command editor_play/editor_stop/list_tests/set_autotick`
成功；错误路径（未知命令、缺参、pipeline 未装）返回正确 `error_type`。

### Phase 2 — pi 扩展动态工具（1 天）

| # | 任务 | 交付物 |
|---|---|---|
| 2.1 | `index.ts`：连接后发 `list_commands`，按 §3.6 规则动态注册 `unity_*` 工具（schema 映射、排除清单、runtimeOnly 双保险过滤） | index.ts 修改 |
| 2.2 | `unity_discover` / `unity_status` 输出合并 `list_commands` 缓存的 pipeline 可用性（§3.6 第 4 点，不改 native status 路径） | 同上 |
| 2.3 | `/unity-install` 增加 pipeline 可选安装询问（Unity ≥ 6000.0 时） | 同上 |
| 2.4 | 断线/重载后的工具表刷新：通过事件驱动刷新——`session_start`、`/unity-connect` 或 `unity_discover` 时重新 `list_commands`；命令集缩减时按 §3.6 第 5 点降级失效工具（无 unregister API） | 同上 |

**验收**：pi 会话中 LLM 可见并成功调用 `unity_run_tests`（async 模式）、
`unity_editor_play` 等工具；在未装 pipeline 的项目中工具面与现状完全一致。

### Phase 3 — run_tests 跨重载协调器（2~3 天，风险最高）

| # | 任务 | 交付物 |
|---|---|---|
| 3.1 | 新建 `PiUnityTestCoordinator.cs`：薄映射方案（§3.5）——SessionState 持久化（`PendingTestRequestId`/`OriginalParametersJson`/`CurrentSegment`/`EditorSegmentResultJson`/`DeadlineUtcTicks`）+ 包装 pipeline `run_tests(async_tests=true)` + `test_status` 轮询（显式处理 `completed`/`error`/`cancelled`/`running`/`no_tests`）+ `OnAfterReload` 恢复轮询与段进度 | 新文件 |
| 3.2 | `mode=all` 的两段串行语义（editor → playmode，第一段汇总落 SessionState，段切换重载敏感点处理，合并响应，§3.5）；单段 mode 直接包装 | 同上 |
| 3.3 | 验证 pipeline 的 `ReattachResultCollector` / `CheckForPendingTests` 在 pipe 驱动下跨重载正确恢复；结果粒度不足时评估升级为自建回调状态机（§3.5 备选） | 验证记录 |
| 3.4 | `command run_tests` 从"拒绝同步模式"切换为走 coordinator 的阻塞语义；解除 1.4 的限制 | PiUnityBridge.cs 修改 |
| 3.5 | 异常路径：run 中途用户干预 / Editor 关闭 / 编译错误导致测试无法启动 → 补发失败响应；孤儿 SessionState 清理 | 同上 |
| 3.6 | TS 侧 `unity_run_tests` 默认切换为阻塞模式（大超时），保留 async 参数 | index.ts 修改 |

**验收**：EditMode 与 PlayMode 测试各验证一次完整阻塞往返（PlayMode 需覆盖
进/出 play mode 两次重载）；`mode=all` 验证两段串行合并；中途手动退出
play mode 能收到失败响应（`cancelled`/`error` 终态）而非超时。

### 实施状态与验证记录

| 项 | 证据 |
|---|---|
| Phase 0 asmdef | 已新增 `Pi.UnityHarness.Editor.asmdef`，Unity Bee rsp 中出现 `PI_UNITY_PIPELINE`；`Pi.UnityHarness.Editor` 程序集已加载 |
| Phase 1 命令桥 | 反射调用 `BuildListCommandsResponse` 返回 `pipelineAvailable=True`、14 个命令；`eval` / `recompile` / `runtime_status` 被过滤；`schema` 为 JSON object |
| Phase 2 动态工具 | `.pi/extensions/pi-unity-harness/index.ts` 可被 jiti 加载；连接后会 `list_commands` 并动态注册 `unity_*`；`unity_status` / `unity_discover` 合并 pipeline 安装与可用性缓存 |
| Phase 3 run_tests | 反射调用 `command run_tests`（EditMode、空过滤器）完成阻塞往返，返回 `status=completed`、`summary.total=0`；`mode=all` 空过滤器返回两段合并结果；真实 PlayMode 过滤 `MyCompany.Example.Tests.ExampleRefTests.Constructor_SetsGuid` 触发域重载（managedGeneration 20→21），pipeline 状态文件 `completed/passed=1`，coordinator SessionState 已清理 |
| TDD 记录 | 红灯：实现前经 pipe 请求 `list_commands` 返回 `unsupported_request_type`；绿灯：实现后 `list_commands`/`editor_status`/`run_tests` 反射路径与 Unity 编译门禁通过 |
| 编译门禁 | 使用 Unity 生成的 `Pi.UnityHarness.Editor.rsp` 调 `csc.exe`，无 `error CS`（仅 Unity analyzer 装载警告） |

### Phase 4（可选，暂不排期）— dev Player HTTP 通道

控制 development Player 构建（runtime server：`reload_file`、`set_timescale` 等）
只能走 HTTP（broker 不在 Player 进程内）。若有需求再立项：TS 侧加 HTTP client，
读取 `.unity-pipeline-runtime-port` descriptor + Bearer token。

## 5. 风险与开放问题

| 风险 | 影响 | 缓解 |
|---|---|---|
| pipeline 是 exp 包，`CommandRegistry`/`CommandInfo` API 可能破坏性变更 | 升级即编译错误 | 默认安装固定 `0.6.0-exp.1`；所有引用集中在单一 executor 文件；versionDefines expression 锁定 `[0.2.0-exp.2,0.7.0)`（§3.1） |
| asmdef 迁移破坏 evaluator 的 `Mono.CSharp` 解析 | Phase 0 阻塞 | 预留反射方案兜底（见 Phase 0 风险） |
| pipeline 命令内部假设 HTTP/`Dispatcher` 上下文 | 个别命令行为异常 | Phase 1 验收逐命令冒烟；异常命令加入路由黑名单 |
| 所有命令（含 `MainThreadRequired=false`）都在主线程执行（§3.3 第 4 点） | 重 CPU 自定义命令阻塞 Editor update | 先接受简化模型；确有需求再为 `MainThreadRequired=false` 命令加 `Task.Run` 通道 |
| PlayMode 测试重载时序边界（如 domain reload 被项目设置禁用） | coordinator 状态机分支增多 | Phase 3 显式覆盖 `Enter Play Mode Options` 两种配置的测试 |
| 双通道（HTTP + pipe）同时驱动 recompile/run_tests | 状态互踩 | 文档声明单驱动约束；coordinator 启动前查 `test_status`，已有运行则拒绝 |
| 平台边界：pipe 集成仅 Windows x64 Editor（§1） | 其他平台无法使用本设计的所有能力 | 文档显式声明；跨平台支持是 harness 层面的独立课题 |

**开放问题**：

1. `list_commands` 是否要把 harness 原生请求（eval/recompile/status）也纳入统一命令表？（当前设计：不纳入，保持两层清晰）
2. 用户自定义 `[CliCommand]` 命令若长时间阻塞主线程，pipe 请求会占住 worker 的 update 循环——
   managed executor 已提供 OnUpdate 轮询超时（默认 30s）作为 per-command 保护。

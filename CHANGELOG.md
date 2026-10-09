# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与
[Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Fixed

- CLI observe 摘要先解包 Pipeline 的 `value` / JSON `output`，避免真实捕获成功后误报 0 帧、空 dHash 和空路径；保持公开参数、字段和默认模式不变。
- CLI capture/observe 的 `--out` 与 `--interval` 改发 C# 声明的 `path` / `interval_ms`；此前这两个参数被静默忽略。
- Harness 72 个无 C# 默认值的 `CliArg` 参数显式声明 `Required=true`，修正 discovery metadata、schema.required 与缺参绑定的一致性；保留官方及 compat 参数解析规则。
- `--version --json` 与 `--json --version` 正确输出版本 JSON，保留裸版本短路，并拒绝额外未知参数。

### Added

- 初始开源版本：native broker、Unity Editor 包、pi 扩展、文档
- 本地维护脚本 `scripts/command-inventory.py`：消费真实 list-commands metadata，比较 schema/policy 多重集，导出 extension 规则和实际 skill 校验；不接入 CI。协议 fixture 改为执行真实消费者。
- 纯 CLI 架构（CLI-First / No-MCP）：新增 `pi-unity` 独立原生 CLI 二进制
  （`native/src/bin/pi_unity.rs`，基于 clap），覆盖 ping / status / eval / compile /
  snapshot / list-commands / pipeline / run-tests / observe / capture / timeline /
  skills 十二个子命令，支持 `--json` 结构化输出与统一退出码
  （0 成功 / 1 失败 / 2 未连接 / 3 超时）。
- 细粒度 Agent Skills 源文件（`skills/`）：pi-unity、pi-unity-status、
  pi-unity-eval、pi-unity-compile、pi-unity-snapshot、pi-unity-observe、
  pi-unity-capture、pi-unity-timeline、pi-unity-pipeline、pi-unity-run-tests，
  支持 `pi-unity skills install --agents|--claude` 一键同步。
- 双模式运行机制文档化：速度模式（snapshot + eval + uitree_* 白盒）与
  GUI 模式（observe + capture，大图落地 `Temp/PiUnityHarness/Captures/`）。
- 文档 handoff：`docs/handoff-cli-only-migration.md` 完整记录 MCP → CLI 迁移指南。

### Changed

- **行为变更**：`run_tests` 有失败用例时 CLI 返回 `ok:false`、退出码 1、`error_type:test_failed`，计数和失败列表在 `result`；pi 扩展的 `unity_run_tests` 因此以错误返回，结果在 `details.result`。
- **行为变更**：视觉领域失败（capture/observe/analysis/provider）不再被桥接 `ok:true` 掩盖，CLI 返回非零退出码，原始领域结果在 `result`。
- **行为变更**：`capture --mode game` 改走 `vision_capture_async`（EOF 捕获，领域 `timeout_ms` 等于 `--timeout`）；scene 仍走同步 `vision_capture`。
- 清理未接线的 native broker/extension 抽取残留。
- 自有 Pipeline 命令按领域目录拆分为独立静态类，文件名与类名一致；内部 `PiMcp` 命名迁移为 `PiPipeline`，支撑/模型归入 `Pipeline/Binding`、`Policy`、`Models`，测试同步按领域整理，程序集、GUID、公开命令和参数默认值保持不变。
- JSON 协议按 wire/domain/presentation 划分：C# 提取 PipeEnvelope/JsonText，Rust 分离错误分类与领域解包，TS 统一实际 exec/mux parser 并独立工具展示；保留方向差异与公开字段，强化控制字符和消费者语义测试。
- Vision 捕获路径解析合并到 `CapturePathService`（按 profile 保留全部旧路径规则），失败 JSON 统一由 `VisionJson.BuildCaptureFailedJson` 生成；同步/异步与 GameView 失败语义不变。
- 校正文档中的 session 存储、CLI 构建输出、截图路径、Pipeline 命令示例、第三方许可证路径和 threat model 代码位置；不改变运行时行为。

- Skill 安装器（`skills install`）引入基于 `.pi-unity-manifest.json` 的文件同步机制：重装时自动收敛清理源端已删除/改名的废弃文件，同时严格保留用户自行添加的文件与自定义目录。
- pi 扩展按 Unity 6 官方 0.8 引导动态命令：`codereload_status` / `cleanup_codereload`、`wait_for async=true` + `wait_status` / `wait_cancel`、`console_status`。compat 0.6 的 `hotreload_status` 不再被写成官方新名称。`jobTimeoutMs` 不带 `job=true` 直接 usage 拒绝。失败 job 的工具 `details.result` 保留 JobFinished snapshot。
- `skills/` 收成一份 `pi-unity`：子命令细节在 `skills/pi-unity/references/`。`pi-unity skills install` 递归拷整个 skill 目录。已安装的九份细粒度 skill（目录里只有匹配的 `SKILL.md`）会在下次 install 时删掉。pipeline 示例改为 `gameobject_find` / `gameobject_create` / `uitree_find -p query=`。skill 不再要求先跑 `pi-unity mark`。
- 结构收口：`PiUnityBridge` 拆成 Native/Pump/Status/Eval/Session partial；CLI 拆成 args/client/discovery/output/commands；native broker 拆出 `imp.rs`/`ffi.rs`。ping/status 只由 native 回答；C# 不再扫 `#32770`。YOLO 默认 `off`，模态以 `modalObservation` 为准。编译入口统一为 `PiUnityCompileCoordinator.RequestRecompile`。
- `.pi/extensions/pi-unity-harness` 由直连 named pipe 的完整实现改为 typed tools
  薄封装，底层统一调用 `pi-unity` CLI；`enabled` 默认值改为 `true`。
- `docs/protocol.md`：pipe 名改为 `pi_unity_` + 项目路径 SHA-256 前 6 字节（12 位 hex），
  `statePlaneName` 由归一化项目路径 64 位 FNV-1a hash 派生；明确以 `bridge.json`
  的 `pipe` 字段为唯一真相。
- `native/Cargo.toml`：crate-type 增加 `rlib`，加入 CLI 所需依赖（clap、base64、regex）。
- `scripts/build-native.ps1`：构建后同时拷贝 CLI 二进制到 `bin/`，DLL 拷贝失败时
  非 Strict 模式降级为警告（Unity 占用插件目录场景）。

### Removed

- `scripts/mcp-server.mjs`、`scripts/mcp-handshake.mjs`（从未声明 MCP SDK，无法运行）。
- 零引用 `PiMcpUnityMcpAliases.cs`。
- 旧 skill：`skills/unity-harness-mcp/SKILL.md`、`skills/unity-playtest-loop/SKILL.md`（以 MCP 为前提）。
- 九份按子命令拆的消费 skill：`pi-unity-eval`、`pi-unity-compile`、`pi-unity-status`、`pi-unity-snapshot`、`pi-unity-pipeline`、`pi-unity-run-tests`、`pi-unity-observe`、`pi-unity-capture`、`pi-unity-timeline`。内容并进 `skills/pi-unity/`。

### Fixed

- 重编译闪退防御：PlayMode 运行中（场景存在活跃 PlayableGraph）直接触发强制同步重编译时，
  Domain Reload 销毁 Playable 会回调上一 AppDomain 的托管侧，访问失效 GC handle 导致编辑器
  SIGSEGV 闪退。新增 PiUnityRecompileGuard 守卫：编译前强制安全退出 PlayMode，等待非托管
  资源完全释放、进入 EditMode 稳定后再发起编译；等待状态经 SessionState 持久化跨 Domain
  Reload 存活，60s 超时兜底返回 playmode_exit_timeout，不挂住 pipe 端请求。
- 测试运行期间请求重编译返回 busy，避免打断运行中的测试（含 PlayMode 段）。
- 修正编译协调器延迟检查在 isCompiling 时丢失调度链、请求永不回调的问题。
- 新增 PiUnityRecompileGuard 契约测试（8 个用例，覆盖延迟、busy、超时、重新进入 PlayMode 等分支）。

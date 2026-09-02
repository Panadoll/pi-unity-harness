# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与
[Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added

- 初始开源版本：native broker、Unity Editor 包、pi 扩展、文档
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

- `.pi/extensions/pi-unity-harness` 由直连 named pipe 的完整实现改为 typed tools
  薄封装，底层统一调用 `pi-unity` CLI；`enabled` 默认值改为 `true`。
- `docs/protocol.md`：pipe 名改为 `pi_unity_` + 项目路径 SHA-256 前 6 字节（12 位 hex），
  `statePlaneName` 由归一化项目路径 64 位 FNV-1a hash 派生；明确以 `bridge.json`
  的 `pipe` 字段为唯一真相。
- `native/Cargo.toml`：crate-type 增加 `rlib`，加入 CLI 所需依赖（clap、base64、regex）。
- `scripts/build-native.ps1`：构建后同时拷贝 CLI 二进制到 `bin/`，DLL 拷贝失败时
  非 Strict 模式降级为警告（Unity 占用插件目录场景）。

### Removed

- MCP 架构：`scripts/mcp-server.mjs`、`scripts/mcp-handshake.mjs` 标记为 deprecated
  legacy archive，不再作为接口（源码保留便于对照）。
- 旧 skill：`skills/unity-harness-mcp/SKILL.md`、`skills/unity-playtest-loop/SKILL.md`
  （以 MCP 为前提），由细粒度 CLI skills 取代。

### Fixed

- 重编译闪退防御：PlayMode 运行中（场景存在活跃 PlayableGraph）直接触发强制同步重编译时，
  Domain Reload 销毁 Playable 会回调上一 AppDomain 的托管侧，访问失效 GC handle 导致编辑器
  SIGSEGV 闪退。新增 PiUnityRecompileGuard 守卫：编译前强制安全退出 PlayMode，等待非托管
  资源完全释放、进入 EditMode 稳定后再发起编译；等待状态经 SessionState 持久化跨 Domain
  Reload 存活，60s 超时兜底返回 playmode_exit_timeout，不挂住 pipe 端请求。
- 测试运行期间请求重编译返回 busy，避免打断运行中的测试（含 PlayMode 段）。
- 修正编译协调器延迟检查在 isCompiling 时丢失调度链、请求永不回调的问题。
- 新增 PiUnityRecompileGuard 契约测试（8 个用例，覆盖延迟、busy、超时、重新进入 PlayMode 等分支）。

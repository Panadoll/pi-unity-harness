# pi-unity-harness

中文 | [English](README.en.md)

`pi-unity-harness` 是 AI 编码代理（Agent）与 Unity Editor 之间的纯命令行桥接层（CLI-First / No-MCP）。通过轻量、高效的 `pi-unity` 命令行工具和 Agent Skills，任何 AI 代理（Claude Code, Codex, Antigravity, Pi, Aider 等）或开发者均可通过 Shell 命令直接驱动 Unity Editor 执行 C# 代码、触发编译与测试、读取场景快照及执行视觉跑测。

- `native/` — Rust 实现：Native Broker（`cdylib` 插件，跨域重载存活）+ `pi-unity` 独立原生 CLI 二进制
- `unity/com.pi.unity-harness/` — Unity Editor 包，C# 侧负责主线程调度与 Pipeline 命令执行
- `skills/` — 一份 `pi-unity` Agent Skill（细节在 `references/`），`pi-unity skills install` 会递归拷到 `.agents/skills/` 或 `.claude/skills/`。下次安装会清掉旧的九份细粒度 skill（目录里只有匹配的 `SKILL.md` 才删）
- `.pi/extensions/pi-unity-harness/` — pi-coding-agent 专用的 typed tools 薄封装（底层统一调用 `pi-unity` CLI）

---

## 核心特性

- **纯 CLI 架构（No-MCP）**：无 Node.js 运行时依赖，启动毫秒级，天然解耦与防崩溃。
- **双模式运行机制**：
  - **速度模式（默认）**：`snapshot` + `eval` + `uitree_*` 白盒交互，耗时几十毫秒，不看大图，节省 Token。
  - **GUI 模式（按需）**：`observe` + `capture` 多帧捕获与 dHash 变化检测，大图自动存盘（`Temp/PiUnityHarness/Captures/`），禁止 Base64 倾倒到 stdout。
- **自动域重载重连**：`pi-unity compile` 触发编译后，自动接管连接断开并在重载完成后轮询至 `ready` 状态。
- **AXI 输出**：默认 stdout 为 TOON；`--json` 才输出 JSON。无参 `pi-unity` 打印 live dashboard。Exit Code `0`（成功，含幂等 no-op）、`1`（失败，含未连接/超时）、`2`（用法错误）。

---

## pi 扩展 mux

pi-coding-agent 扩展在 `session_start` 拉起隐藏子命令 `pi-unity mux`，工具调用走 stdin/stdout JSONL，不再每次 spawn CLI。每条回复带 `result`（精简 JSON）和 `text`（同一份 result 的 Rust TOON）。JSONL 只是 IPC，模型看到的是 `text`。

mux 持有一条 Unity Named Pipe，已连接时每 5 秒 ping，broker 15 秒空闲会断。断线后下一条业务再连，已发出的请求不重放。当前 broker 同一时刻只服务一个客户端，mux 存活期间其它短 CLI 可能 `Pipe busy`。不要当成多 Agent 同时可用。本通道不恢复 slash UI。


## CLI 命令速查

| CLI 子命令 | 参数选项 | 行为描述 |
| :--- | :--- | :--- |
| `pi-unity` | 无 | live dashboard（bin / 状态 / 下一跳） |
| `pi-unity ping` | `--timeout <ms>` | 探测 Unity Broker 连通性 |
| `pi-unity status` | `--json` `--full` | Editor 状态、域重载代次、焦点与模态 |
| `pi-unity eval <code>` | `-f, --file <path>` | 主线程执行 C# 或 `.repl`/`.cs` |
| `pi-unity compile` | `--timeout <ms>` | 触发编译并等到 ready |
| `pi-unity snapshot` | `--depth` `--max-nodes` `--fields` `--full` | 场景层级（默认 path,name,active）、选中、日志 |
| `pi-unity list-commands` | `--full` | 列出 Pipeline 命令（默认 name,summary） |
| `pi-unity pipeline <name>` | `-p <key=val>` `--params-json` | 执行 Pipeline `[CliCommand]` |
| `pi-unity run-tests` | `--mode <edit\|play>` `--filter` | EditMode / PlayMode 测试 |
| `pi-unity observe` | `--frames` `--interval` `--overlay` | 多帧捕获 + dHash，stdout 只给路径 |
| `pi-unity capture` | `--mode` `--out` | 单张视口截图 |
| `pi-unity timeline` | `--limit` `--success` | 审计历史（默认 id,name,ok） |
| `pi-unity setup` | `--project` | 安装 SessionStart hook（Claude / Codex / OpenCode） |
| `pi-unity skills install` | `--agents` `--claude` `--target` | 安装 Agent Skill；`skills check` 防漂移 |

---

## 安装与快速上手

### 1. 在 Unity 项目中安装 Harness 包

把 `unity/com.pi.unity-harness` 作为本地 UPM 包加入 Unity 项目的 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.pi.unity-harness": "file:../pi-unity-harness/unity/com.pi.unity-harness"
  }
}
```

Unity Editor 打开后会自动初始化 native broker 并写入 `Library/PiUnityHarness/bridge.json`。

### 2. 构建与使用 CLI

在仓库根目录下运行构建脚本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-native.ps1
```

编译出的 `pi-unity.exe` 位于 `bin/` 和 `dist/`，可加入系统 PATH，或直接调用：

```bash
# live dashboard（无参）
pi-unity

# 安装 SessionStart hook（Claude / Codex / OpenCode）
pi-unity setup

# 执行 C# 表达式
pi-unity eval "UnityEngine.Application.unityVersion"

# 同步 Skill 到当前项目的 .agents/skills/
pi-unity skills install --agents
```

### 3. 在 pi-coding-agent 中使用（可选）

如需在 pi-coding-agent 中使用 typed tools，在 `~/.pi/agent/settings.json` 中配置：

```json
{
  "extensions": [
    "<pi-unity-harness-仓库路径>/.pi/extensions/pi-unity-harness"
  ],
  "pi-unity-harness": {
    "enabled": true
  }
}
```

---

## 验证闭环工作流（Verify Loop）

所有修改 Unity 代码、场景或资产的 Agent 都应遵循标准闭环：

```text
1. Observe 观察   → pi-unity snapshot （建立改前基线）
2. Act 行动       → 修改代码 / 资产 / 场景
3. Compile 编译   → pi-unity compile （等待域重载完成，确认 0 错误）
4. Verify 验证     → pi-unity run-tests --mode edit 或 pi-unity eval 探针
5. Re-observe 复核 → pi-unity snapshot （对比改后状态与预期结果）
```

---

## 可观测性与日志系统（Observability & Logging）

`pi-unity-harness` 内置轻量级可观测性系统（L0 采集与 L1 存储），记录所有 Agent 与 Unity 的交互调用过程，用于排障和持续进化。

### 1. 存储结构（`~/.pi-unity/`，可通过 `PI_UNITY_LOG_DIR` 覆盖）
- `logs/events-YYYY-MM.jsonl`：统一事件流（单次调用记录一行，包含耗时、阶段耗时、退出码与 `errorType`，按月轮转）。
- `logs/traces/YYYY-MM-DD/`：详细过程日志（命令失败或开启 `--trace` / `PI_UNITY_TRACE=1` 时落盘，保留 7 天）。
- `sessions/current.json`：粘性会话注册表（TTL 12 小时；全用户一份，多 agent 并行会互相覆盖）。

### 2. 会话与打点命令
```bash
# 开启粘性会话
pi-unity session start --task "重构战斗系统"

# 可选：额外标记一次 skill 使用（CLI 调用本身已经记日志）
pi-unity mark --skill pi-unity --event used

# 结束会话
pi-unity session end
```

### 3. 隐私红线与 Best-effort 保障
- **隐私保护**：绝不落盘 C# 代码、参数值、Token 或错误原文（仅记 `errorType`）；项目路径仅记 SHA256 前 6 字节哈希（`projectHash`）。
- **Best-effort**：调用日志与 trace 写入失败不影响 CLI 主流程和退出码。`session start` 例外：注册表没写上就失败。
- **skill mark**：`pi-unity mark` 是约定打点，不是自动用量统计。

---

## License & Third-Party Notices

本仓库核心代码以 **MIT License** 发布，详见 [LICENSE](LICENSE)。

### 第三方开源组件与致谢

本项目集成了以下优秀的开源项目与组件，特此致谢：

- **[UnityCliLoop (Uloop)](https://github.com/hatayama)** (MIT License) - 提供方法级热重载（Hot Reload V3）与暂停点（Pause Point）核心实现，位于 `unity/com.pi.unity-harness/Vendor/Uloop/`。
- **[Lib.Harmony](https://github.com/pardeike/Harmony)** by Andreas Pardeike (MIT License) - 提供运行时 C# 方法体 IL 注入与 JIT Hook 支持（作为 `UnityCliLoop.0Harmony.dll` 引入，详见 `unity/com.pi.unity-harness/Vendor/Uloop/Editor/PausePoint/Plugins/LICENSE.md`）。
- **[.NET Roslyn Libraries](https://github.com/dotnet/roslyn)** by .NET Foundation (MIT License) - 提供内存中 C# 动态代码分析与编译元数据支持，详见 `unity/com.pi.unity-harness/Vendor/Uloop/Editor/Compiler/Plugins/CodeAnalysis/LICENSE.md`。
- **Unity.Pipeline** by Unity Technologies - `unity/com.pi.pipeline.compat/` 按 Unity Package Distribution License 协议发布，详见其子目录下的 [LICENSE.md](unity/com.pi.pipeline.compat/LICENSE.md)。

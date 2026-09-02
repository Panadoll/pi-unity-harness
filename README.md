# pi-unity-harness

中文 | [English](README.en.md)

`pi-unity-harness` 是 AI 编码代理（Agent）与 Unity Editor 之间的纯命令行桥接层（CLI-First / No-MCP）。通过轻量、高效的 `pi-unity` 命令行工具和 Agent Skills，任何 AI 代理（Claude Code, Codex, Antigravity, Pi, Aider 等）或开发者均可通过 Shell 命令直接驱动 Unity Editor 执行 C# 代码、触发编译与测试、读取场景快照及执行视觉跑测。

- `native/` — Rust 实现：Native Broker（`cdylib` 插件，跨域重载存活）+ `pi-unity` 独立原生 CLI 二进制
- `unity/com.pi.unity-harness/` — Unity Editor 包，C# 侧负责主线程调度与 Pipeline 命令执行
- `skills/` — 细粒度 Agent Skills 源文件，支持通过 `pi-unity skills install` 一键同步到 `.agents/skills/` 或 `.claude/skills/`
- `.pi/extensions/pi-unity-harness/` — pi-coding-agent 专用的 typed tools 薄封装（底层统一调用 `pi-unity` CLI）

---

## 核心特性

- **纯 CLI 架构（No-MCP）**：无 Node.js 运行时依赖，启动毫秒级，天然解耦与防崩溃。
- **双模式运行机制**：
  - **速度模式（默认）**：`snapshot` + `eval` + `uitree_*` 白盒交互，耗时几十毫秒，不看大图，节省 Token。
  - **GUI 模式（按需）**：`observe` + `capture` 多帧捕获与 dHash 变化检测，大图自动存盘（`Temp/PiUnityHarness/Captures/`），禁止 Base64 倾倒到 stdout。
- **自动域重载重连**：`pi-unity compile` 触发编译后，自动接管连接断开并在重载完成后轮询至 `ready` 状态。
- **标准退出码与格式化输出**：支持人类可读与 `--json` 结构化输出；Exit Code `0`（成功）、`1`（失败）、`2`（未连接）、`3`（超时）。

---

## CLI 命令速查

| CLI 子命令 | 参数选项 | 行为描述 |
| :--- | :--- | :--- |
| `pi-unity ping` | `--timeout <ms>` | 探测 Unity Broker 连通性 |
| `pi-unity status` | `--json` | 获取 Editor 状态、域重载代次、焦点与模态弹窗状态 |
| `pi-unity eval <code>` | `-f, --file <path>` | 在 Unity 主线程执行 C# 表达式或 `.repl`/`.cs` 文件 |
| `pi-unity compile` | `--timeout <ms>` | 触发 Unity 脚本重新编译并自动等待就绪 |
| `pi-unity snapshot` | `--depth <N>` `--max-nodes <N>` `--log-limit <N>` `--log-level <error\|warning\|all>` `--no-components` | 获取活动场景层级、组件选择和近期日志 |
| `pi-unity list-commands` | `--json` | 列举全部已注册的 Pipeline `[CliCommand]` |
| `pi-unity pipeline <name>` | `-p <key=val>` `--params-json <json>` | 执行 Unity Pipeline `[CliCommand]` |
| `pi-unity run-tests` | `--mode <edit\|play>` `--filter <pattern>` | 运行 Unity 测试套件（EditMode / PlayMode） |
| `pi-unity observe` | `--frames <N>` `--interval <ms>` `--overlay <grid\|annotations\|both\|none>` | 视觉跑测感知（多帧捕获 + dHash + 变化检测） |
| `pi-unity capture` | `--mode <game\|scene>` `--out <path>` | 单张视口截图（速度模式） |
| `pi-unity timeline` | `--limit <N>` `--success <all\|success\|failure>` | 查询操作审计历史 |
| `pi-unity skills install` | `--agents` `--claude` `--target <dir>` | 安装 Agent Skills 到目标项目 |

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
# 验证连通性
pi-unity ping

# 查看编辑器状态
pi-unity status

# 执行 C# 表达式
pi-unity eval "UnityEngine.Application.unityVersion"

# 同步 Skills 到当前项目的 .agents/skills/
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
- `sessions/current.json`：粘性会话注册表（TTL 12 小时）。

### 2. 会话与打点命令
```bash
# 开启粘性会话
pi-unity session start --task "重构战斗系统"

# 标记 Skill 使用
pi-unity mark --skill pi-unity-compile --event used

# 结束会话
pi-unity session end
```

### 3. 隐私红线与 Best-effort 保障
- **隐私保护**：绝不落盘 C# 代码、参数值、Token 或错误原文（仅记 `errorType`）；项目路径仅记 SHA256 前 6 字节哈希（`projectHash`）。
- **Best-effort**：日志与 trace 写入失败绝不影响 CLI 主流程执行与退出码。

---

## License


本仓库核心代码以 MIT 协议发布，见 [LICENSE](LICENSE)。
`unity/com.pi.pipeline.compat/` 按 Unity Package Distribution License 发布，见 `unity/com.pi.pipeline.compat/LICENSE.md`。

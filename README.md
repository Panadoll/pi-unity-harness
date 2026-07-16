# pi-unity-harness

`pi-unity-harness` 把 `pi-coding-agent`、`unity-harness` 的 Editor 主线程执行模型，以及 Locus 的 reload-stable native broker 架构组合到一起。

当前仓库提供一个最小闭环：

- `native/` — Rust `cdylib`，在 Unity 进程内持有 named pipe server，域重载期间不销毁
- `unity/com.pi.unity-harness/` — Unity Editor 包，C# 侧负责主线程执行与域重载生命周期
- `.pi/extensions/pi-unity-harness/` — pi TypeScript 扩展，给 LLM 暴露连接、上下文快照、操作审计、Pipeline 与 eval 工具

## 架构

```text
pi-coding-agent (TS extension)
  -> named pipe client
Rust native broker (Unity process, survives domain reload)
  -> background request queue
Unity C# worker
  -> main-thread execute on EditorApplication.update
```

## 与两个上游的关系

- 借鉴了 `unity-harness` 的思路：
  - C# 侧主线程执行
  - `EditorApplication.update` drain 队列
  - `PostMessage` / `PostThreadMessage` 唤醒 Editor 消息泵
  - `Application.runInBackground = true` 降低失焦后 update 停摆概率
  - Mono.CSharp evaluator 做轻量 `eval`
- 借鉴了 Locus 的思路：
  - native broker 作为 Unity 进程内稳定连接层
  - managed 侧只负责 poll / execute / complete
  - 域重载时 pipe 不换名

## 当前能力

- `unity_ping`：检查 native broker 是否在线
- `unity_status`：查看 broker / managed state / pending queue
- `unity_eval`：在 Unity Editor 主线程执行 C# 代码
- `unity_snapshot`：一次获取 Editor 状态、活动场景层级、当前选择和近期日志；深度、节点数与日志数均有上限
- `unity_timeline`：查询 `Temp/PiUnityHarness/ActionTimeline/*.jsonl` 中的追加式操作审计，支持按请求类型、动作和成功状态过滤
- `bridge.json`：Unity 启动后写入 `Library/PiUnityHarness/bridge.json`，供 pi 扩展发现 pipe 与 token
- 后台保活：Editor 启动 bridge 时临时启用 `Application.runInBackground`，后台线程收到请求后用 `WM_NULL` 唤醒消息泵
- 最小化恢复：收到请求时如果主窗口处于最小化状态，会先调用 `ShowWindow(SW_RESTORE)` 再唤醒消息泵
- 状态面：`unity_status` 返回 `focusState`、`windowState`、`heartbeatAgeMs`、`heartbeatTimedOut`
- 超时保护：native broker 检测 heartbeat 超时与请求超时，避免 Editor 停泵后请求永久悬挂
- 模态对话框保活：Save Scene 等 Win32 弹窗会卡住主线程时，后台 pump 线程继续发送 native heartbeat，避免误报 `managed_heartbeat_timeout`；`editorStatus` 会带 `modal=1` / `mainThreadStale=1`

## 构建 native DLL

```powershell
./scripts/build-native.ps1
```

生成并复制到：

```text
unity/com.pi.unity-harness/Editor/Plugins/x86_64/pi_unity_harness_native.dll
```

## 在 Unity 中安装

把 `unity/com.pi.unity-harness` 作为本地 UPM 包加入 Unity 项目，例如 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.pi.unity-harness": "file:../pi-unity-harness/unity/com.pi.unity-harness"
  }
}
```

Unity Editor 启动后会自动：

- 初始化 native broker
- 写入 `Library/PiUnityHarness/bridge.json`
- 域重载前上报 `reloading`
- 域重载后重连并恢复请求 pump

## 在 pi 中安装扩展

扩展入口：

```text
.pi/extensions/pi-unity-harness/index.ts
```

### 方式 A：全局扩展（推荐）

**配置文件必须是** `~/.pi/agent/settings.json`（不是 `~/.pi/settings.json`）。

```json
{
  "extensions": [
    "F:/Projects-Test/unity-ai-tool/pi-unity-harness/.pi/extensions/pi-unity-harness"
  ],
  "pi-unity-harness": {
    "enabled": false
  }
}
```

- `extensions`：把本扩展设为 **pi 全局扩展**（所有项目可用）
- `pi-unity-harness.enabled`：**默认 `false`（关闭）**
  - `false`（默认）：**不注册**任何 `unity_*` tool，不自动连 Unity；仅保留 `/unity-harness` 命令
  - `true`：注册工具并尝试连接

需要用时手动开：

```text
/unity-harness on              # 仅当前会话
/unity-harness on --persist    # 当前会话 + 写入 settings（下次默认也开）
/unity-harness off             # 关掉（工具从 active 移除；已注册的定义仍在进程内，但不会被模型调用）
```

> 说明：Pi 无法在运行时“卸载”已 `registerTool` 的定义。关闭时的保证是：
> 1）启动时若 `enabled=false`，**根本不会 register**；
> 2）会话中 off 后，从 active tools 移除 + `tool_call` 硬拦截。

项目级覆盖写在 `<project>/.pi/settings.json`（同 key，项目优先于全局）。

进程级临时覆盖（优先级最高）：

```bash
# bash / Git Bash
export PI_UNITY_HARNESS_ENABLED=0

# PowerShell
$env:PI_UNITY_HARNESS_ENABLED = "0"
```

会话内切换：

```text
/unity-harness status            # 查看 runtime / settings / 当前激活的 unity 工具
/unity-harness on
/unity-harness off
/unity-harness on --persist      # 会话 + 写入 ~/.pi/agent/settings.json
/unity-harness off --persist
/unity-harness on --project      # 会话 + 写入 <cwd>/.pi/settings.json
/unity-harness off --project
```

改完 `enabled` 后需 **新开 pi 会话**（或 `/unity-harness on|off`）才会生效；`/reload` 也会重新读 settings。

### 方式 B：项目本地

把仓库里的扩展目录放到项目 `.pi/extensions/`，或在该仓库内直接运行 pi（自动发现 `.pi/extensions/pi-unity-harness`）。

如果当前 cwd 不是 Unity 项目根目录，需要设置：

```bash
export PI_UNITY_PROJECT=/path/to/UnityProject
```

也可以直接指定 bridge 文件：

```bash
export PI_UNITY_BRIDGE_FILE=/path/to/UnityProject/Library/PiUnityHarness/bridge.json
```

## 工具示例

```text
unity_ping
unity_status
unity_snapshot { maxDepth: 3, maxNodes: 500, logLimit: 50, logLevel: "error" }
unity_timeline { limit: 20, success: "failure" }
unity_eval { code: "UnityEngine.Debug.Log(123); 123" }
```

## 上下文快照与操作审计

`unity_snapshot` 默认返回：

- 项目路径、Unity 版本与平台
- Editor 的 PlayMode、暂停、编译和更新状态
- 活动场景元数据、主相机和当前选择
- 有界场景层级；默认深度 3、最多 500 个节点
- 近期错误日志；可用 `logLevel: "all"` 包含普通日志

Action Timeline 在 native broker 接收请求时写 `started` 事件，在响应、超时或断开时写 `completed` 事件，因此可跨 Domain Reload 保留操作开始记录。eval 仅记录代码长度，不落盘原始代码；Pipeline 参数与结果会截断并递归脱敏常见凭据字段，也不会写入 bridge token。`unity_timeline` 查询本身不进入时间线，避免递归噪声。单个 JSONL 文件达到 5 MB 后自动轮转。

## 当前限制

- 目前不包含 Locus 那种 engine-level background hook；失焦依赖 `runInBackground` 与 Win32 唤醒，最小化时采用恢复窗口兜底
- `unity_eval` 使用 Mono.CSharp evaluator，偏向 REPL / 轻量主线程诊断，不是完整 Roslyn 编译管线
- 目前只实现了 Windows named pipe 路径

## 下一步

建议优先补三件事：

1. `unity_recompile` / `unity_refresh` 工具
2. 更强的请求分类与错误码（`managed_reloading`、`managed_not_ready`、`compile_failed`）
3. 可选的 Locus 级 background hook（若需要避免恢复最小化窗口）

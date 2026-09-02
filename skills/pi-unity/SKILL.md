---
name: pi-unity
description: >
  Drive Unity Editor via the pi-unity CLI (status, eval, compile, snapshot,
  pipeline, run-tests, observe, capture, timeline). Use when interacting with a
  running Unity Editor, inspecting scenes or PlayMode, compiling C#, evaluating
  scripts, running tests, taking screenshots, or following the verify loop.
---

# pi-unity

`pi-unity` 是 AI 编码代理与 Unity Editor 之间的纯 CLI 交互通道。通过直接执行 shell 命令与 Unity Editor 进行双向通信、执行 C# 代码、触发编译、运行测试套件、获取场景快照以及进行视觉跑测。

## 常用命令速查

| 命令 | 用途 | 典型参数 |
| :--- | :--- | :--- |
| `pi-unity ping` | 探测 Editor 连通性 | `--timeout 5000` |
| `pi-unity status` | 获取 Editor 与 Broker 运行状态 | `--json` |
| `pi-unity snapshot` | 获取场景层级、组件与最近日志 | `--depth 3 --log-limit 50` |
| `pi-unity eval "<code>"` | 在 Unity 主线程执行 C# 表达式或代码 | `-f path/to/script.repl` |
| `pi-unity compile` | 触发脚本编译并自动等待重载就绪 | `--timeout 120000` |
| `pi-unity list-commands` | 列出全部注册的 Pipeline 命令 | `--json` |
| `pi-unity pipeline <name>` | 执行指定 Pipeline `[CliCommand]` | `-p key=val --params-json "{...}"` |
| `pi-unity run-tests` | 运行 EditMode 或 PlayMode 测试套件 | `--mode edit` / `--mode play` |
| `pi-unity observe` | 多帧视觉捕获与画面变化感知 | `--frames 3 --interval 160` |
| `pi-unity capture` | 单张视口截图（速度模式） | `--mode game` / `--mode scene` |
| `pi-unity timeline` | 查询近期操作审计历史 | `--limit 20` |

## 双模式机制（Speed 模式 vs GUI 模式）

- **速度模式（默认）**：
  - 日常开发与重构走结构化白盒通道，耗时短（毫秒级），不看图、不调用 VLM。
  - 使用 `pi-unity snapshot` 查看场景树与日志，`pi-unity eval` 进行定向状态探查，`pi-unity pipeline uitree_*` 定位与操作 UI。
- **GUI 模式（按需）**：
  - 仅在需要验证画面观感、坐标对齐、动画反馈、Toast 或无 UI 树的自定义绘制时使用。
  - 使用 `pi-unity observe` 或 `pi-unity capture` 捕获画面进行视觉分析，验完立即降回速度模式。

## 闭环开发工作流（Verify Loop）

修改 Unity 脚本、场景或资产时，必须执行完整的验证闭环：

```text
1. Observe 观察   → pi-unity snapshot （建立改前基线）
2. Act 行动       → 修改代码 / 资产 / 场景
3. Compile 编译   → pi-unity compile （等待域重载完成，确认 0 错误）
4. Verify 验证     → pi-unity run-tests --mode edit 或 pi-unity eval 探针
5. Re-observe 复核 → pi-unity snapshot （对比改后状态与预期结果）
```

## 退出码约定

- `0`：成功。
- `1`：运行时错误 / 执行失败。
- `2`：未能连接 Unity（Unity 未运行或未加载 `com.pi.unity-harness`）。
- `3`：操作超时或主线程卡死。

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity --event used`。


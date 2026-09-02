---
name: pi-unity-status
description: >
  Check Unity Editor status, broker connection, domain reload state, and modal
  dialogs via pi-unity status and pi-unity ping. Use when checking if Unity is
  ready, investigating connection errors, or detecting blocking modal dialogs.
---

# pi-unity-status

用于探测 Unity Editor 与底层 Native Broker 状态、检查域重载进度以及模态弹窗状态。

## 基础用法

```bash
# 快速连通性探测
pi-unity ping

# 获取完整状态信息
pi-unity status

# 结构化 JSON 输出
pi-unity status --json
```

## 关键字段说明

| 字段 | 含义 | 说明 |
| :--- | :--- | :--- |
| `managedState` | 托管代码状态 | `ready`（就绪）、`reloading`（域重载中）、`initializing`（初始化中） |
| `editorStatus` | 编辑器主状态 | `editing`、`playing`、`paused` 等 |
| `focusState` | 焦点状态 | `focused`（有焦点）、`background`（后台） |
| `managedGeneration` | 域重载代次 | 每次 C# 重新编译后递增 |
| `modalObservation` | 模态窗口探测 | `present: true` 时表明有弹窗阻塞主线程（包含窗口标题与按钮列表） |

## 典型场景

- **确认就绪**：在执行其它重度命令前，执行 `pi-unity status` 确保 `managedState == "ready"`。
- **弹窗阻塞排查**：如果命令响应缓慢或超时，检查 `modalObservation.present` 是否存在弹窗。

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity-status --event used`。


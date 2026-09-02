---
name: pi-unity-timeline
description: >
  Query recent operations timeline and audit history from the Unity native
  broker via pi-unity timeline. Use for debugging long processes or auditing
  command execution performance and failures.
---

# pi-unity-timeline

查询底层 Native Broker 记录的近期操作审计流水与执行时间轴，用于长流程调试或失败复盘。

## 基础用法

```bash
# 查询最近 20 条操作事件（默认）
pi-unity timeline

# 查询最近 50 条操作事件
pi-unity timeline --limit 50

# 仅筛选失败的操作记录
pi-unity timeline --success failure

# 仅筛选成功的操作记录
pi-unity timeline --success success
```

## 输出信息说明

每个事件包含：
- `action`：执行的请求或命令名称
- `requestId`：唯一请求标识符
- `startedAtUtc` / `completedAtUtc`：精确时间戳
- `durationMs`：耗时毫秒数
- `success`：执行成功/失败标识
- `input` / `result`：输入参数与输出响应摘要

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity-timeline --event used`。


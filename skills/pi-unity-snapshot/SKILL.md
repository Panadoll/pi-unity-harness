---
name: pi-unity-snapshot
description: >
  Get a comprehensive context snapshot of the Unity Editor (active scenes,
  hierarchy, selection, and console logs) via pi-unity snapshot. Use before and
  after actions to observe baseline state and verify changes.
---

# pi-unity-snapshot

有界获取 Unity Editor 上下文快照：包含当前活动场景、GameObject 层级树、挂载的组件列表、当前选中的对象以及最近的 Console 日志。

## 基础用法

```bash
# 默认快照（深度 3，错误日志 50 条）
pi-unity snapshot

# 指定遍历深度与节点数量上限
pi-unity snapshot --depth 2 --max-nodes 300

# 获取包含警告在内的近期日志
pi-unity snapshot --log-level warning --log-limit 100

# 仅获取 GameObject 树结构，省略组件详情（体积更小）
pi-unity snapshot --no-components
```

## 输出结构说明

- `project`：项目名称、Unity 版本、根目录路径。
- `scene`：当前打开场景名称、路径、根节点总数、MainCamera 路径。
- `hierarchy.roots`：场景根节点数组，递归包含子节点、Layer、Tag、InstanceId、挂载组件类型全名。
- `selection`：当前在 Editor 中选中的对象实例。
- `logs`：符合等级过滤的近期 Console 日志条目及堆栈摘要。
- `editor`：`isPlaying`, `isCompiling`, `isPaused`, `timeSinceStartup` 等状态标志。

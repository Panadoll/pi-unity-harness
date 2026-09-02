# snapshot

有界读取 Editor 上下文：活动场景、层级、组件、选中对象、近期 Console。

```bash
pi-unity snapshot
pi-unity snapshot --depth 2 --max-nodes 300
pi-unity snapshot --log-level warning --log-limit 100
pi-unity snapshot --no-components
```

默认深度 3，错误日志 50 条。

- `project`：项目名、Unity 版本、根目录
- `scene`：当前场景、根节点数、MainCamera 路径
- `hierarchy.roots`：根节点，含子节点、Layer、Tag、InstanceId、组件类型
- `selection`：当前选中
- `logs`：按等级过滤的 Console
- `editor`：`isPlaying`、`isCompiling`、`isPaused`、`timeSinceStartup`

改前拍一张，改后再拍一张。不要靠猜场景树。

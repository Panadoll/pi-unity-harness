# snapshot

有界读取 Editor 上下文。默认 TOON，层级列只有 `path,name,active`。

```bash
pi-unity snapshot
pi-unity snapshot --depth 2 --max-nodes 300
pi-unity snapshot --log-level warning --log-limit 100
pi-unity snapshot --fields childCount,tag
pi-unity snapshot --full
```

默认深度 3，错误日志 50 条。默认不含 components；`--full` 或 `--fields components` 才要。快照是活动场景的有界、可能不完整的层级树：运行时生成的对象、DontDestroyOnLoad 根、自绘 UI 可能不完整。树浅不代表场景空，这类状态用 eval 探针或 `uitree_*` 补查。

默认字段：

- `scene`、`selection`、`errors`（错误摘要，随 log level 变化；字段形状以实际返回为准）
- `nodes: N of M total`
- `hierarchy`：path, name, active
- `logs`：level, message

`--max-nodes` 默认 500 是有用的节点预算，别收到个位数；树被截断时按提示加大（`pi-unity snapshot --max-nodes <M> --full`）。

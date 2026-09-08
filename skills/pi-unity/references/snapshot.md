# snapshot

有界读取 Editor 上下文。默认 TOON，层级列只有 `path,name,active`。

```bash
pi-unity snapshot
pi-unity snapshot --depth 2 --max-nodes 300
pi-unity snapshot --log-level warning --log-limit 100
pi-unity snapshot --fields childCount,tag
pi-unity snapshot --full
```

默认深度 3，错误日志 50 条。默认不含 components；`--full` 或 `--fields components` 才要。

默认字段：

- `scene`、`selection`、`errors`
- `nodes: N of M total`
- `hierarchy`：path, name, active
- `logs`：level, message

树被截断时会给 `pi-unity snapshot --max-nodes <M> --full`。

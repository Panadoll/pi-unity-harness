# pipeline

发现并执行 `[CliCommand]`。命令名和参数名以 `pi-unity list-commands` 为准。参数是 snake_case。默认只给 `name,summary`；`--full` 才带 schema。

```bash
pi-unity list-commands
pi-unity list-commands --full
pi-unity list-commands --json
```

不熟的命令先查 schema，再调用。

```bash
pi-unity pipeline gameobject_find -p name="Main Camera"
pi-unity pipeline gameobject_create -p name="Player" -p parent_path="WorldRoot"
pi-unity pipeline uitree_snapshot --params-json "{\"interactive_only\": true}"
pi-unity pipeline uitree_find -p query="Start Game"
pi-unity pipeline input_click -p x=640 -p y=360
```

`gameobject_find` 按 `name`、`path` 或 `instance_id` 找，没有叫 `query` 的参数。
`gameobject_create` 的父节点参数是 `parent_path`，不是 `parent`。

## 参数推断

- `true` / `false` → boolean
- 整数 / 浮点 → number
- `{...}` / `[...]` → JSON
- 其余当字符串

## 常用前缀

真正有哪些名字，仍以 `list-commands` 为准。下面只是入口。

- `gameobject_*`：找、建、改、挂组件
- `assets_*`：资源
- `scene_*`：场景
- `uitree_*`：UI 树。`uitree_find` 用 `query`
- `input_*`：点击、探测。坐标是 GameView 左上角
- `vision_*`：截图和分析

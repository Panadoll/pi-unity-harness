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
pi-unity pipeline input_probe -p x=640 -p y=360
pi-unity pipeline input_click -p x=640 -p y=360
pi-unity pipeline input_drag -p from_x=640 -p from_y=200 -p to_x=640 -p to_y=500
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
- `assets_*`：资源。`assets_refresh` 只触发异步刷新，不等导入 / 域重载结束，必须接着 `pi-unity compile` 等到 ready；`force` 使用 `ForceUpdate` 也不代表刷新同步完成
- `scene_*`：场景
- `uitree_*`：UI 树。`uitree_find` 用 `query`；UI 流程先 `uitree_snapshot interactive_only=true`，再 find / probe，然后动作
- `input_*`：点击、探测。坐标是 GameView 左上角。拖拽优先一次 `input_drag`，少拆 `input_drag_start/move/end`
- `vision_*`：截图和分析

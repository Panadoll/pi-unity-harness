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

## 官方 0.8 与 compat 0.6

名字以 `list-commands` 为准。Unity 6 官方 `0.8` 与 compat `0.6` 不是同一套：

- Code Reload：官方 `codereload_status` / `cleanup_codereload`。compat 才是 `hotreload_status` / `cleanup_hotreload`，不要把旧名说成 0.8。
- 长条件：`wait_for` 加 `async=true`，再用 `wait_status` / `wait_cancel`。`wait_id` 不是 pipeline job 的 `jobId`。同步 `wait_for` 会占住 exec 队列。
- console：拉条目用 `console`；只要计数和编译失败标记用 `console_status`。
- eval：官方 `eval_file` 只接受 `.cs`。Harness 多行 REPL 仍是 `pi-unity eval -f`（可跑 `.repl`）。`run_script` 编译磁盘脚本入口，不是临时 REPL。

## job

长命令用 `--job` 提交后立即返回 `jobId`，再用 `pipeline-job`：

```bash
pi-unity pipeline eval_file --job --job-timeout 300000 -p path="Temp/probe.cs"
pi-unity pipeline-job status <jobId>
pi-unity pipeline-job progress <jobId>
pi-unity pipeline-job cancel <jobId>
```

`--timeout` 在 `--job` 时只是提交预算。`--job-timeout` 是执行预算（默认 300000，范围 1..86400000），必须和 `--job` 一起用，单独出现是用法错误。running 上的 cancel 只请求协作取消，终态以实际返回为准。查询成功和 job 失败分开：终态失败的载荷仍带 JobFinished snapshot，里面有 `jobId` / `state`。

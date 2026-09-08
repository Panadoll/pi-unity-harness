# timeline

查 native broker 记下的近期操作。

```bash
pi-unity timeline
pi-unity timeline --limit 50
pi-unity timeline --success failure
pi-unity timeline --success success
```

默认列：`id`（requestId）、`name`（action）、`ok`（success）。空结果写成 `actions: 0 条时间线记录 found`。命令失败或耗时长时先看这个。

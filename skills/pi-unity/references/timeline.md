# timeline

查 native broker 记下的近期操作。

```bash
pi-unity timeline
pi-unity timeline --limit 50
pi-unity timeline --success failure
pi-unity timeline --success success
```

每条有 `action`、`requestId`、起止时间、`durationMs`、`success`、输入和结果摘要。命令失败或耗时长时先看这个。

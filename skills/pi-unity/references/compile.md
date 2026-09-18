# compile

触发脚本编译，等待域重载结束、`managedState` 回到 `ready`。

```bash
pi-unity compile
pi-unity compile --timeout 180000
pi-unity compile --json
```

默认超时 120000 ms。

1. CLI 发 `recompile`。
2. 以编译结果为准：收到成功结果才继续等 `ready`；编译失败按错误返回，不得报成 `compiled:true`。
3. Unity 卸掉当前 AppDomain，IPC 会断。
4. CLI 轮询 `status`，`managedState == "ready"` 时返回，并带上新的 `generation`。

改过 `.cs` 之后必须跑这一步。退出码不是 0 就不要宣称代码已经生效。编译不自动恢复 PlayMode（域重载会退出 PlayMode）；要继续验证就重新进 Play。

`assets_refresh` 之类只触发异步刷新，不等导入 / 域重载结束，之后必须接本命令等到 `ready`，再继续后续操作。
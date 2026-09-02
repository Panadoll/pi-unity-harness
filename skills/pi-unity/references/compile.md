# compile

触发脚本编译，并等到域重载结束、`managedState` 回到 `ready`。

```bash
pi-unity compile
pi-unity compile --timeout 180000
pi-unity compile --json
```

默认超时 120000 ms。

1. CLI 发 `recompile`。
2. Unity 卸掉当前 AppDomain，IPC 会断。
3. CLI 每 500ms 问一次 `status`。
4. `managedState == "ready"` 时返回，并带上新的 `generation`。

改过 `.cs` 之后必须跑这一步。退出码不是 0 就不要宣称代码已经生效。

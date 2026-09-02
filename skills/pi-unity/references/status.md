# status

探测 Editor 和 native broker。重度命令前先确认 `managedState` 是 `ready`。命令慢或超时，先看 `modalObservation.present`。

```bash
pi-unity ping
pi-unity status
pi-unity status --json
```

`ping` 和 `status` 由 native broker 直接回答。域重载期间仍可用来看状态。

| 字段 | 含义 |
| :--- | :--- |
| `managedState` | `ready` / `reloading` / `initializing` |
| `editorStatus` | `editing` / `playing` / `paused` 等 |
| `focusState` | `focused` / `background` |
| `managedGeneration` | 每次 C# 重编译后递增 |
| `modalObservation` | `present: true` 时有弹窗挡住主线程，带标题和按钮 |

`pi-unity ping` 成功时 result 是 `{"pong":true}`。

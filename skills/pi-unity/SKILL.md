---
name: pi-unity
description: >
  Drive a running Unity Editor via the pi-unity CLI (status, eval, compile,
  snapshot, pipeline, run-tests, observe, capture, timeline). Use when Unity is
  already open and you need to inspect scenes, run C#, compile, test, or capture
  PlayMode. Do not use this skill to edit the pi-unity-harness source tree.
---

# pi-unity

`pi-unity` 是代理和已经在跑的 Unity Editor 之间的 CLI。Editor 要已加载 `com.pi.unity-harness`。命令名和参数以 `pi-unity list-commands --json` 为准，不要猜。

需要某条子命令的字段表时，读同目录 `references/<命令>.md`。

## 命令速查

| 命令 | 用途 | 细节 |
| :--- | :--- | :--- |
| `pi-unity ping` | 探测 broker | [status.md](references/status.md) |
| `pi-unity status` | Editor / 域重载 / 模态 | [status.md](references/status.md) |
| `pi-unity snapshot` | 场景树、选中、日志 | [snapshot.md](references/snapshot.md) |
| `pi-unity eval "<code>"` | 主线程执行 C# | [eval.md](references/eval.md) |
| `pi-unity compile` | 编译并等到 `ready` | [compile.md](references/compile.md) |
| `pi-unity list-commands` | 列出 `[CliCommand]` | [pipeline.md](references/pipeline.md) |
| `pi-unity pipeline <name>` | 跑一条 pipeline | [pipeline.md](references/pipeline.md) |
| `pi-unity run-tests` | EditMode / PlayMode | [run-tests.md](references/run-tests.md) |
| `pi-unity observe` | 多帧画面 | [observe.md](references/observe.md) |
| `pi-unity capture` | 单张截图 | [capture.md](references/capture.md) |
| `pi-unity timeline` | 最近操作 | [timeline.md](references/timeline.md) |

```bash
pi-unity pipeline gameobject_find -p name="Main Camera"
pi-unity pipeline gameobject_create -p name="Player" -p parent_path="WorldRoot"
pi-unity pipeline uitree_find -p query="Start Game"
pi-unity pipeline uitree_snapshot -p interactive_only=true
pi-unity pipeline input_click -p x=640 -p y=360
```

## 速度模式和 GUI 模式

日常走速度模式：`snapshot`、`eval`、`uitree_*`。不看大图。

只有画面、动画、坐标、自绘 UI 对不上的时候才用 `observe` / `capture`。图写到 `Temp/PiUnityHarness/Captures/`，stdout 只给路径。不要把 Base64 倒进终端。

## Verify loop

改脚本、场景或资产时按这个走。无关的只读查询不必五步都做。

```text
1. snapshot    改前基线
2. 改代码 / 资产 / 场景
3. compile     等到 ready，退出码必须是 0
4. run-tests 或 eval
5. snapshot    对一下改后状态
```

## 退出码

- `0` 成功
- `1` 执行失败
- `2` 连不上 Unity（没开 Editor，或没加载 harness）
- `3` 超时或主线程卡住

`--json` 时 stdout 是 JSON。别把 stderr 拼进 JSON。

## eval 红线

1. 多行或临时变量写到 `Temp/PiUnityHarness/AgentScratch/*.repl`，用 `pi-unity eval -f`。
2. eval 里不要调 `AssetDatabase.Refresh()`，也不要自己触发编译。编译用 `pi-unity compile`。
3. 代码跑在 Editor 主线程。不要 `Thread.Sleep`，不要同步死锁。

## 可观测性

CLI 每次调用会自己记一条 call 事件。你不必先跑 `pi-unity mark`。项目若要额外审计，再手动 `pi-unity mark`。

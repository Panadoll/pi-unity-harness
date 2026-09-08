---
name: pi-unity
description: >
  通过 pi-unity CLI 驱动已打开的 Unity Editor（status、eval、compile、
  snapshot、pipeline、run-tests、observe、capture、timeline）。Unity 已打开且
  需要检查场景、跑 C#、编译、测试或截 PlayMode 时使用。不要用本 skill 改
  pi-unity-harness 源码树。
---

# pi-unity

从 shell 驱动正在运行的 Unity Editor。默认 stdout 是 TOON；`--json` 才输出 JSON。无参 `pi-unity` 打印 live dashboard，不要先跑 `--help`。

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
| `pi-unity setup` | 安装 SessionStart hook | 见下 |
| `pi-unity skills install` | 安装本 skill | 见下 |

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

- `0` 成功，含幂等 no-op（文案含 already … (no-op)）
- `1` 执行失败，含连不上 Unity、超时
- `2` 用法错误（缺参、未知 flag、未知子命令）

`--json` 时 stdout 是 JSON。别把 stderr 拼进 JSON。默认 TOON。`--help` 永远合法。

## eval 红线

1. 多行或临时变量写到 `Temp/PiUnityHarness/AgentScratch/*.repl`，用 `pi-unity eval -f`。
2. eval 里不要调 `AssetDatabase.Refresh()`，也不要自己触发编译。编译用 `pi-unity compile`。
3. 代码跑在 Editor 主线程。不要 `Thread.Sleep`，不要同步死锁。

## 发现

优先 `pi-unity setup` 装 SessionStart hook（Claude Code / Codex / OpenCode），每个会话注入 live dashboard。本 skill 是第二条路径，不含 live Editor 状态。CLI 版本 0.1.0。

# capture

单张视口截图。GameView 或 SceneView。只在视觉 mismatch 或自绘渲染 UI 需要核对时用，不要每步截图。

```bash
pi-unity capture --mode game
pi-unity capture --mode scene
pi-unity capture --mode scene --out "Temp/PiUnityHarness/Captures/scene_view.png"
pi-unity capture --json
```

返回 `status`、`path`、`width`、`height`、`bytes`。不要往终端倒 Base64。结果里的 `embed:false` 之类标记只是建议，宿主不强制阻止内联图片，别依赖它省 token。

## 输出 profile

三类目录互不合并。缺省 path 只落在自己的 profile，不会改到另一个默认目录。

| profile | 默认相对路径 | 谁写 | 相对 path 基准 |
|---|---|---|---|
| HarnessVision 单帧（`vision_capture` / `capture`） | `Temp/Harness/vision/` | Unity | 工程根 |
| Playtest observe | `Library/PiUnityHarness/playtest/observe` | Unity | 工程根 |
| Playtest `vision_capture_after` | `Library/PiUnityHarness/playtest/after` | Unity | 工程根 |
| Camera / isolation（`vision_capture_camera`、`vision_capture_isolated`） | `Temp/PiUnityHarness/Screenshots/` | Unity | **cwd**（`Path.GetFullPath`），不是工程根 |
| CLI Base64 剥离 | `Temp/PiUnityHarness/Captures/` | CLI presentation | 工程根；不是 Unity capture 默认目录 |

## 参数与同步

`pi-unity capture` 默认 `--mode game`，内部发 `vision_capture_async`。`--mode scene` 仍走同步 `vision_capture`。同步 GameView 在 HarnessVision 里是 `not_supported`，所以默认不再走那条路径。

| CLI | 发出的 JSON 键 | C# `[CliArg]` | 说明 |
|---|---|---|---|
| `--mode` | `mode` | `mode` | 默认 `game` → `vision_capture_async`；`scene` → `vision_capture` |
| `--out` | `path` | `path` | 真实输出路径。缺省不发，由 C# 落到 `Temp/Harness/vision/` |
| `--timeout` | 传输 `timeoutMs` | game 同时发 `timeout_ms` | 两处都是 `--timeout`（默认 30000）。不另加固定余量，也不改成 C# 声明默认 60000。scene 同步命令没有 `timeout_ms`，不发 |

GameView 仍要在 PlayMode。需要与传输等待不同的领域预算，或加标注时，用已有 pipeline：

```bash
pi-unity pipeline vision_capture_async -p mode=game -p path=Temp/Harness/vision/game.png -p timeout_ms=60000
```

# 截图 recipe：capture / observe

给编码代理的截图规范。坐标系统、命令选择与证据管理。

## 坐标系统

- 所有输入/输出坐标均为 `top_left_game_view` 像素：左上角 (0,0)，右下角为
  GameView 尺寸减一。
- capture JSON 里 `screenshot_to_gameview` 把截图像素换算回 GameView 像素
  （截图可能被缩放/重采样）。
- Unity 内部坐标（EventSystem / ScreenPointToRay）是左下原点，换算公式：
  `unity_y = game_view_height - input_y`。

## 命令选择

| 场景 | 命令 |
|------|------|
| 单张 SceneView 截图（编辑模式） | `pi-unity capture --mode scene` |
| 单张 GameView 截图（PlayMode，含 Overlay UI） | `pi-unity capture`（默认 game，内部 `vision_capture_async`） |
| 多帧观察 + 变化检测 + 指纹 + 网格 | `pi-unity observe`（见 playtest-observe.md）；`--interval` 已发 `interval_ms`。要改领域总超时用下面的显式 pipeline |
| 动作后连拍反馈 | `pi-unity pipeline vision_capture_after -p mode=short` |

> `observe` / `vision_capture_after` 只支持 `mode="game"`（自动要求 PlayMode 且依赖
> 渲染帧）；非 PlayMode 返回 `not_supported`，不要试图改 `mode="scene"` 绕过——
> SceneView 捕获请用 `pi-unity capture --mode scene`。

`pi-unity capture` 默认 `mode=game`，走 `vision_capture_async`。`--out` 发声明名 `path`。`--mode scene` 仍是同步 `vision_capture`。game 的领域 `timeout_ms` 等于 `--timeout`（默认 30000），传输等待也是这个数，不另加余量。

```bash
pi-unity capture --out Temp/Harness/vision/game.png
pi-unity pipeline vision_capture_async -p mode=game -p path=Temp/Harness/vision/game.png -p timeout_ms=60000
```

## 默认路径

三个 Unity profile 加一个 CLI presentation，缺省目录不互相合并：

- HarnessVision 单帧：`Temp/Harness/vision/<时间戳>.png`（相对 path 相对工程根）。
- observe：`Library/PiUnityHarness/playtest/observe`。
- `vision_capture_after`：`Library/PiUnityHarness/playtest/after`。
- Camera / isolation（`vision_capture_camera`、`vision_capture_isolated`）：`Temp/PiUnityHarness/Screenshots/`。相对 path 用 `Path.GetFullPath`，基准是 **cwd**，不是工程根。
- CLI 为防止大响应膨胀而剥离 Base64 时另写到 `Temp/PiUnityHarness/Captures/`。这只是 presentation，不是 Unity capture 默认目录。

响应只回路径，不内嵌图片；代理按需读取文件再交给视觉模型。

## 参数矩阵

| 入口 | 传输等待 | 运行时预算 | 键 |
|---|---|---|---|
| `pi-unity capture --mode game --timeout` | pipe `timeoutMs` = `--timeout` | 领域 `timeout_ms` = 同一个 `--timeout` | `--out` → `path` |
| `pi-unity capture --mode scene --timeout` | pipe `timeoutMs` = `--timeout` | 同步命令自己返回，不发 `timeout_ms` | `--out` → `path` |
| `pi-unity observe --timeout` | pipe `timeoutMs` = `--timeout` | 不注入；C# `total_timeout_ms` 默认 5000，`per_frame_timeout_ms` 默认 500 | `--interval` → `interval_ms` |
| `vision_capture_async` | 另计 | `-p timeout_ms=` | `-p path=` |
| `vision_observe` | 另计 | `-p total_timeout_ms=` | `-p interval_ms=` |

要改领域总超时时：

```bash
pi-unity pipeline vision_observe -p mode=game -p frames=4 -p interval_ms=200 -p overlay=none -p total_timeout_ms=30000
```

`unity_capture` / `unity_observe` 的工具字段仍是 `outPath` / `intervalMs`，argv 仍是 `--out` / `--interval`。CLI 再把它们映射到声明名。

## 使用规则

1. PlayMode 未开启时 `capture`（默认 game）会在异步路径失败。先
   `pi-unity pipeline editor_play`，再 `pi-unity capture`。SceneView 单张截图用 `--mode scene`，不要拿它当
   game 的 fallback。
2. 需要精确点击时：先 `pi-unity pipeline input_probe -p x=.. -p y=..` 确认命中对象，
   再 `input_click`；能用 `uitree_find` 的节点优先走 uitree。
3. 需要 UI/3D 可点击候选时用 `pi-unity pipeline vision_capture -p mode=scene -p annotate=true`，
   或 `vision_capture_async -p mode=game -p annotate=true`（额外收集 UGUI Selectable 与物理网格命中并画到 PNG，仅在需要交互候选时开启）。同步 `vision_capture` 的 game 仍会 `not_supported`。
4. 画面判断交给视觉模型时，附上 capture JSON（含尺寸与换算系数），不要只给
   图片路径。

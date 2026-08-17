# 截图 recipe：vision_capture / vision_observe

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
| 单张 SceneView 截图（编辑模式） | `vision_capture(mode="scene")` |
| 单张 GameView 截图（PlayMode，含 Overlay UI） | `vision_capture_gameview` 或 `vision_capture_async(mode="game")` |
| 需要 UI/3D 可点击候选 | `vision_capture(annotate=true)` 或 `vision_annotate` |
| 多帧观察 + 变化检测 + 指纹 + 网格 | `vision_observe`（见 playtest-observe.md） |
| 动作后连拍反馈 | `vision_capture_after`（见 playtest-observe.md） |

> `vision_observe` / `vision_capture_after` 只支持 `mode="game"`（`auto` 与 `game`
> 等价，均要求 PlayMode 且依赖渲染帧）；非 PlayMode 返回 `not_supported`，不要改
> `mode="scene"` 绕过——SceneView 捕获请用 `vision_capture` / `vision_capture_async`。

## 默认路径

- 单张截图默认写到 `Temp/Harness/vision/<时间戳>.png`。
- 跑测会话写到 `Library/PiUnityHarness/playtest/<session>/`（见 playtest skill）。
- 响应只回路径，不内嵌图片；代理按需读取文件再交给视觉模型。

## 使用规则

1. PlayMode 未开启时 `mode="game"` 会失败（EOF 捕获依赖渲染帧）；先
   `editor_play`。SceneView 单张截图用 `mode="scene"`，不要拿它当 game 的
   fallback。
2. 需要精确点击时：先 `input_probe(x,y)` 确认命中对象，再
   `input_click(x,y)`；能用 `uitree_find` 的节点优先走 uitree。
3. `annotate=true` 会额外收集 UGUI Selectable 与物理网格命中并画到 PNG，
   仅在需要交互候选时开启（有额外开销）。
4. 画面判断交给视觉模型时，附上 capture JSON（含尺寸与换算系数），不要只给
   图片路径。

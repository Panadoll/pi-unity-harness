# capture

单张视口截图。GameView 或 SceneView。只在视觉 mismatch 或自绘渲染 UI 需要核对时用，不要每步截图。

```bash
pi-unity capture --mode game
pi-unity capture --mode scene
pi-unity capture --mode scene --out "Temp/PiUnityHarness/Captures/scene_view.png"
pi-unity capture --json
```

返回 `status`、`path`、`width`、`height`、`bytes`。不要往终端倒 Base64。结果里的 `embed:false` 之类标记只是建议，宿主不强制阻止内联图片，别依赖它省 token。

# capture

单张视口截图。GameView 或 SceneView。

```bash
pi-unity capture --mode game
pi-unity capture --mode scene
pi-unity capture --mode scene --out "Temp/PiUnityHarness/Captures/scene_view.png"
pi-unity capture --json
```

返回 `status`、`path`、`width`、`height`、`bytes`。不要往终端倒 Base64。

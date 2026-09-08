# observe

PlayMode 下连续抓视口，去重，算 dHash，可选 0-1000 网格和标注。

```bash
pi-unity observe
pi-unity observe --frames 4 --interval 200
pi-unity observe --overlay none
pi-unity observe --overlay grid
```

默认 3 帧，间隔 160ms，叠加网格和标注。

只在 GUI 模式用。日常重构留在 `snapshot` + `eval` + `uitree_*`。GameView 要在 PlayMode。

图写到 `Temp/PiUnityHarness/Captures/`。CLI 只返回路径、`changedFrames`、`dHash`。不要把 Base64 倒进终端。

# observe

PlayMode 下连续抓视口，去重，算 dHash，可选 0-1000 网格和标注。

```bash
pi-unity observe
pi-unity observe --frames 4 --interval 200
pi-unity observe --overlay none
pi-unity observe --overlay grid
```

默认 3 帧，间隔 160ms，叠加网格和标注。mode 固定 `game`，命令本身是异步 `vision_observe`。

只在 GUI 模式用：视觉 mismatch / 自绘渲染 UI 对不上时。日常重构留在 `snapshot` + `eval` + `uitree_*`。GameView 要在 PlayMode。不要每步读图；结果里的 `embed:false` 之类标记只是建议，宿主不强制拦截内联图片。

## 路径 profile

observe 默认写到 `Library/PiUnityHarness/playtest/observe` 前缀；`vision_capture_after` 默认使用 `Library/PiUnityHarness/playtest/after`。这两条不并进 HarnessVision 的 `Temp/Harness/vision/`。CLI 剥离 Base64 时另存到 `Temp/PiUnityHarness/Captures/`，那只是 presentation，不是 observe 的输出目录。CLI 只返回路径、`changedFrames`、`dHash`，不要把 Base64 倒进终端。

CLI 摘要从 Pipeline 信封的 `value` 或 JSON `output` 中读取领域结果，再映射 `frames`、`fingerprint`、`captured_count`、`unique_count`。`changedFrames` 沿用现有唯一帧统计语义，不应仅按字段名理解为变化事件数；静止画面去重后仍可返回 1。`harness.vision.*` 的 `failed` / `unavailable` 是执行终态（超时是 `status=failed` + `error_type=timeout` + `timed_out:true`）：CLI 退出码为 1（领域 `error_type=usage` 为 2），JSON 的 `result` 是原样的领域结果。成功摘要保留 `status`、`timedOut`。普通业务 `status` 不是执行状态，不会因此失败。

## 参数映射

| CLI | 发出的 JSON 键 | C# `[CliArg]` |
|---|---|---|
| `--frames` | `frames` | `frames` |
| `--interval` | `interval_ms` | `interval_ms` |
| `--overlay` | `overlay` | `overlay` |
| `--timeout` | 传输 `timeoutMs`，不进参数 | `total_timeout_ms` 沿用 C# 默认 5000 |

`--interval` 发声明名 `interval_ms`，不再落回 C# 默认 160。CLI 不注入 `total_timeout_ms`。传输 `--timeout` 与命令自己的 `total_timeout_ms` / `per_frame_timeout_ms` 仍是两笔预算。

需要改领域总超时或逐帧超时时，走已有 pipeline：

```bash
pi-unity pipeline vision_observe -p mode=game -p frames=4 -p interval_ms=200 -p overlay=none -p total_timeout_ms=30000
```

# 跑测 recipe：observe → probe → click → capture_after 最小闭环（仅 gui 模式）

一步最小闭环的完整示例：观察、确认命中、点击、采集反馈。
默认 speed 模式不要用本 recipe；先看 `playtest-speed.md`。

## 前置

```text
gui 模式已声明（见 skills/pi-unity/references/observe.md）
editor_play                       # 进入 PlayMode（pi-unity pipeline editor_play）
```

## 1. 观察（多帧 + 指纹 + 网格）

```bash
pi-unity observe --frames 3 --interval 160 --overlay both
```

返回要点：

- `frames`：只含写盘的唯一帧路径（重复帧不写盘，长度 1..frames）。
- `vision`：带 0-1000 网格（+可选 UI/物理标注）的主图，喂给视觉模型。
- `timeline`：仅画面变化时非 null（FRAME i/n 序列图）。
- `fingerprint`：64 hex dHash，跨步计数防卡死。
- `changed` / `unique_count` / `captured_count` / `deduplicated_count` / `timed_out`。

## 2. 并行取 UI 上下文

```bash
pi-unity pipeline uitree_roots
pi-unity pipeline uitree_snapshot -p interactive_only=true
```

## 3. 确认命中（像素动作必须）

```bash
pi-unity pipeline input_probe -p x=523 -p y=236
```

返回 `would_hit` / `would_hit_kind` / `blocked_by`；命中对象不是目标时
重新选点，不要盲点。

## 4. 执行动作

```bash
pi-unity pipeline input_click -p x=523 -p y=236
```

## 5. 动作后连拍（普通翻页用 short，快速反馈用 burst）

```bash
pi-unity pipeline vision_capture_after -p mode=short -p path_prefix=Library/PiUnityHarness/playtest/smoke/step_001
```

返回 `sheet`（带毫秒时间戳的序列图）作为下一步的 pending_feedback。

## 6. 记忆与 checkpoint

把 observation/reasoning/action/expected_result 追加到 `events.jsonl`，
更新 working_memory；每 12 步或上下文超限时压缩进 `memory.json`（见
`skills/pi-unity/references/observe.md`；先对账 `session.json` 的
`last_compressed_step`）。
# 开发逐步 recipe：speed 模式（默认）

日常开发验证的默认路径。结构化白盒，不连拍、不叠网格、不调 VLM。
几十到几百毫秒一步。

## 每步

```bash
pi-unity snapshot                        # 场景层级 + 选择 + 近期日志
pi-unity pipeline uitree_snapshot -p interactive_only=true   # 可交互 UI 树

# 用 node_ref / 文本定位目标后执行动作：
# - 能 uitree 操作 → pi-unity pipeline uitree_click -p node_ref=...
# - 否则先确认命中再点击
pi-unity pipeline input_probe -p x=523 -p y=236     # 确认命中对象
pi-unity pipeline input_click -p x=523 -p y=236     # 点击
# 键盘/滚轮/拖拽 → pi-unity pipeline input_press_key / input_scroll / input_drag

# 再拉 uitree / console 对照 expected_result：
pi-unity snapshot --log-level all
pi-unity pipeline uitree_roots
```

每步之后对照 `expected_result`；树对不上肉眼时，升一次 gui 步
（见 `playtest-observe.md`），验完回来。

## 规则

- 唯一允许的截图：`pi-unity capture`（单张、无 overlay、无 fingerprint）。
- 不写 working_memory / memory.json（会话内短笔记即可）。
- 不用 `pi-unity observe` / `vision_capture_after`。
- 状态没变但画面可能变了（卡死、Toast、过场）→ 这正是升 gui 的信号，
  不要继续盲点。
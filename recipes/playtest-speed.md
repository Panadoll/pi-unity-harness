# 开发逐步 recipe：speed 模式（默认）

日常开发验证的默认路径。结构化白盒，不连拍、不叠网格、不调 VLM。
几十到几百毫秒一步。

## 每步

```text
1. unity_snapshot                 # 场景层级 + 选择 + 近期日志
   uitree_snapshot(interactive_only=true)   # 可交互 UI 树
2. 用 node_ref / 文本定位目标
3. 动作：
   - 能 uitree 操作 → uitree_click / uitree_text 等
   - 否则 input_probe(x,y) 确认命中 → input_click
   - 键盘/滚轮/拖拽 → input_press_key / input_scroll / input_drag
4. 再拉 uitree / console，对照 expected_result
5. 树对不上肉眼时：升一次 gui 步（见 playtest-observe.md），验完回来
```

## 规则

- 唯一允许的截图：`vision_capture` / `vision_capture_async` 单张、无
  overlay、无 fingerprint。
- 不写 working_memory / memory.json（会话内短笔记即可）。
- 不用 `vision_observe` / `vision_capture_after`。
- 状态没变但画面可能变了（卡死、Toast、过场）→ 这正是升 gui 的信号，
  不要继续盲点。

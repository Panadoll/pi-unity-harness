---
name: unity-playtest-loop
description: >
  在 Unity Editor 里执行自主游戏测试：默认 speed 模式（uitree/快照/日志，几十毫秒
  一步），需要验证 GUI 观感/坐标/动画反馈时临时升 gui 模式（vision_observe 多帧
  观察 → 决策 → input/uitree 动作 → vision_capture_after 反馈 → 双记忆维护与压缩）。
  借鉴 game_test_agent 的感知原语与 Agent 侧协议，白盒化到 pi-unity-harness。
---

# unity-playtest-loop

自主游戏测试闭环。源自 `game_test_agent` 的
`感知 → 决策 → 动作 → 反馈 → 记忆压缩` 循环，但 Unity 侧全部走白盒通道
（uitree / input_probe / 场景快照 / console），不靠纯像素猜测。

## 模式

**默认 `speed`**：日常开发走结构化白盒，不连拍、不叠网格、不调 VLM、
不做 18 字段记忆。**只有明确信号才升 `gui`**，验完当前问题立即降回 `speed`。
禁止「开着 gui 写业务」。

升 gui 的信号（skill 不得自行升级）：

- 用户说「看一下界面 / 那个按钮看起来对不对 / 验 GUI / 截图确认」
- speed 下 uitree 找不到可交互节点（自定义绘制、无 EventSystem）
- speed 动作后结构化状态没变，需要确认是否画面卡死或短暂反馈
- 过场、抽卡、伤害数字、Toast 等「树上看不到」的视觉反馈

## speed 模式（默认，不调用新视觉原语）

```
1. unity_snapshot + uitree_snapshot(interactive_only=true)
2. 用 node_ref / 文本定位目标
3. 能 uitree 操作就 uitree；否则 input_probe → input_click
4. 再拉 uitree / console，对照 expected_result
5. 只有树对不上肉眼时，升一次 gui 步，然后回来
```

允许的唯一截图：`vision_capture` / `vision_capture_async` 单张、无 overlay、
无 fingerprint。详细逐步见 `recipes/playtest-speed.md`。

## gui 模式前置

- 已连接 Unity Editor（`unity_ping` ok，`unity_status` ready）。
- 目标游戏场景已就绪；进入 PlayMode：`editor_play`（observe 的 `mode=game`
  非 PlayMode 直接失败，不会静默落到 SceneView）。
- 会话目录：`Library/PiUnityHarness/playtest/<session_id>/`（session_id 用
  `YYYYMMDD_HHMMSS` 生成；不用 Temp，Editor 退出会清）。

## gui 每步闭环（严格执行）

1. **Observe**：`vision_observe(mode="game", frames=3, interval_ms=160,
   overlay="both", path_prefix="Library/PiUnityHarness/playtest/<sid>/step_NNN")`
   —— 3 帧捕获、内存像素去重（重复帧不写盘）、dHash 亮度指纹、0-1000 网格叠图、
   变化时才合成 timeline 序列图；单帧 500ms 硬超时，`timed_out=true` 时先
   取消 Editor 最小化 / 保持 GameView 可见再重试。
2. **并行上下文**（只取需要的，控制 token）：
   `uitree_roots` + `uitree_snapshot`（有 UI 时），`unity_snapshot(logLevel="error")`。
3. **核对反馈**：用当前画面对照 working_memory.expected_result，写
   `last_action_result`（成功/失败/不确定 + 依据）。
4. **卡死检查**：fingerprint 在本会话出现 ≥ 3 次 → 禁止同坐标连点，强制换策略：
   读 console（`console_get_logs`）、查 `isPlaying` / `timeScale`、走返回路线。
5. **决策**：产出决策 JSON（见下），动作按优先级选择。
6. **执行**：动作前 `input_probe(x,y)` 确认命中（像素动作），再执行。
7. **反馈采集**：按动作类型选 `vision_capture_after`，结果作为下一步上下文。
8. **记忆维护**：每步更新 working_memory 并追加 events.jsonl。
9. **checkpoint**：每步写 `session.json`。

## 决策 JSON（gui 模式每步必须完整返回）

```json
{
  "observation": "当前画面与状态描述",
  "reasoning": "为什么推进测试目标",
  "action": {
    "type": "uitree_click|input_click|input_drag|input_key|wait|finish",
    "x": 0, "y": 0,
    "capture_after": "none|short|burst"
  },
  "issues": [{"severity": "P0|P1|P2|P3", "title": "", "reproduction": ""}],
  "done": false,
  "context": {
    "current_phase": "", "short_term_goal": "", "plan": [],
    "progress": "", "last_action_result": "", "expected_result": "",
    "open_question": "", "discoveries": [], "failed_attempts": []
  }
}
```

动作优先级（白盒优势，必须遵守）：

1. `uitree_find` 能定位的可交互节点 → `uitree_click`（不靠坐标幻觉）。
2. 否则 `input_probe(x,y)` 确认命中后再 `input_click`。
3. 自定义绘制 / 无 EventSystem 的画面 → 网格坐标 + `input_click`。
4. `capture_after=burst`：过场、抽卡、伤害数字、Toast、摇杆位移等快速反馈。
5. `capture_after=short`：普通翻页 / 开关面板。
6. 指纹重复 ≥ 3：禁止同坐标再点，改路线 / 返回 / 读 console / 查 hierarchy。

## 记忆协议（仅 gui 模式）

### 工作记忆（逐步更新，跨步保持短期目标）

| 字段 | 作用 |
|------|------|
| `long_term_goal` | 会话级目标，不每步改 |
| `current_phase` | 当前流程阶段 |
| `short_term_goal` | 跨多步里程碑，完成/受阻才换 |
| `plan` | 2-5 条有序意图，不是死板坐标脚本 |
| `progress` | 短期目标推进到哪 |
| `last_action_result` | 对照 expected_result：成功/失败/不确定 + 依据 |
| `expected_result` | 本动作后必须可验证的反馈 |
| `open_question` | 下一步最该确认的问题 |
| `discoveries` / `failed_attempts` | 本压缩周期内新事实 / 已证伪尝试 |

### 长期记忆（18 槽，压缩后清空 discoveries/failed_attempts）

`summary`、`covered`、`blocked`、`key_facts`、`npc_guidance`、`spatial_map`、
`quests`、`resources`、`interaction_rules`、`hazards`、`game_state_model`、
`routes`、`constraints`、`failed_attempts`、`unresolved`、`next_goals`、
`updated_at_step`（共 17 个语义槽 + 时间戳）。

Unity 语义适配：

| 槽 | Unity 含义 |
|----|-----------|
| `spatial_map` | 场景路径 / 地标 / 传送点（可带 hierarchy path） |
| `interaction_rules` | UI 规则 + Input 映射（哪个按钮走 uitree，哪个必须像素点） |
| `game_state_model` | PlayMode 状态、当前 scene、加载中、timeScale |
| `hazards` | 会弹 modal / 会进战斗 / 会丢输入焦点的区域 |
| `routes` | 已验证的菜单/关卡路径 |

硬约束：只保留画面或动作反馈确认过的事实；合并重复；删流水账和猜测。

### 压缩触发（对账是强制协议，不是建议）

- **每步先读 `session.json`**：若 `step - last_compressed_step >= 12`，
  或 working_memory + 最近 8 步的 JSON 序列化 ≥ 14000 字符，**必须先压缩
  再 observe**。漏做视为协议违规。
- 压缩用一次 LLM 调用完成，压缩失败沿用旧记忆。
- 压缩后 history 截断到最近 8 步，写回 `last_compressed_step = step`。

## 会话文件

```
Library/PiUnityHarness/playtest/<session_id>/
  session.json      # 每步 checkpoint：目标、阶段、step、last_compressed_step、事件计数
  memory.json       # 长期记忆（压缩后覆盖写）
  events.jsonl      # 追加式事件流：thinking/stream/step/issue/memory/error
  step_NNN_frame_MM.png     # 原始帧（仅唯一帧写盘）
  step_NNN_vision.png       # 网格/标注叠图层（喂给 VLM 的主图）
  step_NNN_timeline.jpg     # 画面变化时的时间序列
  step_NNN_after_short|burst_sheet.jpg  # 动作后连拍序列图
```

## 白盒卡死信号（叠加在指纹之上）

- `isPlaying == false`：跑测目标丢失，停止并报告。
- `timeScale == 0` 且画面静止：可能是暂停菜单——observe 必须仍能工作
  （间隔用 realtime），先尝试恢复或返回，**不**当作画面卡死。
- console 同栈错误重复出现：`console_get_logs` 记录为 issue。
- `bridge_yielding` 或管道异常：先 `unity_status` / `unity_ping` 再继续。

## 结束

- `done: true` 或用户停止时：用长期记忆生成摘要写入 `summary.md`，停止
  PlayMode，保留会话目录作为证据。

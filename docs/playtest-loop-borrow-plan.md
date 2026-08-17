# 从 game_test_agent 借鉴核心跑测循环到 pi-unity-harness

来源：`F:\Projects-Test\game_test_agent-master` 的 `_run()` 闭环。  
目标：只借感知原语和 Agent 侧协议，不把黑盒 Python runner 搬进 UPM。

审查：第一次对照 `HarnessVision` / RuntimeCapture / 协程泵 / Annotator / Probe / pipeline / asmdef / TS。第二次核对出 A1–A3 失实与 B1–B5 / C / D 缺口。下文已吸收——**双模式默认速度**；**observe 独立捕获链 + realtime/硬超时**；**Phase 0 补测试宿主**。

## 结论

`game_test_agent` 的价值不在「再写一个 Python 黑盒 Agent」，而在一套已经跑通的 **感知 → 决策 → 动作 → 反馈 → 记忆压缩** 闭环。`pi-unity-harness` 已经是白盒 Unity 桥（GameView 截图、Input 注入、UI 树、场景快照、日志），缺的是这条闭环的 **感知原语** 和 **Agent 侧协议**。

不要把 `AgentRunner._run()` 搬进 UPM 包。闭环放在外部 Agent（skill + session 文件），Unity 只提供确定性的观察/动作原语。

**默认是速度模式，不是视觉跑测。** 开发阶段走结构化白盒（`uitree_*` / `unity_snapshot` / `input_*` / eval），不连拍、不叠网格、不调 VLM、不做 18 字段记忆。只有「需要验证 GUI 实际长什么样、点到哪里、动画/反馈对不对」时才切 GUI 模式，启用 observe / burst / 网格 / 指纹 / 记忆压缩。

两种模式共用同一套 Input / UiTree / Vision 积木；差别在 **Agent skill 选哪些命令、每步允许多贵**，不在 C# 里做全局开关污染普通截图。

```
game_test_agent（黑盒 Windows）          pi-unity-harness（白盒 Editor）
HWND PrintWindow 截图          →        vision_capture_async (WaitForEndOfFrame)
0-1000 网格叠图                →        新增 overlay=grid（保留现有 UI/物理标注）
dHash 指纹 / 卡死检测          →        新增 fingerprint + state_visits
3 帧去重 + 时间序列合成        →        新增 vision_observe
Win32 鼠标 / 虚拟摇杆          →        已有 input_* + uitree_*（摇杆 CV 后置）
动作后 short/burst 连拍        →        新增 vision_capture_after
工作记忆 + 18 字段长期记忆     →        skill + Library/PiUnityHarness/playtest/<session>/
单次视觉分析                   →        已有 vision_analyze_*（HttpClient 单次，无 SSE）
流式输出                       →        外部 Agent 负责，C# 分析器不做流式
```

---

## 双模式：速度（默认）vs GUI 验证（按需）

| | 速度模式 `speed` | GUI 验证模式 `gui` |
|--|------------------|-------------------|
| **何时** | 日常开发：改逻辑、改 UI 结构、点按钮、看日志、跑编译 | 验观感、验坐标、验动画/Toast/过场、画面是否卡死 |
| **观察** | `uitree_snapshot` / `uitree_find` + `unity_snapshot`（日志/层级） | `vision_observe`（3 帧 + dHash + 可选网格/标注） |
| **决策** | 主模型读 JSON 树，不看图 | 主模型 + 叠图层 / 时间序列图 |
| **动作** | `uitree_*` 优先，否则 `input_probe` → `input_click` | 同左；自定义绘制才用 0–1000 网格坐标 |
| **动作后** | 再拉一次 uitree / snapshot，必要时单张 `vision_capture` | `vision_capture_after`（`short` / `burst`） |
| **记忆** | 不必 18 字段；会话内短笔记即可 | 工作记忆 + 每 12 步压缩长期记忆 |
| **墙钟/步** | 几十到几百毫秒（结构化 JSON） | observe ≈ 0.5s+（EOF 可控时）；burst ≈ 2s **+ 帧率扰动** + VLM |
| **默认** | **是** | 否，skill 显式声明才进 |

触发 GUI 模式的信号（写进 skill，Agent 不得自行升级）：

- 用户说「看一下界面 / 点那个按钮看起来对不对 / 验 GUI / 截图确认」
- 速度模式 uitree 找不到可交互节点（自定义绘制、无 EventSystem）
- 速度模式动作后结构化状态没变，需要确认是不是画面卡死或短暂反馈
- 过场、抽卡、伤害数字、Toast 等「树上看不到」的视觉反馈

切回速度模式：GUI 验完当前问题，下一步开发继续走树。禁止「开着 gui 写业务」。

### 速度模式逐步（不调用新视觉原语）

```
1. unity_snapshot + uitree_snapshot(interactive_only)
2. 用 node_ref / 文本定位目标
3. 能 uitree 操作就 uitree；否则 input_probe → input_click
4. 再拉 uitree / console，对照 expected_result
5. 只有树对不上肉眼时，升一次 gui 步，然后回来
```

速度模式允许的唯一截图：`vision_capture` / `vision_capture_async` **单张、无 overlay、无 fingerprint**。这是现有热路径，保持不变。

### GUI 模式逐步（才用本文后半的闭环）

```
1. vision_observe (3 帧 + dHash + overlay)
2. 可选并行 uitree + snapshot（白盒仍可用）
3. 对照 expected_result；fingerprint 重复 ≥ 3 换路线
4. VLM / 主模型 JSON 决策
5. probe → 执行
6. vision_capture_after(short|burst)
7. 每 12 步或上下文超限压缩 memory.json
```

### 实现落点

- **C# 不加** `playtest_mode=speed|gui` 全局开关。普通 `vision_capture*` 永远是速度路径。
- skill 头声明当前模式；`unity-playtest-loop` 默认 `speed`。
- GUI 模式第一步：确认 PlayMode（`editor_play` / 现有 play 命令）。`vision_observe(mode=game)` 非 PlayMode 直接失败，**不**静默落到 SceneView。
- `vision_observe` / `vision_capture_after` 只在 `gui` 被 skill 或用户点名时出现。
- 发现测试、现有 capture 测试、TS 扩展 shortcut 列表：**不**把 observe/after 注册成默认高频 shortcut。

---

## 改动规模与对普通截图的影响

**结论：普通 `vision_capture` / `vision_capture_async` 零额外负担。** 连拍、网格、指纹、burst 全部做成新命令；现有截图路径不改默认行为、不默认写第二张图、不默认算 hash。

### 代码量（Phase 1 + 2）

| 范围 | 大约 | 说明 |
|------|------|------|
| 新增 C#（独立 Observe/After 捕获链 + 网格 + dHash + JSON） | 800–1200 行 | **不**循环调用 `CaptureJsonAsync`；新纹理生命周期 |
| 新增 pipeline wrapper + 发现测试 | 80–120 行 | 仿 `PiVisionPipelineCommands` |
| Editor/PlayMode 单测 | 250–400 行 | 网格、dHash 钉死算法、去重长度、EOF 超时、timeScale=0 |
| 改现有 `HarnessVision.CaptureJson*` / `RuntimeCapture` | **0 行行为变化** | 单张热路径不动；observe 另开 runner |
| skill + recipe（Markdown） | 3–4 个文件 | 含 speed / gui / session 对账 |
| 记忆压缩 / AgentRunner | **不写进 C#** | session.json 强制对账 |

不是大重构。不动 Input、UiTree、Bridge、native pipe。风险面只在 Vision 新 API。

### 普通截图为什么不会变慢

现有一次 GameView 捕获的成本是：等 `WaitForEndOfFrame` + 读 framebuffer + 写一张 PNG。这已经是热路径。

| 能力 | 若绑进每次 capture | 本方案 |
|------|-------------------|--------|
| 3 帧观察 | 约 3× EOF + 间隔 + 最多 3 次 EncodeToPNG | 仅 `vision_observe`；内存比像素，只编码唯一帧 |
| 网格叠图 | 多一次 CPU 画线 + 多写一张 PNG | 仅 observe 且 `overlay` 打开 |
| dHash | Blit 到 17×16 RT + 行邻比较 | 仅 observe / after 返回；不进普通 capture |
| burst 24 帧 | 2s 量级 **+ 每帧 EncodeToPNG 扰动帧率** | 仅 `vision_capture_after`；单帧硬超时 |
| 时间序列拼图 | 额外 JPEG/PNG 合成 | 仅 `changed==true`（与像素 diff 同源） |

因此：

- `unity_pipeline({ command: "vision_capture", ... })` 和现在一样。
- `annotate=true` 的现有路径也不变（仍是 UI/物理标记，不是 0–1000 网格）。
- 速度模式根本不调用 `vision_observe` / `vision_capture_after`；只有 GUI 验证 skill 路径才调。

若以后有人想在普通 capture 上顺带指纹：只加 **可选** `fingerprint=false` 默认关。即使打开，hash 相对读 framebuffer 可忽略；真正贵的是多帧和叠图写盘。

### 跑测路径的成本（可接受、可关）

单步 observe：3 次 EOF + realtime 间隔 + 内存像素 diff + 最多 3 次 EncodeToPNG + 可选 grid/timeline。动作后 short ≈ 1s，burst 在 60fps 约 2s **且会把游戏帧率打到约 25fps**（720p EncodeToPNG 约 20–50ms/帧，与 Runtime 同主线程）。依赖精确帧时序的连招测试应避开 burst，或接受扰动。

EOF 在 Editor 最小化 / GameView 不呈现时可能根本不触发，因此 observe/burst **必须有硬超时**，不能假设「约 0.5s / 约 2s」。

---

## 源循环（已核对代码）

实现集中在 `F:\Projects-Test\game_test_agent-master\game_test_agent\agent.py`。

每步 `_run()`：

1. `_capture_frames`：连拍 3 帧，间隔 160ms
2. SHA256 精确去重（`_deduplicate_frame`）
3. 帧有差异才合成 `FRAME i/n` 时间序列图（`_make_timeline`）
4. 最新帧做 **dHash**（17×16 灰度，行内相邻比较，64 hex）记入 `state_visits`；重复 > 2 强制换路线
5. 最新帧叠加 **0–1000 坐标网格**（每 100 一格）再送给 VLM
6. 流式 Chat Completions，解析单一 JSON 决策
7. 执行 `click|drag|swipe|joystick_move|wait|finish`（0–1000 → 客户区像素）
8. `_capture_after_action`：`short`=8 帧/120ms，`burst`=24 帧/80ms，合成带时间戳的 sequence sheet，作为 **下一步** 的 `pending_feedback`
9. 每 12 步或上下文 JSON ≥ 14000 字符：LLM 把工作记忆压进 18 个长期字段，history 截到最近 8 步

### 工作记忆（逐步更新，跨步保持短期目标）

| 字段 | 作用 |
|------|------|
| `long_term_goal` | 会话级目标，不每步改 |
| `current_phase` | 当前流程阶段 |
| `short_term_goal` | 跨多步里程碑，完成/受阻才换 |
| `plan` | 2–5 条有序意图，不是死板坐标脚本 |
| `progress` | 短期目标推进到哪 |
| `last_action_result` | 对照 `expected_result`：成功/失败/不确定 + 画面依据 |
| `expected_result` | 本动作后必须可验证的反馈 |
| `open_question` | 下一步最该确认的问题 |
| `discoveries` / `failed_attempts` | 本压缩周期内的新事实 / 已证伪尝试 |

### 长期记忆（18 字段，压缩后清空 discoveries）

`summary`、`covered`、`blocked`、`key_facts`、`npc_guidance`、`spatial_map`、`quests`、`resources`、`interaction_rules`、`hazards`、`game_state_model`、`routes`、`constraints`、`failed_attempts`、`unresolved`、`next_goals`、`updated_at_step`（加 `summary` 共 17 个语义槽 + 时间戳；用户所说 18 字段即此 schema）。

硬约束：只保留画面或动作反馈确认过的事实；合并重复；删流水账和猜测。

---

## 现状差距（pi-unity-harness）

已有、可直接当积木：

- `vision_capture` / `vision_capture_async` / `vision_capture_gameview`：PlayMode 等 `WaitForEndOfFrame`，返回 `screenshot_to_gameview` 换算。**每次请求新建协程，finally 里 `Destroy` 纹理，对外只有 JSON + PNG 文件**——不能当 observe 的帧缓冲复用。
- `vision_annotate` + `PiVisionAnnotator`：UGUI 可点击目标 + 物理网格命中（比纯视觉网格更准）。这里的 grid 是物理采样点，不是 0–1000 坐标尺。
- `vision_build_analysis_request` / `vision_analyze_*`：**单次** OpenAI 兼容请求（`HttpClient.SendAsync`，无 SSE/增量）。流式不在 C# 里。
- `input_click` / `input_drag` / `input_probe` / `input_sequence`：GameView 左上角像素
- `uitree_snapshot` / `uitree_find`：结构化 UI，不必全靠 VLM 点坐标
- `unity_snapshot`：场景层级 + 选择 + 近期日志

明确没有（或不可当积木）：

- `action-timeline-v1`：只出现在 `docs/protocol.md` / MCP `unity_timeline` 声明。`com.pi.unity-harness` C# **没有** ActionTimeline 实现，不能复用做跑测审计。
- 多帧内存缓冲、去重、时间序列拼图
- 感知哈希 / 画面卡死计数
- 0–1000 网格叠图
- 动作后 short/burst 连拍
- 工作记忆 / 长期记忆协议
- 把「观察→决策→执行→反馈」写成 skill 的跑测循环

README 写了 `skills/unity-harness-mcp/SKILL.md` 和 `recipes/vision-capture.md`，仓库里这两份正文目前不存在（只有 `.grok/skills` 指针）。

---

## 分层：借什么、放哪、别抄什么

### 放进 C# Vision（确定性、可测、主线程友好）

这些必须在 Editor 进程里做，Agent 用 pipeline 调用即可。

| 原语 | 对应源实现 | 建议命令 |
|------|------------|----------|
| 3 帧观察 + 去重 + 可选时间序列 | `_capture_frames` | `vision_observe` |
| dHash 指纹 | `_fingerprint` | 观察结果里带 `fingerprint`；也可单独 `vision_fingerprint` |
| 0–1000 网格叠图 | `_make_coordinate_grid` | `overlay=grid\|annotations\|both\|none` |
| 动作后连拍 + sheet | `_capture_after_action` | `vision_capture_after`（`none\|short\|burst`） |
| 点击/拖拽标注图 | `_annotate_target` | 观察/执行后可选 `annotate_action` |

建议 API 形状：

```
vision_observe({
  mode: "game",             // game 必须 PlayMode；非 PlayMode 失败，不 fallback SceneView
  frames: 3,
  interval_ms: 160,         // EOF 之后再补 realtime 间隔（见下）
  overlay: "both",
  path_prefix: "Library/PiUnityHarness/playtest/<id>/step_012",
  per_frame_timeout_ms: 500,
  total_timeout_ms: 5000
})
→ {
  schema: "harness.vision.observe.v1",
  frames: [...],            // 只含写盘的唯一帧路径，长度 1..frames
  latest, vision,
  timeline: null | path,    // 当且仅当 changed==true（写盘失败除外）
  fingerprint: "64hex",     // 最后一帧唯一画面的 dHash
  changed: bool,            // 与 timeline 同源：内存像素 diff
  captured_count: 3,        // 实际发起的捕获次数
  unique_count: 1,          // == frames.length
  timed_out: false,
  gameview_size, screenshot_to_gameview, captured_at_utc
}

vision_capture_after({
  mode: "short" | "burst",  // 8@120ms / 24@80ms
  path_prefix: "...",
  per_frame_timeout_ms: 500,
  total_timeout_ms: 15000
})
→ { sheet, frames, timings_ms, fingerprint, timed_out, unique_count }
```

#### 独立捕获链（A1：不能循环 CaptureJsonAsync）

`HarnessVisionRuntimeCapture.CaptureCoroutine` 每请求：`WaitForEndOfFrame` → `CaptureScreenshotAsTexture` → `EncodeToPNG` 写盘 → `Destroy(screenshot)`。`CaptureJsonAsync` 只是 `while (!pending.IsDone) yield return null`。因此 observe **必须新建** `HarnessVisionRuntimeBurst`（或同等 runner）：

| 步骤 | 做法 |
|------|------|
| 取帧 | 同一 MonoBehaviour 上连续协程：EOF → 拿到 Texture2D，**先不 Destroy** |
| 间隔 | EOF 返回后，用 `Time.realtimeSinceStartup` 补齐 `interval_ms`（禁止 `WaitForSeconds`） |
| 去重 | 内存 `Color32[]` 逐像素相等 → 视为重复；重复帧 **不写盘、立刻 Destroy** |
| changed / timeline | 任两帧像素不等 → `changed=true`，只在此时合成 timeline |
| 指纹 | 对最后一帧唯一画面 `Graphics.Blit` 到 17×16 RT（双线性）再 dHash |
| 写盘 | 仅唯一帧 `EncodeToPNG`；observe 最多 3 张全分辨率常驻内存（1280×720 RGB24 ≈ 8MB） |
| burst | 全分辨率纹理编码后立即 Destroy，内存里只留 160×90 比较缓冲 + 路径列表（避免 24×全图 ≈ 66MB） |
| finally | 无论成功/超时/异常，Destroy 全部 Texture2D / 释放 RT |

可复用的只有：`ResolvePath`、`BuildCaptureJson` 字段、`WaitForEndOfFrame` + `ScreenCapture.CaptureScreenshotAsTexture`、现有 `ResizeTexture`（Blit）。**不**复用「单次请求写盘即毁」的控制流。

#### EOF / realtime / 超时（B1–B3）

- `mode=game` 且 `!Application.isPlaying`：`status=failed`，`error_type=not_supported`，文案写明先进 PlayMode。`mode=auto` 的 observe **同样不**落到 SceneView（跑测看错窗口比报错更糟）。`mode=scene` 仅显式指定时允许，且不做 EOF 连拍。
- 单帧语义：`WaitForEndOfFrame` **加上**「至少 `interval_ms` 墙钟」。实现：记录 `due = realtime + interval_ms`；EOF 回来后 `while (realtime < due) yield return null`。
- 单帧硬超时默认 500ms（从开始等 EOF 起算）。超时：该帧失败，整个 observe/burst 停止，返回已拿到的帧 + `timed_out=true` + 可操作错误（「EOF 未在 500ms 内触发：取消最小化、保持 GameView 可见」）。
- 整次 `total_timeout_ms`：observe 默认 5s，burst 默认 15s。
- **禁止** `WaitForSeconds`。暂停菜单 `timeScale=0` 是跑测常态，间隔必须 realtime。`timeScale==0` 在 Phase 3 是游戏状态信号（暂停 UI），**不是** observe 卡死判据。

#### dHash 钉死（B4）

- 算法：`Graphics.Blit` → 17×16 ARGB32 RT（默认双线性）→ `ReadPixels` → 亮度 `0.299R+0.587G+0.114B` → 每行 16 次相邻比较 → 256bit / 64 hex。
- **不用** `Texture2D.Resize`（box filter，与源 PIL LANCZOS 不一致）。也不追求与 PIL 逐 bit 相同，只要求本实现可复现。
- Phase 1 跨步「重复」用 **精确指纹字符串**。测试钉：纯色图多次哈希相同；同一 RenderTexture 内容稳定。抗锯齿/光标抖动导致误报时，再加 Hamming≤4，不进 Phase 1 默认。

#### changed / 去重语义（C1 / C2）

对齐源项目顺序：先精确去重（省存储）再像素 diff（决定 timeline）。内存路径下二者合一：

- 去重依据：全分辨率 `Color32` 逐字节相等（不是 dHash，不是 PNG sha256）。
- 重复帧不写盘，故测试断言 `unique_count == frames.Length`，`1 <= unique_count <= captured_count`。
- `changed == (unique_count > 1)`。
- `timeline != null` 当且仅当 `changed` 且 sheet 写盘成功；否则 `timeline=null` 且 `warnings` 说明。禁止 `changed=true` 且无 warning 的 `timeline=null`。
- `fingerprint` 只用于跨步 `state_visits`，不参与本步 `changed`。

#### 证据目录（C3）

默认 `Library/PiUnityHarness/playtest/<session>/`，**不**用 `Temp/`（Editor 退出会清）。`Library/` 通常 gitignore，但本机会话间保留。skill 允许把 `path_prefix` 指到项目外持久目录。Phase 1 即按此默认，不等 Phase 4。

### 放在 Agent 侧（skill + session 文件，不进 UPM）

记忆压缩、流式 VLM、暂停/续跑、HTML 报告都属于 **编码代理的工作记忆**，不是 Editor 能力。

建议产物：

- `pi-unity-harness/skills/unity-playtest-loop/SKILL.md`
- `pi-unity-harness/recipes/playtest-observe.md`
- 会话目录 `Library/PiUnityHarness/playtest/<session_id>/`（不要 Temp）
  - `session.json` / `memory.json` / `events.jsonl`
  - `step_NNN_*.png` / `*_sheet.jpg`

每步 Agent 协议（从源 JSON 决策改一版，加上白盒通道）：

```json
{
  "observation": "...",
  "reasoning": "...",
  "action": {
    "type": "uitree_click|input_click|input_drag|input_key|wait|finish",
    "x": 0, "y": 0,
    "capture_after": "none|short|burst"
  },
  "issues": [{ "severity": "P0|P1|P2|P3", "title": "...", "reproduction": "..." }],
  "done": false,
  "context": { "current_phase": "...", "short_term_goal": "...", "plan": [], "...": "..." }
}
```

动作优先级（Harness 相对黑盒的优势，必须写进 skill）：

1. 能 `uitree_find` 到的可交互节点 → `uitree` 点击/设值（不靠坐标幻觉）
2. 否则 `input_probe(x,y)` 确认命中后再 `input_click`
3. 自定义绘制 / 无 EventSystem 的画面 → 网格坐标 + `input_*`
4. `capture_after=burst`：过场、抽卡、伤害数字、Toast、摇杆位移
5. `capture_after=short`：普通翻页 / 开关面板
6. 指纹重复 ≥ 3：禁止同坐标再点，改路线、按返回、或读 console/hierarchy

长期记忆字段建议 **沿用 18 槽，改几个 Unity 语义**，不要另起一套：

| 源字段 | Unity 适配 |
|--------|------------|
| `spatial_map` | 场景路径 / 地标 / 传送点（可带 hierarchy path） |
| `interaction_rules` | UI 规则 + Input 映射（哪个按钮走 uitree，哪个必须像素点） |
| `game_state_model` | PlayMode 状态、当前 scene、加载中、timeScale |
| `hazards` | 会弹 modal / 会进战斗 / 会丢输入焦点的区域 |
| `routes` | 已验证的菜单/关卡路径 |
| 新增可选 | `console_signatures`、`scene_refs`（不替代 18 槽，作附录） |

压缩触发保持源参数：每 12 步或 payload ≥ 14k 字符；压缩失败沿用旧记忆。

**不能只靠 skill 自觉（D2）。** `session.json` 必含 `step`、`last_compressed_step`。GUI 每步先读文件：若 `step - last_compressed_step >= 12` 或工作记忆 JSON ≥ 14k，必须先压缩再 observe。漏做视为协议违规。C# 不催，但对账字段由 skill 强制读写。

### 明确不要搬

| 源能力 | 原因 |
|--------|------|
| HWND `PrintWindow` + Win32 鼠标 | Harness 已有 GameView 捕获和 Input System 注入 |
| 独立 Python `AgentRunner` 常驻线程 | 违反「桥是通道、Agent 在外面」 |
| 虚拟摇杆 + 模板匹配闭环（`vision.py` / `joystick_control.py`） | 只对屏幕摇杆手游有用；Input System 轴/键盘更稳。需要时再做 recipe |
| `MouseBusyError` 让出真实鼠标 | 合成输入不抢系统光标 |
| 把 0–1000 做成唯一坐标系 | 与现有 `top_left_game_view` + `screenshot_to_gameview` 冲突 |
| 在 C# 里再做一套流式 LLM 客户端 | 现有分析器是单次 `SendAsync`；流式只放外部 Agent |

---

## 推荐的目标闭环

默认走速度模式。GUI 闭环是同一 skill 的第二条路径，不是默认逐步。

```
Agent skill: unity-playtest-loop   mode = speed | gui   （默认 speed）

speed:
  1. uitree_snapshot + unity_snapshot
  2. 对照 expected_result
  3. uitree 操作，否则 probe → input_*
  4. 再拉树 / 日志；不连拍、不压 18 字段记忆
  5. 树解释不了画面时，本步升 gui，验完回 speed

gui:
  1. vision_observe (3 帧 + dHash + grid/annotations)
  2. 可选并行：uitree_snapshot + unity_snapshot(logLevel=error)
  3. 对照 working_memory.expected_result，写 last_action_result
  4. 若 fingerprint 重复 ≥ 3 → 强制换策略（禁止同点连点）
  5. VLM / 主模型产出 JSON 决策（可先 uitree，再视觉）
  6. input_probe → 执行动作
  7. vision_capture_after(short|burst) → 留给下一步
  8. 每 12 步或上下文超限：压缩 memory.json，history 留 8 步
  9. checkpoint session.json + events.jsonl
```

白盒加成（源循环没有、这里应该比它强）：

- 卡死看画面指纹 + console 同栈 + `bridge_yielding` + `!isPlaying`。`timeScale==0` 表示暂停菜单，observe 必须仍能拍，**不当作**画面卡死。
- 证据不只靠截图：hierarchy path、UI node_ref、input_probe 命中对象
- 加载/动画可用 `input_wait_ready` + observe，而不是盲目 `wait`

---

## 实施阶段

### Phase 0 — 动工前必须落地（否则 Phase 1 返工）

1. 本文独立捕获链 + EOF/realtime/超时 + changed/去重语义（已写死，实现按此）
2. 测试宿主可复现（D1）：
   - 本仓库 **没有** `Assets/`，`com.pi.unity-harness/package.json` 也 **没有** `testables`
   - 宿主项目 `Packages/manifest.json` 必须含 `"testables": ["com.pi.unity-harness"]`，否则包内 Editor/PlayMode 测试程序集不会进 Test Runner
   - CONTRIBUTING / 本计划写明：宿主路径（本机游戏工程或另建 `tests/host`）、过滤器 `Pi.UnityHarness`、以及 `unity_run_tests` 命令
   - Phase 1 单测在未声明 testables 的宿主上「绿了」不算数
3. 证据目录默认改为 `Library/PiUnityHarness/playtest/`

### Phase 1 — 感知原语（C# + 测试）

- 新 runner：`HarnessVisionRuntimeBurst`（或同等）+ `ObserveJsonAsync` / `CaptureAfterJsonAsync` / `Fingerprint` / `OverlayNormalizedGrid`
- **禁止** `for (i=0;i<3;i++) CaptureJsonAsync()` 充当 observe
- pipeline 只 **新增** `vision_observe`、`vision_capture_after`；不改 `vision_capture` 默认参数
- 普通 capture JSON 不强制加 `fingerprint` / `overlay`
- 测试：
  - Editor：网格 0/500/1000；dHash 纯色稳定（Blit 17×16，不是 `Texture2D.Resize`）；去重后 `unique_count == frames.Length`
  - PlayMode：静止 UI `changed=false` 且 `timeline=null`；动画物体 `changed=true` 且有 timeline
  - PlayMode：`timeScale=0` 时 observe 仍在 `per_frame_timeout_ms` 内返回（证明没用 `WaitForSeconds`）
  - PlayMode：非 PlayMode 调 `mode=game` 得到 `not_supported` + 可操作文案
  - 发现测试：新命令可被 `CommandRegistry.DiscoverCommands()` 发现

### Phase 2 — 跑测 skill + recipe（不进 UPM）

- 补齐 README 已承诺的 `skills/`、`recipes/vision-capture.md`
- 新增 `skills/unity-playtest-loop/SKILL.md`：
  - **默认 `mode=speed`**
  - 何时升 `gui`、何时降回 `speed`
  - gui 才启用决策 JSON、18 字段记忆、卡死策略、burst
- `recipes/playtest-observe.md`：仅 GUI 路径的 observe → probe → click → capture_after
- `recipes/playtest-speed.md`：uitree + snapshot 开发逐步（先写这个，作为默认）
- session 文件约定：`step` / `last_compressed_step` 每步对账；先不写 C# session store

### Phase 3 — 白盒融合

- observe 可选附带 bounded `uitree` 摘要 / console error tail（或 skill 规定并行调用，避免把 Vision 包撑大）
- `input_click` 接受可选 `norm_x/norm_y`（0–1000），内部换算并回写两套坐标
- 重复指纹时 skill 要求先 `unity_snapshot` + `uitree_roots` 再决策

### Phase 4 — 证据与报告（按需）

- 动作标注图、rolling `events.jsonl`、结束时用记忆生成摘要
- 不移植整份 HTML UI（`web.py`）；需要时再做只读 report recipe

### Phase 5 — 摇杆闭环（仅当目标游戏是虚拟摇杆）

- 复用 `input_drag_start/move/end` + 短脉冲，而不是 Win32 鼠标
- 模板跟踪可后置；优先键盘/Input Action

---

## 风险

| 风险 | 缓解 |
|------|------|
| 循环 `CaptureJsonAsync` 导致 3× 编解码或无法像素 diff | Phase 1 独立 burst runner，内存比较，只编码唯一帧 |
| EOF 在无渲染/低帧率下不触发或墙钟爆炸 | 单帧 500ms + 整次硬超时；错误要求 GameView 可见；interval = EOF + realtime |
| `timeScale=0` + `WaitForSeconds` 永久卡住 | 只用 realtime；单测钉死暂停菜单场景 |
| dHash 滤波抖动导致「重复≥3」误报 | 钉死 Blit 17×16；Phase 1 精确匹配；Hamming 容差后置 |
| burst EncodeToPNG 把 60fps 打到 ~25fps | 文档写明「2s + 帧率扰动」；连招测试避开 burst |
| 非 PlayMode 静默 SceneView | observe `mode=game/auto` 失败，skill 先 `editor_play` |
| 网格 + UI 标注太花 | `overlay` 可关；grid alpha 低 |
| VLM 坐标幻觉 | 默认 uitree；像素点击必须 probe |
| 记忆 schema 膨胀 | list 上限照抄源实现 |
| skill 漏压缩 | `session.json` 的 `last_compressed_step` 每步对账 |
| Temp 被清掉证据 | 默认 `Library/PiUnityHarness/playtest/` |
| 包内测试宿主不可复现 | 宿主 `testables` + CONTRIBUTING 命令 |
| 把 Agent 写进 Editor | C# 不调压缩记忆 LLM |
| MCP 回传多图 | 响应只给 path |

---

## 建议落地顺序

1. **Phase 0**：testables / 宿主命令写进 CONTRIBUTING（可与文档同步，不必等 C#）。
2. **Phase 2 速度模式 skill**（`recipes/playtest-speed.md` + skill 默认 speed），开发立刻能用，零 C#。
3. **Phase 1 独立捕获链**（只服务 GUI）。动工前不得再含糊「复用 CaptureJsonAsync」。
4. Phase 3–5 按需。

不先做独立 `com.harness.agent-vision` 包，也不把 Python runner 嵌进 Unity。

---

## 审查核对（现有代码）

### 第一次：引用存在

| 引用 | 位置 | 对双模式的含义 |
|------|------|----------------|
| `HarnessVision.CaptureJson` / `CaptureJsonAsync` | `Editor/Capabilities/Vision/HarnessVision.cs` | 速度模式唯一允许的截图；Game 必须 async |
| `HarnessVisionRuntimeCapture` | `Runtime/Vision/HarnessVisionRuntimeCapture.cs` | 单张：EOF → 编码 → Destroy。observe **另写** burst runner，只复用取帧原语 |
| `PiAbilityCoroutine.ToTask` | `Editor/Capabilities/Shared/PiAbilityCoroutine.cs` | 新 async 命令同一适配 |
| `PiUnityCoroutinePump` | `Editor/PiUnityCoroutinePump.cs` | 分帧泵；**不能**替代 EOF 硬超时 |
| `PiVisionAnnotator` | `Editor/Capabilities/Shared/PiVisionAnnotator.cs` | 现有 grid 是物理采样，不是 0–1000 尺 |
| `PiInputProbe` / `input_probe` | Shared + `PiInputPipelineCommands` | 两模式共用 |
| pipeline 发现 | `VisionPipelineDiscoveryTests` 等 | 新命令可发现即可，不加默认 shortcut |
| 测试 asmdef | Editor.Tests / PlayMode.Tests | 宿主还缺 `testables`，见 Phase 0 |
| TS / MCP 扩展 | `scripts/mcp-server.mjs` | 不把 observe/after 塞进默认工具集 |

### 第二次：与代码不符 + 未覆盖风险（已吸收）

| ID | 原计划问题 | 本文修订 |
|----|------------|----------|
| A1 | 「复用 CaptureJsonAsync」与内存去重矛盾 | 独立 burst 捕获链；只复用取帧/JSON 小工具 |
| A2 | 「已有 action-timeline-v1」失实 | 挪到「不可当积木」：协议有字、C# 无实现 |
| A3 | 「流式 → vision_analyze_*」写错 | 单次 `SendAsync`；流式归外部 Agent |
| B1 | observe 未定义非 PlayMode | `mode=game/auto` 失败 + skill 先 play |
| B2 | EOF 无超时 / 低帧率墙钟 | 单帧 500ms + 整次硬超时；interval = EOF + realtime |
| B3 | `timeScale=0` 会卡死 `WaitForSeconds` | 禁止该 API；暂停不当作画面卡死 |
| B4 | dHash 缩放算法未钉 | Blit 17×16 双线性；不用 `Texture2D.Resize` |
| B5 | 「约 2s」低估帧率扰动 | 改为 2s + EncodeToPNG 扰动 |
| C1 | `changed` 与 timeline 阈值可能打架 | `changed == (unique_count > 1)`，同源像素 diff |
| C2 | 去重是否写盘再删未定义 | 重复帧不写盘；`unique_count == frames.Length` |
| C3 | Temp 会被清 | 默认 `Library/PiUnityHarness/playtest/` |
| D1 | 无测试宿主 / 无 testables | Phase 0：宿主 manifest + 运行命令 |
| D2 | 「每 12 步」靠自觉 | `session.json.last_compressed_step` 每步对账 |

动工门槛：A1 + B2 + B3 的设计已写进「独立捕获链 / EOF / realtime」。未按此实现即视为 Phase 1 返工。

---

## 实施状态（TDD + grok review 闭环）

已按本文 Phase 0-2 完成，全程 TDD（先红后绿）+ grok cli 三轮对抗性 review。

### Phase 0 / 1（感知原语）

- 新增 `Editor/Capabilities/Vision/PlaytestVision.cs`（纯算法 + observe/after 协程 + JSON）、
  `Runtime/Vision/HarnessVisionPlaytestRunner.cs`（独立捕获链）、
  `Editor/Capabilities/PipelineCommands/PiPlaytestVisionPipelineCommands.cs`
  （`vision_observe` / `vision_capture_after`）。
- 独立 runner：相机渲染回调（`Camera.onPostRender` + `RenderPipelineManager.endCameraRendering`，
  仅 GameView 相机）作为呈现信号 + yield null 轮询实现单帧 500ms 硬超时；
  整次 `total_timeout_ms`（observe 5s / after 15s）取消传播；`s_busy` 互斥 +
  `OnDestroy` 清理；间隔用 realtime due-time（timeScale=0 可用）。
- 去重：内存 `Color32` 逐像素相等，重复帧不写盘、立刻销毁；`frames` 只含唯一路径，
  `unique_count == frames.Length`；`changed == (unique_count > 1)` 与 timeline 同源。
- dHash：`Graphics.Blit` 17×16 双线性 → 亮度 `0.299R+0.587G+0.114B` → 256bit/64hex。
- 网格：叠加在截图上（绝不清空原图），0-1000 归一化，5x7 点阵标签。
- JSON：`harness.vision.observe.v1` / `harness.vision.capture_after.v1`，
  含 `warnings` / `timed_out` / `unique_count`；超时返回 schema JSON 而非异常。
- 默认证据目录 `Library/PiUnityHarness/playtest/`。

### 测试

- Editor 22 个（网格映射/保留原图、dHash 确定性/亮度/微扰、diff、sheet、JSON schema、
  非 PlayMode `not_supported`、无效模式 `usage`、discovery）。
- PlayMode 5 个（动画 changed=true+timeline、静止 changed=false+去重、`timeScale=0` 可用、
  short/burst 序列图）。
- 回归：Editor 175/175、PlayMode 23/23（含既有 Input/Occlusion 测试）。

### Phase 2（skill + recipe）

- `skills/unity-harness-mcp/SKILL.md`、`skills/unity-playtest-loop/SKILL.md`
  （默认 speed 双模式 + 升级信号 + 18 槽记忆 + `last_compressed_step` 对账）。
- `recipes/vision-capture.md`、`recipes/playtest-speed.md`、`recipes/playtest-observe.md`。
- CONTRIBUTING 补测试宿主 `testables` 与 `unity_run_tests` 命令。

### review 记录（grok cli，3 轮）

1. 首轮：抓出网格清屏 bug（vision 主图被清空）、EOF 无硬超时、dHash 只比 R 通道、
   changed 与去重不同源、超时/失败路径 Thumbs 泄漏、测试脆弱点。
2. 复审：确认 P0 修复，指出帧号轮询不等价 EOF（GameView 不可见时帧号仍涨）→
   改为相机渲染回调信号；`timed_out` 接线、`s_busy` 永久占用、due-time 语义。
3. 终审：结论「可以合入」，残留 P1（相机过滤、中途超时算成功）已在本轮修复。

注意：PlayMode 测试依赖 Editor 会话状态，**每次会话第一次 PlayMode 运行最可靠**；
后续运行若返回 0 个测试且 Console 出现 `Run started: 0 test(s)`，重启 Editor 后
重新执行（已在 CONTRIBUTING 说明）。

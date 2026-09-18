# pi-unity-harness 会话分析与优化计划

来源会话：`C:\Users\yingbf\.pi\profiles\test\sessions\2026-09-16T17-47-32-833Z_01a0ab54-be60-710d-adcd-70d4f9aa8423.jsonl`  
工程：`F:\UnityProjects\ctest` 复刻《我的花园世界》（Unity 2022.3.14f1 + URP 2D，场景 `FarmMain`）  
宿主：pi-coding-agent + `pi-unity-harness` typed tools（mux）  
模型：`deepseek-v4-flash`  
墙钟：约 23 小时（2026-09-16 17:47 → 2026-09-17 17:08）

本计划只根据该会话的工具轨迹对照当前仓库，提出 **harness 可改** 的点。游戏业务代码本身的 NRE / 复刻缺口不在范围内。

仓库根目录 `plan.md` 仍是 unity-harness 能力集成计划，本文不覆盖它。

---

## 会话画像（证据）

| 指标 | 数值 | 含义 |
| :--- | :--- | :--- |
| 工具调用 | 3475 | 其中 bash 1968、unity_* 929 |
| `unity_pipeline` | 499 | 最高频 Unity 工具 |
| `unity_eval_file` | 226，失败 50（22%） | 主探测手段，失败率最高 |
| `unity_eval` | 46，其中 42 条含 `return` | 几乎全用错 REPL 语义 |
| `unity_snapshot` | 7，且 `maxNodes` 全是 3 或 5 | 层级观察形同虚设 |
| `unity_observe` / `unity_timeline` | 0 | 设计里的 GUI 闭环没用上 |
| `unity_capture` | 1 | 被 `vision_capture_gameview`×53 替代 |
| `uitree_*` | 0 次调用；结果里只出现 1 次字样 | 速度模式主路径被绕开 |
| 截图进模型 | 91 张 PNG 作为 `toolResult` image | 上下文被视觉帧打爆 |
| compaction | 26 次，每次压前 ~270k tokens；摘要从 18k 涨到 115k 字 | 长会话记忆膨胀 |
| mux / pipe 故障 | `mux 进程退出`×9，`bridge_not_found`×8，`managed_reloading`×15 | 域重载后通道不自愈 |
| 并行 | subagent 99 次；后半段在读 mux 单客户端限制 | 子代理无法共用 Unity |

`unity_pipeline` 命令分布（实际用法，不是文档推荐用法）：

- `editor_play` 90 / `editor_stop` 69 — PlayMode 进进出出
- `input_click` 75 + `input_drag_*` 38 — 屏幕坐标点击/拖图
- `assets_refresh` 69 — 绕开 `unity_recompile` 的等待语义
- `console_get_logs` 65，合计 ~95k 字符 — 轮询控制台
- `vision_capture_gameview` 53 — 把 GameView 当眼睛

---

## 失败模式（按影响排序）

### P1. 截图路径把 PNG 灌进模型，26 次 compaction

**症状**：`vision_capture_gameview` 53 次、`unity_capture` 1 次，会话里 91 张 `image/png` 出现在 `toolResult`。CLI 本身按设计只回路径、剥离 Base64，但 pi 宿主看到路径后把图贴进对话。

**根因（两层）**：

1. **Skill / 系统提示与真实用法脱节**。`UNITY_VERIFY_WORKFLOW_PROMPT` 和 `skills/pi-unity/SKILL.md` 把观察默认写成 `unity_snapshot`，没把 `uitree_*` 放成速度模式第一步，也没禁止「每步一张 GameView」。设计文档 `docs/playtest-loop-borrow-plan.md` 写得很清楚：日常走树，只有树对不上才升 GUI；会话里完全反了。
2. **工具结果没有「不要内联图片」的契约**。`formatResult` 只回 text；宿主按路径自动 attach。`unity_observe`（dHash、路径、不灌原图）一次都没用。

**建议改的组件**：Skill + 扩展 prompt + `unity_capture`/`pipeline vision_*` 的返回约定（只给 path + hash + 尺寸；可选 `embed=false` 默认）。

### P2. `eval` / `eval_file` 对 Agent 不友好（失败率 22%）

会话里能归因的 eval 失败：

| 模式 | 次数 | 根因 |
| :--- | :--- | :--- |
| CS0127 `InteractiveHost` void + `return` | 9 | 46 次 `unity_eval` 里 42 次写了 `return`；PreLint 只剥顶层 `return expr`，`if (x) return y;` 剥完变废语句；HINT 从未出现在 tool 结果里（HINT 计数 = 0） |
| `Object` CS0104 歧义 | 1+ | Evaluator 预置 `using System;` + `using UnityEngine;`，Agent 写 `Object` 必炸 |
| NRE / invalid cast / Sequence empty | 14 runtime | 探针假设 PlayMode 对象已在，没有「未就绪」结构化错误 |
| CS0127 / CS1525 等编译 | 27 | `.repl` 作者体验差：void host、最后一行才是值、无模板 |
| `managed_reloading` / pipe / mux | 见 P3 | 不是 C# 写错，是通道 |

代码证据：

- `PiUnityEvaluator.Lint.cs` 已有 `PreLint` 剥 `return` 和 `ClassifyError → [HINT]`，但 `Eval()` 的 `TargetInvocationException` 分支 **不走** `EnhanceCompileError`，HINT 到不了 Agent。
- HINT 文案还写着过期名 `"uh eval does not support 'return'"`。
- `skills/pi-unity/references/eval.md` 和 `unity_eval` 工具 description **完全没写**「不要 return，最后一行裸表达式」。

**建议**：工具描述 + eval.md 写清 REPL 契约；异常路径也 Enhance；预置 `using UnityEngine.Object = ...` 或默认别 `using UnityEngine`；对 `if (...) return` 给 HINT 而不是静默剥；提供 `probe` 模板（null 安全、PlayMode 检查）。

### P3. 域重载后通道不自愈

**症状**：

- `managed_reloading` 15 次（eval_file / `console_get_logs` / `vision_capture_gameview`）
- Named Pipe `bridge_not_found`（os error 2）打在 `unity_recompile` / eval 上 8 次
- `mux 进程退出` 9 次；mux 超时会 `failTransport` → `killChild`，排队请求全部 `mux 未发送已终止`，**且 `retryAllowed=false`**，不会 fallback `execFile`

`pi-unity compile` 会等到 `ready`，但会话里 `assets_refresh` 69 次会自己触发编译/导入，**不等域重载结束**。随后立刻 eval / capture 就撞 `managed_reloading`。

`MuxClient` 在 child `exit` 时直接失败当前+队列，不会拉起新 mux 再发。README 写「断线后下一条业务再连」，扩展层没做到。

**建议**：CLI/扩展对 `managed_reloading` / `bridge_not_found` 短重试直到 `status=ready`；mux 进程退出后自动 restart + 重放 **未写入** 的队列（已发出的仍不重放）；`assets_refresh` 在需要 reload 时走 compile 同等等待，或文档/schema 标明「不等待，请接着 `unity_recompile`」。

### P4. 输入拖拽 `PointerEventData.delta` 恒为 (0,0)

Agent 自己写进 `research/FINDINGS.md`：harness 拖拽 `delta` 恒 (0,0)，地图拖动手感/命中对不上。

代码证据：`HarnessInputBackend.CreatePointerData` 写死 `delta = Vector2.zero`；`SetMousePosition` 在非 EventSystem 路径调用 `ApplyMouseState(mouse, Vector2.zero)`。`DispatchPointerMoveOrDrag` 用这份 data 调 `IDragHandler`。读 `eventData.delta` 或 Input System `mouse.delta` 的游戏逻辑（2D 地图拖动非常常见）会认为没动。

`input_drag` 整段 API 只用了 1 次，拆开的 start/move/end 用了 37 次，每步还要 Agent 自己算坐标。

**建议**：移动时 `delta = current - previous`；同步写入 Input System mouse delta；`input_drag` 作为默认（一次调用、内部插值），skill 写明优先于拆步。

### P5. 场景 snapshot 对「运行时生成 / 自绘 2D」几乎无信息

7 次 `unity_snapshot` 全部是：

```
nodes: 2 of 2 total
hierarchy: Main Camera, Bootstrap
```

且 Agent 把 `maxNodes` 设成 3 或 5（工具默认 500）。对这个项目，玩法对象不在场景树上，snapshot 无法作为 Observe 步骤。Agent 于是用 226 次 eval_file 手搓探针，这是 token 和时间的主成本之一。

**建议**：

- snapshot 在 PlayMode 增加 runtime 根（DontDestroyOnLoad、动态实例）和「树太浅」时的 next-step（`uitree_snapshot` / eval probe）。
- 工具 schema 不要让模型随便把 `maxNodes` 收到个位数；描述写成「不要小于 100，截断时按提示加大」。
- 提供一个稳定的 `unity_probe`/`game_state` 管道（或 skill 里的 `.repl` 模板），专门 dump 选中/日志/自定义状态，避免每次从零写反射。

### P6. 速度模式主路径（uitree）没被选中；坐标点击 + 截图成了默认

设计：`uitree_find` → `input_probe` → `input_click`，截图是升级。  
实际：0 次 uitree，75 次盲点坐标，53 次截图。

部分合理：自绘农场地图可能根本没有 UGUI 节点。但 skill 没有「先 uitree_snapshot(interactive_only)，空再升 GUI」的硬规则；`unity_pipeline` 的 promptSnippet 只说 “like uitree_*, assets_*, input_*”，没有优先级。动态注册的 `unity_uitree_find` 等 shortcut **会话中一次都没出现**——`refreshDynamicPipelineTools` 在 `session_start` 时 fire-and-forget，Unity 未就绪就永久不注册。

**建议**：session 第一次成功连上 Unity 后再注册动态工具；skill/verify prompt 改成「uitree → snapshot/eval → observe」；`list-commands` 未知名时只回近邻（`console_clear` 那次把全部命令名倒进 5k 字符错误里）。

### P7. 单客户端 mux 卡住并行

会话后半 Agent 用 bash 翻 `mux.test.ts`、`imp.rs` Named Pipe `max_instances`、`AGENTS.md`「只允许主代理碰 Unity」。结论正确：broker 同时只服务一个客户端，mux 占管期间短 CLI / 子代理会 `Pipe busy`。

99 次 subagent 几乎不能碰 Editor，并行只剩改文件。这不是 bug，但是 **产品缺口**：要嘛 skill 写死「子代理禁止 unity_*」；要嘛给 mux 一个 `status` 租约/排队，让子代理请求进同一 mux FIFO（扩展已对 stdin 做 FIFO，但子代理是新进程，看不到父 mux）。

**建议（选一）**：

- A（小）：prompt + 工具 description 写明 Unity 工具仅主会话；子代理只写代码。
- B（中）：子代理走父进程 mux（需要 pi 扩展把 mux 提到 agent 级单例，而不是 session child）。
- C（大）：broker 多客户端队列。README 已承认当前不做。

本会话里 A 就能少掉一轮「研究 harness 源码」的跑题。

### P8. 其它摩擦（中低优）

- `unity_pipeline` 同时接受 `command` 和 `name`（314 vs 186，从未同时出现）——schema 双字段浪费决策。
- `console_clear` 不存在，真名 `console_clear_logs`；错误把全部命令列表打出来。
- `unity_run_tests` 一次 400s 超时，help 只有 `pi-unity status`，没有「PlayMode 测试要先 play / 加大 timeout / 看 test_status」。
- `test_status` 19 次、均长 3.3k 字符——缺增量/摘要字段。
- `console_get_logs` 65 次 / 95k 字符，和 snapshot.logs 重复；默认 limit 100 太大。
- eval 红线写了不要 `AssetDatabase.Refresh`，但 `assets_refresh` pipeline 没有同等警告。
- 观测日志 `~/.pi-unity/logs` 已落地（L0/L1），这次优化分析却是手扒 jsonl——L2 `pi-unity analyze` 仍缺，无法把「eval 失败率 / 截图次数 / reload 碰撞」自动汇总。

---

## 优化项映射（AHE 七组件）

| ID | 改什么 | 组件 | 预期会话变化 |
| :--- | :--- | :--- | :--- |
| O1 | 禁止默认内联截图；capture/observe 只回 path+dHash | Tool impl + Skill | 91 张图 → 个位数；compaction 次数下降 |
| O2 | eval 契约写入 tool description / eval.md；异常路径输出 HINT；修 PreLint；消 `Object` 歧义 | Tool desc + Tool impl + Skill | CS0127 / CS0104 接近 0 |
| O3 | `managed_reloading` / pipe gone / mux exit 自动等 ready 并重启 mux | Middleware（CLI+扩展） | 15+9+8 次通道失败大部分消失 |
| O4 | 拖拽写入真实 `delta`；skill 推荐 `input_drag` | Tool impl + Skill | 地图拖动可测，少 37 次拆步 |
| O5 | snapshot PlayMode 补全 / maxNodes 下限；probe 模板 | Tool impl + Skill | 少写上百个一次性 .repl |
| O6 | 连上 Unity 后再动态注册；verify prompt 改 uitree 优先 | Extension + Skill | 少盲点、少截图 |
| O7 | 写明 Unity 工具单主会话；未知 pipeline 名近邻提示 | Skill + Tool impl | 少跑题、少 5k 错误列表 |
| O8 | `assets_refresh` 等待或强制下一步 compile | Pipeline command + Skill | 少 reload 碰撞 |
| O9 | L2：从 `~/.pi-unity` + 宿主 jsonl 出失败模式报告 | 新 CLI（analyze） | 下次不用手扒 6MB jsonl |

---

## 建议落地顺序

先做 **O1 + O2 + O3 + O4**：都有会话里的硬失败/硬成本，改动面清楚，可单测。

O5/O6 是体验与 token，适合第二批。  
O7 是文档/schema。  
O8 要小心改变 `assets_refresh` 语义。  
O9 是观测闭环，不挡功能。

---

## Key Decisions

1. **先修 harness 契约，不先加新视觉模型。** 这次瓶颈是工具把错误用法和原图送进上下文，不是模型看不懂图。
2. **域重载重试放 CLI/mux，不放 Agent prompt。** 15 次 `managed_reloading` 证明「请自己重试」无效。
3. **拖拽 delta 是实现 bug，不是 Agent 用错。** 与 FINDINGS.md 和 `CreatePointerData` 写死 `Vector2.zero` 对得上。
4. **不在本轮做 broker 多客户端。** 用 skill 约束并行；多客户端是单独设计。

---

## Open Questions

1. 第一批是否按 O1–O4 实施，还是只要分析报告、先不改代码？
2. 截图默认：完全不给模型图（只 path），还是给一张 256px 缩略图？
3. 子代理并行：只写禁止（A），还是做父进程 mux 共享（B）？

---

## PR Plan

### PR1 — Eval 对 Agent 可编译

- 文件：`PiUnityEvaluator.cs` / `PiUnityEvaluator.Lint.cs` / `eval.md` / 扩展 `unity_eval*` description / 测试
- 内容：异常路径 EnhanceCompileError；HINT 文案去掉 `uh eval`；`Object` 歧义；tool/skill 写清「禁止 return，最后一行是值」；补 `if (...) return` 的测试
- 依赖：无

### PR2 — 通道在 reload 后自愈

- 文件：`native/src/bin/pi_unity/client.rs`（或 compile 同款 poll）、`.pi/extensions/pi-unity-harness/index.ts` MuxClient、测试
- 内容：`managed_reloading` / `bridge_not_found` 有界重试到 ready；mux child exit 后重启并对未 dispatch 队列重发；超时不要误杀已完成的语义
- 依赖：无（可与 PR1 并行）

### PR3 — 输入 delta + 少截图

- 文件：`HarnessInputBackend.cs`、`HarnessInput.Drag.cs`、capture/observe 输出、skill、扩展 formatResult
- 内容：pointer/mouse delta 按位移填写；capture 结果带 `embed:false` 契约；skill 把 observe/dHash 设为 GUI 默认、禁止逐步 GameView 原图
- 依赖：无

### PR4 — 观察默认路径

- 文件：`UNITY_VERIFY_WORKFLOW_PROMPT`、`skills/pi-unity/SKILL.md`、snapshot 实现、动态工具注册时机、pipeline 未知命令错误
- 内容：uitree → snapshot/eval → observe；动态工具在首次 ping 成功后注册；未知命令近邻；snapshot 拒绝过小 maxNodes 或给警告
- 依赖：PR3 的 skill 改动可合并，否则紧随其后

### PR5 — 观测 L2（可选）

- 文件：新 `pi-unity analyze` 或脚本读 `~/.pi-unity/logs` + 可选宿主 jsonl
- 内容：按 errorType / 子命令 / 截图次数出一页报告，对应 `docs/observability-logging-design.md` 里明确未做的 L2
- 依赖：不挡 PR1–4

---
name: unity-harness-mcp
description: >
  Drive Unity Editor through the pi-unity-harness tools (unity_discover,
  unity_ping, unity_status, unity_snapshot, unity_eval / unity_eval_file,
  unity_recompile, unity_pipeline, unity_run_tests / unity_list_tests,
  unity_timeline). Use when talking to a running Unity, inspecting scenes or
  PlayMode, compiling C#, evaluating scripts, taking GameView/Scene screenshots
  or HUD captures, or when bind / discover / need_target_selection /
  bridge_yielding / StatePlane appears.
---

# unity-harness-mcp

pi-unity-harness 是 AI 编码代理与 Unity Editor 之间的桥接层：代理通过 `unity_*`
工具在 Editor 进程内执行 C#、触发编译与测试、读取场景/UI/日志快照，并复用
`com.unity.pipeline` 的命令体系。本 skill 定义这些工具的正确用法与验证闭环。

## 连接

1. `unity_discover`：列出运行中的 Editor 实例并选择目标项目（多实例时按
   `projectPath` 区分，不要猜）。
2. `unity_ping` / `unity_status`：确认 broker 在线、Editor 就绪
   （`managedState: ready`；`managed_reloading` 表示域重载中，稍等再试）。
3. 若 `unity_status` 显示 `pipelineAvailable: false`，先确认包安装状态再继续。

## 工具用法

- `unity_snapshot`：有界获取 Editor 状态、活动场景层级、当前选择与近期日志。
  行动前观察基线、行动后复核结果都优先用它。
- `unity_eval`：执行短 C# 探针（主线程）。`unity_eval_file`：多行/非平凡 C#
  写到 `Temp/PiUnityHarness/AgentScratch/*.repl` 再执行。
- `unity_recompile`：改 C# 脚本后触发编译并取回错误。
- `unity_pipeline`：发现/执行 pipeline 命令；高频命令（`run_tests`、
  `list_tests` 等）有独立 shortcut。
- `unity_timeline`：查看操作审计（请求、结果、耗时），失败或长流程后复盘。

## 验证闭环（必须）

改 Unity 脚本、场景、资产或运行时行为后，不允许只改不验：

1. **Observe**：行动前 `unity_snapshot`（或定向 probe）建立基线。
2. **Act**：应用改动。
3. **Compile**：C# 改动后 `unity_recompile`，修掉编译错误。
4. **Verify**：`unity_run_tests`（EditMode/PlayMode）、PlayMode 冒烟
   （`editor_play` / `editor_stop`）或 vision/input probe。
5. **Re-observe**：行动后再快照，对比预期结果。

## 红线

- 禁止在 `unity_eval` / `unity_eval_file` 里调用 `AssetDatabase.Refresh` 或其它
  域重载触发器——用 `unity_recompile` 或 pipeline 的 `assets_refresh`。
- 禁止在主线程阻塞等待（`Task.Wait` / `.Result` / `Thread.Sleep`）；异步用
  协程（IEnumerator）。
- PlayMode 测试依赖 Editor 会话状态：**每次会话的第一次 PlayMode 运行最可靠**；
  若后续运行返回 0 个测试且日志显示 `Run started: 0 test(s)`，重启 Editor
  恢复，再用一次 `run_tests` 覆盖全部目标测试。
- 输入坐标一律 `top_left_game_view` 像素；UI 交互优先 `uitree_*`，其次
  `input_probe` 确认命中后再 `input_click`，最后才用纯像素点击。

## 截图与视觉分析

截图规范见 `recipes/vision-capture.md`；跑测循环（多帧观察、指纹、动作后连拍、
记忆压缩）见 `skills/unity-playtest-loop/SKILL.md` 与 `recipes/playtest-observe.md`。

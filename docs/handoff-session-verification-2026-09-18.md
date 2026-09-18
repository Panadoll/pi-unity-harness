# Handoff：真实 Editor 验证轮的结果、已修缺陷与待决项

上一轮交接见 `docs/handoff-session-optimizations-2026-09-18.md`，实现台账见
`docs/session-optimization-implementation.md`（末节“验证轮补记”）。本文件是验证轮
（在真实 Unity Editor 上跑）的交付证据与下一步。

## 1. 接手目标

先 review 本轮的 4 处代码改动与 2 处环境改动，再决定是否继续修 3 个真实失败用例、
补齐未验证链路（编译 no-op / 输入 / capture-observe / 工具发现）。**不要在未确认前
回退本轮的 eval 修复**：它修掉的是一个会让整个 eval 会话死到域重载的缺陷。

## 2. 工作区与提交状态

- 仓库：`F:\Projects-Test\unity-ai-tool\pi-unity-harness`（Windows / Git Bash 或 PowerShell）。
- 验证轮结束时工作区已提交，分为两个提交（前一提交是上一轮实现，后一提交是验证轮修复）：
  - `feat(session): 会话稳定性与体验优化`：上一轮的 native / TS / eval / 输入 / 视觉改动、文档与测试。
  - `fix(harness): 验证轮修复 eval 会话毒化、run_tests 汇总与新增回归`：本轮修复与探针。
- 上一轮交接里提到的 43 处未提交改动已随第一个提交入库；没有 reset / clean / 丢弃任何文件。
- Git Bash 下 `pi-unity` 不在 PATH：直接调用 `bin/pi-unity.exe`（扩展也是优先解析它）。

## 3. 本轮实测基线（可复跑，均 exit 0）

```powershell
cargo test --manifest-path native/Cargo.toml          # 98 passed
node --test .pi/extensions/pi-unity-harness/*.test.ts # 51 passed, 0 skipped
& ./scripts/test-evaluator-standalone.ps1             # compile 0；25 passed, 0 failed
& ./scripts/probes/mono-csharp-anon-poison/run.ps1    # 观察型探针（退出码恒 0）
git diff --check                                      # exit 0（仅 CRLF 警告）
```

真实 Editor（`F:\UnityProjects\ctest`，2022.3.14f1）：

- EditMode `list_tests`：258 个（harness 208 + 项目 50）。
- harness EditMode 运行：**205 passed / 3 failed**，原始证据 `F:\UnityProjects\ctest\Temp\pipeline_test_status.json`。
- PlayMode `list_tests`：**0 个**（见第 6 节）。
- `unity_recompile` 多次成功，域重载后连接恢复（generation 217 → 222 全程可用）。

## 4. 本轮改了什么（review 重点）

### 4.1 `unity/com.pi.unity-harness/Editor/PiUnityEvaluator.cs`

修复 Mono.CSharp 匿名类型容器永久毒化 eval 会话：

1. **避免**：`TryEvalTail` / `TryValidateTail` 在 `HasTopLevelValueReturn(code)` 为真时
   先走 strip/wrap 适配链（顶层带值 return 在 void 交互宿主里必然 CS0127，raw 编译不可能成功，
   却会留下半 emit 容器）；适配全失败时才补做 raw，以保留原始 CS0127 报错语义。
2. **手术修复（主力）**：`_lastCompileHadErrors` 在“编译 API 返回但带诊断 / 不完整输入 /
   抛异常”三条路径都置位；下次 `Compile` 前 `PrepareCompilerForCompile()` 通过反射清掉
   `Evaluator.module.anonymous_types` 里的脏容器。**实例不重建，因此持久变量不丢。**
3. **重建兜底**：只有反射不可用或遇到别的 `InternalErrorException` 才重建实例，且
   `DecorateRebuild` 会明确输出“持久变量已失效，需要重新声明”。

review 时请注意的可疑点：
- 反射依赖 `Mono.CSharp.Evaluator.module` 与 `ModuleContainer.anonymous_types` 两个字段名
  （Unity 2022.3 的 Mono.CSharp 4.0.0.0 已实测存在）。名字漂移时自动禁用清理并退到重建。
- 清理会清空匿名类型缓存，因此“跨调用使用同一匿名形状、且中间发生过一次编译失败”时，
  两个匿名类型可能不再同型（极少数场景下 `t = u` 会报类型不匹配）。
- `HasTopLevelValueReturn` 的判定边界见
  `Tests/Editor/PiUnityEvaluatorExecutionTests.TopLevelValueReturnGate`（void return、
  嵌套 return、字符串/注释中的 return、标识符前缀都要排除）。

### 4.2 `Editor/PiUnityEvaluator.Lint.cs`

新增 `HasTopLevelValueReturn`（第 0 层带值 return 检测，跳过字符串/注释/大括号内）。

### 4.3 `native/src/bin/pi_unity/schema.rs`

`shape_run_tests` 之前读顶层 `summary`，而桥接信封是 `{output, typeName, command, valueTypeName, value}`，
且用例字段是 PascalCase（`Status` / `FullName`）——真实跑完 208 个测试，工具却报 `passed: 0, failed: 0`。
现在解信封（`value` → `output` 解析 → 原样）+ 大小写不敏感取值，并补 3 个单测。
验证：同一条真实运行现在报 `passed: 205 / failed: 3` 并列出失败用例名。

### 4.4 `Tests/Editor/PiUnityEvaluatorExecutionTests.cs`

新增 6 个用例（用例名即断言要点）：匿名类型失败不毒化会话、污染后自愈、
带值 return 保住跨调用状态、顶层带值 return 判定边界、匿名类型失败保住状态、
带值 return 失败保住状态。全部同时跑在 NUnit 与独立 Mono runner 上。

### 4.5 新增探针

`scripts/probes/mono-csharp-anon-poison/`：直接调用 Mono.CSharp（绕开 harness），
复现污染并演示清缓存即可恢复。README 里有四组场景与预期输出。

## 5. 真实 Editor 验证到的行为（修复后）

| 步骤 | 结果 |
|---|---|
| `var edKeep = 7; edKeep` | 7 |
| `var edBad = new { a = 1 };\nnoSuchNameHere;` | 编译失败（CS0103），无重建提示 |
| `(edKeep) + 0` | 7（状态保住） |
| `return new { edKeep, broken = noSuchNameHere };` | CS0103 + CS0127 + strip/wrap 诊断 + HINT，无重建提示 |
| `(edKeep) + 1` | 8（失败后状态仍在） |
| `return new { edKeep, doubled = edKeep * 2 };` | `{ edKeep = 7, doubled = 14 }` |

修复前同一条链路：失败后连 `1 + 1` 都报 `InternalErrorException: builder already exists`，
4 分钟不自愈，只有域重载恢复。

## 6. 未修复的 3 个真实失败（需要你决策）

1. `PiUnityPipelineExecutorTests.CommandSuggestions_RankDeterministicallyAndOnlyIncludeVisibleCommands`
   —— 输入 `console_claer_logs` 期望仅 `console_clear_logs`，实际多出 `console_get_logs`
   （编辑距离正好等于阈值 `min(4, len/3)`=4）。这是「测试即规格 vs 实现」的落差；
   建议收紧阈值（如 `len/6`）而不是放宽测试。
2. `CommandSuggestions_ReturnAtMostFiveAndBoundLongUnknownInput`
   —— 未知命令名回显 `MaxSuggestionTextLength = 128` + `...`，加上后缀后消息长 216 > 200。
   建议把回显上限收到 96（总长 184）以同时满足语义与测试。
3. `Vision.VisionAnalyzeUnavailableTests.CaptureAndAnalyzeJson_DefaultProvider_ReturnsPartial`
   —— 捕获触发 URP `Render2DLightingPass.Execute` 的 NullReferenceException（未处理日志判定失败）。
   怀疑与 2D Renderer 项目下的临时相机/渲染数据有关，需要单独定位。

另：`Pi.UnityHarness.PlayMode.Tests` 的 `defineConstraints` 需要 `PI_UNITY_PLAYMODE_TESTS`，
仓库与宿主工程都没有定义 → `MouseSyncDuringDragTests` 从未编译。跑 PlayMode 前必须先在
宿主 `ProjectSettings.asset` 的 scriptingDefineSymbols 里加该符号（未获授权前不要改）。

## 7. 环境改动（仓库外，review 时须知）

- `F:\UnityProjects\ctest\Packages\manifest.json` 增加 `"testables": ["com.pi.unity-harness"]`。
  缺它时包内测试程序集根本不进编译管线（134 个程序集中没有 `Pi.*.Tests`），
  这也是上一轮“C# NUnit 测试未在真实 Editor 执行”的根本原因。
  备份在 `Temp/ctest-manifest-backup/manifest.json.orig`。是否保留由你决定。
- 清掉 6 个占用单客户端管道的旧 mux（父进程都是活跃 Pi 会话，不是孤儿；它们下次业务调用会自愈，
  但会各看到一次 `mux_crash` 诊断）。
- `bin/pi-unity.exe` 已用 `cargo build --release` 重建；`bin/`、`dist/` 都在 .gitignore 内，不入库。
  未重建时真实链路跑的是 9-10 的旧 CLI（二进制里搜不到本轮新增字段 `compiledErrorType`）。
- native 插件 `Editor/Plugins/x86_64/pi_unity_harness_native.dll`（7-31 构建）早于 9-2 的
  `lib.rs` 重构提交；协议版本 1、导出符号一致，本轮未改 lib，未替换（运行中的 Editor 也不会卸载旧 DLL）。

## 8. 仍未验证（不要写成通过）

- 编译链路：只验证了 recompile 成功与域重载后连接恢复；**no-op、临时编译错误、修复后恢复未做**。
- 输入链路：多帧拖拽、正负 delta、静止归零、清理后基线重置未做。
- 观察链路：capture/observe 实际截图、`embed:false` 契约、工具发现、session 切换后 stale generation 隔离未做。
- PlayMode 测试（缺 define）、native DLL 版本对齐未做。
- `node` 基线依赖本机原生 TypeScript 支持；换环境需先核对版本。

## 9. 顺手发现、未修（非本轮回归）

- Mono 交互语法把「语句后跟 `*` 表达式」解析成指针类型声明：`var z = 3;\nz * 7` 报
  `CS1525 Unexpected symbol 7`，`(z) * 7` 正常。纯 Mono.CSharp 可复现，建议写入 eval 使用说明。
- `unity_run_tests` 工具自身的汇总曾长期为 0（本文 4.3 已修）；若你在别的机器上仍看到 0，
  先确认 `bin/pi-unity.exe` 是新构建，再对照 `Temp/pipeline_test_status.json`。

## 10. 建议的下一步顺序

1. Review 第 4 节四处代码改动 + 第 7 节环境改动，确认语义边界（尤其 4.1 的反射清理）。
2. 决定第 6 节 3 个失败的修法（1/2 建议按测试收紧实现；3 先做最小定位探针）。
3. 决定是否给宿主加 `PI_UNITY_PLAYMODE_TESTS` 并跑 PlayMode。
4. 补编译链路 no-op / 编译错误 / 修复恢复，再补输入链路与 capture/observe。
5. 把结论写回 `docs/session-optimization-implementation.md`，未执行项明确标注。

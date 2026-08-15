# unity-harness 能力集成计划

## 实施状态

已按 TDD 完成迁移：先为 coroutine adapter、input、uitree、vision、pipeline discovery 编写失败测试，再迁移实现并跑绿。当前实现以 pipeline metadata 作为唯一发现/执行入口，不迁移旧 AbilityRegistry / registrar。

验证结果：

- `unity_recompile`：通过。
- `unity_run_tests(mode="editor", filter="Pi.UnityHarness")`：110 passed / 0 failed。
- `unity_run_tests(mode="playmode", filter="Pi.UnityHarness")`：10 passed / 0 failed。
- `unity_pipeline({})`：可发现 47 个命令，包含全部 input / uitree / vision 新命令。
- smoke：`input_ready_state`、`uitree_roots`、`uitree_snapshot`、`vision_settings`、`vision_capture(mode="scene")` 均通过。
- 审计：未迁入 `AbilityRegistry`、`AbilityManifest`、registrar、旧 `HarnessServerHost`；能力代码无阻塞等待调用。

## 目标

把 `F:/Projects-Test/unity-ai-tool/unity-harness` 中已有的 Unity 自动化能力，以拷代码式整体迁移到 `pi-unity-harness`，并复用 `pi-unity-harness` 现有的 native broker、domain reload、pipeline command 和测试能力。

## 已确认边界

- 不做 MCP，不新增 MCP server、MCP adapter、stdio/http MCP 兼容层。
- 不迁移 `unity-harness` 的旧 Named Pipe bridge、CLI、传输协议、REPL host。
- 不迁移 `unity-harness` 的测试 runner 能力；`pi-unity-harness` 已有 `PiUnityTestCoordinator` 和 `unity_run_tests`。
- 不单独迁 Roslyn 执行能力；pipeline 的 `reload_file` / `reload_file_override` 已覆盖当前热重载诉求。
- 不迁移 `AbilityRegistry` / `AbilityManifest` / registrars / ability contract tests；pi 统一用 pipeline metadata 做发现。
- `unity-harness` 能力迁移按整体迁移处理，不做“先迁一小部分能力”的路线。
- 迁移方式以拷贝原实现为主，只做命名空间、程序集引用、公共工具类、pipeline wrapper 的必要适配。

## 发现与执行决策

只保留一套发现和执行机制：`[CliCommand]` + `CommandRegistry.DiscoverCommands()` + `unity_pipeline({})`。

- 不保留旧 `AbilityRegistry`，避免形成第二套元数据系统。
- 不迁移 `InputRegistrar.cs`、`UiTreeRegistrar.cs`、`VisionRegistrar.cs`。
- 不迁移源 `*AbilityContractTests`，改为新增 pipeline discovery / schema / smoke tests。
- 源 `SKILL.md` 和 recipe 只作为文档素材，可改写为 pi docs，不作为运行时注册依据。
- 能力核心代码只提供 C# public API；pipeline wrapper 是唯一对外执行入口。

## 源能力清单

结论：能力边界是 3 个包，但执行迁移不能只拷 3 个包目录本体。去掉 registrar / AbilityRegistry 后，3 个能力包仍直接依赖 `com.harness.bridge` 里的少量 observability shared 代码，因此实际迁移范围是：

```text
com.harness.input
com.harness.uitree
com.harness.vision
+ HarnessJson.cs
+ HarnessGameViewCoordinates.cs
+ pi 自己新增的 coroutine-to-task 适配器
```

### 必须整体迁移的能力包

| 源 package | 能力 | 目标 |
| --- | --- | --- |
| `com.harness.input` | 输入注入、点击、拖拽、滚轮、文本输入、组合键、动作序列 | 整体迁移核心代码，跳过 registrar |
| `com.harness.uitree` | UGUI / UI Toolkit 树、查找、描述、文本读取、ref 管理 | 整体迁移核心代码，跳过 registrar |
| `com.harness.vision` | SceneView/GameView 截图、截图 JSON、视觉请求构建、provider 诊断 | 整体迁移核心代码，跳过 registrar |

### 必须随迁的 shared 支撑

| 源文件 | 被谁使用 | 处理 |
| --- | --- | --- |
| `com.harness.bridge/Editor/Observability/HarnessJson.cs` | input / uitree / vision JSON 构建 | 合并/适配到 `PiUnityJsonHelper` 或保留兼容工具类 |
| `com.harness.bridge/Editor/Observability/HarnessGameViewCoordinates.cs` | input 坐标转换、uitree UGUI 坐标、vision 相关坐标 | 随 input/vision/uitree 迁移到 shared |
| `com.harness.bridge/Editor/Evaluator/CoroutinePump.cs` | 不被能力包直接引用，但源 coroutine API 依赖同类运行语义 | 不直接迁旧类型；基于 `PiUnityCoroutinePump` 增加 coroutine-to-task 适配 |
| `SKILL.md` / recipe 文档 | 仅文档 | 只作为文档素材，改写为 pi 用法，不参与运行时注册 |

### 不迁移

| 源 package / 文件 | 原因 |
| --- | --- |
| `com.harness.bridge/Editor/Abilities/*` | pi 使用 pipeline metadata，不保留 AbilityRegistry |
| `com.harness.input/Editor/InputRegistrar.cs` | 旧 AbilityRegistry 注册入口，不迁移 |
| `com.harness.uitree/Editor/UiTreeRegistrar.cs` | 旧 AbilityRegistry 注册入口，不迁移 |
| `com.harness.vision/Editor/VisionRegistrar.cs` | 旧 AbilityRegistry 注册入口，不迁移 |
| `com.harness.*/*AbilityContractTests.cs` | 测旧 AbilityRegistry，改为 pipeline discovery tests |
| `com.harness.bridge/Editor/Transport/*` | pi 已有 native broker，旧 C# pipe 不需要 |
| `com.harness.bridge/Editor/HarnessServerHost.cs` | pi 已有 `PiUnityBridge` |
| `com.harness.bridge/Editor/Evaluator/HarnessEvaluator*` | pi 已有 `PiUnityEvaluator` |
| `com.harness.bridge/Editor/Compilation/*` | pi 已有 `PiUnityCompileCoordinator` / pipeline reload |
| `com.harness.testing` | pi 已有 Editor/PlayMode 测试运行能力 |
| `com.harness.bridge/Editor/Cli~` | 不迁旧 CLI |

## 目标目录结构

```text
unity/com.pi.unity-harness/
  Editor/
    Capabilities/
      Shared/
        PiAbilityJson.cs
        PiGameViewCoordinates.cs
        PiAbilityCoroutine.cs
      Input/
        HarnessInput.cs
        HarnessInput.Actions.cs
        HarnessInput.Backend.cs
        HarnessInput.Drag.cs
        HarnessInput.Sequence.cs
        InputJson.cs
      InputSystem/
        HarnessInputBackend.cs
      UiTree/
        HarnessUiTree.cs
        UguiBackend.cs
        UIToolkitBackend.cs
        UiTreeJson.cs
        UiTreeRefManager.cs
      Vision/
        HarnessVision.cs
        OpenAiCompatibleVisionAnalyzer.cs
        VisionJson.cs
        VisionSettings.cs
        VisionSettingsProvider.cs
      PipelineCommands/
        PiInputPipelineCommands.cs
        PiUiTreePipelineCommands.cs
        PiVisionPipelineCommands.cs
  Runtime/
    Vision/
      HarnessVisionRuntimeCapture.cs
  Tests/
    Editor/
      Input/*
      UiTree/*
      Vision/*
      PipelineDiscovery/*
    PlayMode/
      Input/*
```

说明：

- 文件名尽量保留源项目命名，降低后续 diff 难度。
- 命名空间统一调整到 `Pi.UnityHarness.Editor.Capabilities.*` 和 `Pi.UnityHarness.Runtime.Capabilities.*`。
- 不使用 `Abilities` 目录名，避免和旧 AbilityRegistry 概念混淆。
- 如保留源 namespace 更利于拷贝，可增加兼容 namespace wrapper，但最终公开 API 以 `Pi.UnityHarness` 为准。

## 程序集与依赖适配

### package.json

`unity/com.pi.unity-harness/package.json` 需要补充：

```json
{
  "dependencies": {
    "com.unity.ugui": "1.0.0"
  }
}
```

`com.unity.inputsystem` 不作为硬依赖，保持可选。

### asmdef

保留现有：

```text
Pi.UnityHarness.Editor
```

新增或调整：

```text
Pi.UnityHarness.Runtime.Vision
Pi.UnityHarness.InputSystem.Editor
Pi.UnityHarness.Editor.Tests
Pi.UnityHarness.PlayMode.Tests
```

关键点：

- `Pi.UnityHarness.Editor` 增加 `UnityEngine.UI` 引用，支持 UGUI 树。
- `Pi.UnityHarness.InputSystem.Editor` 使用 version define 检测 `com.unity.inputsystem`，只在安装 Input System 时编译。
- Vision runtime capture 单独放 Runtime asmdef，Editor vision assembly 引用它。
- 测试 asmdef 复用现有测试依赖，不引入旧 `Harness.*` assembly 名。
- 新增 pipeline wrapper 必须和现有 pipeline 代码一样受 `PI_UNITY_PIPELINE` version define 保护，避免未安装 `com.unity.pipeline` 时编译失败。

## Pipeline command 暴露策略

能力实现保持 C# public API，外部调用通过 pipeline command。

原则：

- 不把每个能力都提升为 pi shortcut tool。
- 默认通过 `unity_pipeline({ command, params })` 调用。
- 只在确认高频后再增加 shortcut tool。
- wrapper 尽量薄，不改动核心迁移代码。
- wrapper 文件中所有 `[CliCommand]`、`[CliArg]`、`using Unity.Pipeline.Commands` 均放在 `#if PI_UNITY_PIPELINE` 内。
- pipeline command 的 description / parameter metadata 是唯一运行时发现元数据。

建议命名：

### input

```text
input_ready_state
input_wait_ready
input_clear_all
input_press_key
input_release_key
input_key_chord
input_type_text
input_mouse_position
input_mouse_press
input_mouse_release
input_click
input_double_click
input_drag
input_drag_start
input_drag_move
input_drag_end
input_drag_cancel
input_scroll
input_sequence
```

### uitree

```text
uitree_roots
uitree_snapshot
uitree_find
uitree_describe
uitree_text
```

### vision

```text
vision_capture
vision_capture_async
vision_capture_gameview
vision_build_analysis_request
vision_analyze_image
vision_analyze_image_async
vision_capture_and_analyze
vision_capture_and_analyze_async
vision_settings
vision_test_provider
```

## 异步与协程适配

`PiUnityPipelineCommandExecutor` 只支持同步返回值和 `Task` / `Task<T>`，不支持 pipeline command 直接返回 `IEnumerator`。因此所有源 API 中返回 `IEnumerator` 的方法必须通过适配器包装成 `Task<string>`。

适配决策：

- 新增 `PiAbilityCoroutine`，内部使用 `PiUnityCoroutinePump` 或给 `PiUnityCoroutinePump` 增加 result-capturing overload。
- `PiAbilityCoroutine.ToTask(IEnumerator coroutine, string requestId, int timeoutMs)` 返回 `Task<string>`。
- 适配器逐帧驱动 coroutine，捕获最后一个 `string` yield 作为 Task result。
- timeout / cancel / exception 通过 Task fault 或标准 JSON 失败结果返回，不阻塞主线程。
- pipeline wrapper 对 coroutine API 一律返回 `Task<string>`，让 `PiUnityPipelineCommandExecutor` 现有 Task 轮询路径处理完成。

示例模式：

```csharp
#if PI_UNITY_PIPELINE
[CliCommand("vision_capture_gameview", "Capture GameView screenshot asynchronously")]
public static Task<string> VisionCaptureGameView(
    [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 60000)
{
    return PiAbilityCoroutine.ToTask(
        HarnessVision.CaptureGameViewAsyncJson(),
        "vision_capture_gameview",
        timeoutMs);
}
#endif
```

硬性要求：

- 不使用 `Task.Wait()`、`.Result`、`GetAwaiter().GetResult()`、`Thread.Sleep()`。
- `vision_capture_gameview`、`vision_capture_async`、`vision_analyze_image_async`、`vision_capture_and_analyze_async` 必须走 `IEnumerator -> Task<string>` 适配。
- `input_wait_ready`、`input_click`、`input_double_click`、`input_drag`、`input_drag_start`、`input_drag_move`、`input_drag_end`、`input_scroll`、`input_type_text`、`input_key_chord`、`input_sequence` 必须走 `IEnumerator -> Task<string>` 适配。
- 同步 API 仍直接返回 `string`。

## 测试迁移策略

迁移源能力测试，但不迁移测试 runner 和旧 AbilityRegistry contract tests。

### 需要迁移的测试

| 源路径 | 目标 |
| --- | --- |
| `com.harness.input/Tests/Editor/InputJsonTests.cs` | `Tests/Editor/Input/*` |
| `com.harness.input/Tests/PlayMode/*` | `Tests/PlayMode/Input/*` |
| `com.harness.uitree/Tests/Editor/UiTreeStaleRefTests.cs` | `Tests/Editor/UiTree/*` |
| `com.harness.uitree/Tests/Editor/UiTreeToolkitTests.cs` | `Tests/Editor/UiTree/*` |
| `com.harness.uitree/Tests/Editor/UiTreeUguiTests.cs` | `Tests/Editor/UiTree/*` |
| `com.harness.vision/Tests/Editor/VisionAnalysisRequestTests.cs` | `Tests/Editor/Vision/*` |
| `com.harness.vision/Tests/Editor/VisionAnalyzerSettingsTests.cs` | `Tests/Editor/Vision/*` |
| `com.harness.vision/Tests/Editor/VisionAnalyzeUnavailableTests.cs` | `Tests/Editor/Vision/*` |
| `com.harness.vision/Tests/Editor/VisionJsonTests.cs` | `Tests/Editor/Vision/*` |

### 不迁移的测试

| 源路径 | 原因 |
| --- | --- |
| `com.harness.input/Tests/Editor/InputAbilityContractTests.cs` | 旧 AbilityRegistry contract |
| `com.harness.uitree/Tests/Editor/UiTreeAbilityContractTests.cs` | 旧 AbilityRegistry contract |
| `com.harness.vision/Tests/Editor/VisionAbilityContractTests.cs` | 旧 AbilityRegistry contract |
| `com.harness.testing/Tests/*` | 测试 runner 不迁移 |
| `com.harness.bridge/Tests/Editor/*` 中 transport/evaluator/compile 相关 | 已由 pi 原生测试覆盖 |

### 新增测试

- Pipeline discovery tests：验证 `input_*`、`uitree_*`、`vision_*` 命令可被 `CommandRegistry.DiscoverCommands()` 发现。
- Pipeline schema tests：验证关键命令参数名、required/default/type metadata 正确。
- Coroutine adapter tests：验证 `PiAbilityCoroutine.ToTask` 能捕获最终 JSON、处理 timeout 和异常。
- Smoke tests：每类能力至少一个 pipeline 端到端调用。

### 测试适配规则

- 删除对旧 CLI / `uh eval` 的依赖。
- coroutine API 的测试需要验证 `PiAbilityCoroutine.ToTask`，不能在测试里阻塞 Unity 主线程。
- pipeline discovery tests 受 `PI_UNITY_PIPELINE` 条件保护。

### 必跑验证

```text
unity_recompile
unity_run_tests(mode="editor", filter="Pi.UnityHarness")
unity_run_tests(mode="playmode", filter="Pi.UnityHarness")
unity_pipeline({})
```

能力 smoke test：

```text
unity_pipeline({ command: "uitree_roots" })
unity_pipeline({ command: "uitree_snapshot", params: { interactiveOnly: true, maxDepth: 4, limit: 50 } })
unity_pipeline({ command: "vision_settings" })
unity_pipeline({ command: "vision_capture", params: { mode: "scene" } })
unity_pipeline({ command: "input_ready_state" })
```

Input PlayMode smoke test 只在项目安装 Input System 时启用。

## 实施步骤

### 1. 建立迁移骨架

- 新建 `Editor/Capabilities`、`Runtime/Vision`、对应测试目录。
- 新增/调整 asmdef 和 package dependency。
- 不创建 AbilityRegistry / AbilityManifest / registrar。
- 加 `PiAbilityCoroutine` 和必要的 `PiUnityCoroutinePump` result-capturing 适配。

### 2. 拷贝 input / uitree / vision 全部实现

- 直接复制源文件，但跳过 `InputRegistrar.cs`、`UiTreeRegistrar.cs`、`VisionRegistrar.cs`。
- 机械替换 namespace 和 assembly reference。
- 保留原 public API 语义。
- 只改编译错误和 pi 集成点。

### 3. 适配共享依赖

- `HarnessJson` 适配为 pi JSON 工具。
- `HarnessGameViewCoordinates` 迁入 shared。
- 协程等待统一接入 `PiAbilityCoroutine.ToTask`。
- settings/provider 路径改为 pi package 命名。

### 4. 添加 pipeline wrappers

- 为 input / uitree / vision 的所有原 entrypoint 加 `[CliCommand]` wrapper。
- 参数命名与原 JSON API 保持一致。
- 返回值保持 JSON string 或结构化对象，不做二次包装。
- 同步源 API wrapper 返回 `string`。
- 协程源 API wrapper 返回 `Task<string>`，通过 `PiAbilityCoroutine.ToTask` 适配。
- 所有 wrapper 都受 `#if PI_UNITY_PIPELINE` 保护。

### 5. 迁移并修正测试

- 拷贝源非 AbilityRegistry 测试。
- 替换 namespace、asmdef、package 名和旧 bridge 引用。
- 不迁移 ability registry contract tests。
- 新增 pipeline discovery / schema / smoke tests，覆盖 `unity_pipeline({})` 可发现新增命令。
- 所有测试通过后再进入 smoke test。

### 6. 端到端验证

- 编译通过。
- Editor tests 通过。
- PlayMode tests 通过或按 Input System 条件跳过。
- pipeline command 可发现。
- 每类能力至少一个 pipeline smoke test 通过。
- 反复 domain reload 后命令仍可发现。
- 不要求 in-flight pipeline command 跨 domain reload 存活；现有 executor 会在 reload 前中止内存中的 pending Task，这是设计行为。

## 风险与处理

| 风险 | 处理 |
| --- | --- |
| Input System 未安装 | optional asmdef + 命令返回 unavailable |
| UGUI 未安装 | `com.unity.ugui` 作为 package dependency |
| GameView 截图跨帧 | `IEnumerator -> Task<string>` 适配，不阻塞主线程 |
| 视觉 provider 需要网络/API key | 默认 provider=none，测试只做 settings/request 构建 |
| 源代码依赖旧 bridge 类型 | 迁移必要 shared 类型，不迁 transport/AbilityRegistry |
| pipeline 命令数量变多 | 统一走 `unity_pipeline`，不增加大量 shortcut tool |
| domain reload 后静态状态丢失 | 只保证 reload 后重新发现；in-flight command 跨 reload 中止是设计行为 |
| 未安装 com.unity.pipeline | wrapper 和 Unity.Pipeline 引用必须受 `PI_UNITY_PIPELINE` 保护 |

## 验收标准

- `plan.md` 中列出的 input / uitree / vision 源能力全部迁入。
- 不新增任何 MCP 相关运行时组件。
- 不引入旧 `uh.exe`、旧 Named Pipe server、旧 `HarnessServerHost`。
- 不引入旧 `AbilityRegistry`、`AbilityManifest`、registrars。
- `unity_recompile` 成功。
- `Pi.UnityHarness` 相关 Editor tests 全部通过。
- 可用环境下 PlayMode tests 通过；不可用依赖必须明确 skip/unavailable。
- `unity_pipeline({})` 能列出新增能力命令。
- input / uitree / vision 至少各完成一个 pipeline 端到端 smoke test。
- 迁移代码没有主线程阻塞等待。
- pipeline async/coroutine command 全部以 `Task<string>` 形式进入 executor，不直接返回 `IEnumerator`。

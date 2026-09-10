# uloop vendor 快照（Vendor/Uloop）

harness 通过 vendor uloop（[unity-cli-loop](https://github.com/hatayama/unity-cli-loop)，MIT）的 Editor 侧能力，
再用 pipeline `[CliCommand]` 薄包装暴露给 CLI / pi 工具。不使用 MCP。

- 上游 ref：`v3.6.3`（commit `5c0c074c`）
- vendor 根：`unity/com.pi.unity-harness/Vendor/Uloop`
- 重制命令：`python scripts/vendor-uloop.py --ref v3.6.3`
- 上游来源：`../unity-cli-loop`（可用 `--src` 指定）

## 目录约定

`Vendor/Uloop` 与上游 `Packages/src` **同构**：目标路径 = `Vendor/Uloop/<相对 Packages/src 的路径>`。
升级时可以直接对整棵树做 diff，不需要路径映射表。

只复制清单里列出的程序集（见 `scripts/vendor-uloop.manifest.json`），排除 `Skill/`、`DESIGN.md`、`*.meta`
以及 uloop 自己的应用层（`Application`/`Domain`/`Infrastructure`/`Presentation`/`CompositionRoot`）。
`ExecuteDynamicCode` 只取动态编译后端（`Compilation/`、`DynamicCompilation/`、`Models/`），
uloop 的 execute-dynamic-code 工具层与 harness 原生 `unity_eval` 重复，不 vendor。

## 本地补丁（由脚本自动重放，勿手改 vendor 文件）

1. `AssemblyInfo.cs` 追加 `InternalsVisibleTo("Pi.UnityHarness.Editor")` / `Pi.UnityHarness.Editor.Tests`。
2. `*EditorStartup.cs`、`ToolContracts/MainThreadSwitcher.cs` 的 `internal static` 放开为 `public static`。
3. 包根路径字面量：`"Editor/FirstPartyTools/` → `"Vendor/Uloop/Editor/FirstPartyTools/`
   （`HotReloadConstants.WorkerSourcePackageRelativePath` 等常量必须指向真实位置）。
4. 剥离对未 vendor 程序集的死 using（`Application`/`Domain`/`Infrastructure`/`Presentation`/`CompositionRoot`）。
   注意 `InternalAPIBridge` 已 vendor，不能剥离。
5. 不复制 `*.dll.meta`：沿用上游 GUID 会让 Unity 复用旧路径的导入 artifact，`-analyzer` 会指向已删除路径。

harness 自有文件放 `scripts/vendor-extras/`（按 vendor 相对路径复制回来），不会被重制覆盖。
目前只有一个：`Editor/ToolContracts/EditorUpdateMainThreadDispatcher.cs`。

## 暴露的命令

| Pipeline 命令 | uloop 来源 |
| --- | --- |
| `hot_reload` / `hot_reload_status` / `hot_reload_revert_all` | 源码级 Harmony/Cecil 即时补丁，不触发 recompile |
| `pause_point_enable` / `pause_point_clear` / `pause_point_status` / `pause_point_await` | Harmony 注入 + 变量捕获；`persist` 支持跨 Domain Reload 重挂，`snapshot_timing=post-line` 捕获行执行后的值 |
| `input_record_start` / `input_record_stop` / `input_record_status` | Input System 录制 |
| `input_replay` / `input_replay_stop` / `input_replay_status` | 回放录制的 JSON |
| `vision_annotate_raycast` | 分簇物理射线标注（胶水在 `Editor/Capabilities/VendorGlue/RaycastAnnotationGlue.cs`） |

包装入口：`Editor/Capabilities/PipelineCommands/PiUloopUniquePipelineCommands.cs`，
初始化入口：`Editor/PiUloopVendorBootstrap.cs`。
uloop 工具本身只暴露 `protected ExecuteAsync(TSchema, ct)`，harness 调基类的公开重载
`ExecuteAsync(JToken, ct)`，因此**不需要**再给 vendor 打可见性补丁。

## asmdef 依赖

harness 侧程序集引用（新增或调整过）：

- `Pi.UnityHarness.Editor` 额外引用 `UnityCLILoop.FirstPartyTools.Common.GameView.Editor`
  （`RaycastAnnotationGlue` 需要读 GameView 尺寸）。
- `Pi.UnityHarness.Editor.Tests` 额外引用 `UnityCLILoop.FirstPartyTools.HotReload.Shared.Editor`
  （3.6.3 把 `HotReloadFileSystemPath` 等移到了 Shared，内部类型靠 `InternalsVisibleTo` 可见）。

## 已验证（Unity 2022.3.14f1，工程 `F:/UnityProjects/2022test`）

- 全量重制后编译干净，broker 正常；全新启动编辑器同样干净。
- EditMode 回归：222/222 通过，测试集合与升级前基线一致（`.baseline/edit-before-summary.json`）。
- hot reload 端到端：改源码 `1→2` 后 `hot_reload` 报 `PatchedTotal=2`，运行时返回值即刻变为 `2`
  （`hot_reload_status` 显示两方法 InvocationCount=1），`hot_reload_revert_all` 后回到 `1`。
- pause point 端到端：命中 `Compute` 第 27 行，捕获 `seed=3`、`acc=1015`（pre-line）；
  `snapshot_timing=post-line` 捕获 `acc=1016`；`persist=true` 返回 `Persisted=true`。
- PlayMode：uloop `capture_game_view` 出 1280x720 PNG（覆盖新 vendor 的 Screenshot + InternalAPIBridge）。

探针脚本：`F:/UnityProjects/2022test/Assets/PiHarnessFixtures/HotReloadProbe.cs`。

## 前置条件与坑

- pause point 需要 Unity 处于 **Debug 代码优化**（`CompilationPipeline.codeOptimization = Debug`），
  并且要把机器级偏好 `EditorPrefs "ScriptDebugInfoEnabled" = true` **重启编辑器**后才在整场生效；
  否则 `pause_point_enable` 会返回 `PAUSE_POINT_RELEASE_CODE_OPTIMIZATION`。
  Release 下 hot reload 仍然可用。
- 探针方法体不能过小，否则被 Mono JIT 内联，pause point 命中率 0（启用时会有 warning 提示）。
- 后台焦点下 Unity 不会自动刷新，因此**编译路径必须先刷新**：`PiUnityCompileCoordinator.StartCompile`
  会先 `AssetDatabase.Refresh()` 再 `CompilationPipeline.RequestScriptCompilation()`（与上游 uloop 的
  `CompileController` 一致）。少了 Refresh，"改完文件直接 compile" 会看不到新脚本而静默返回成功。
  `scripts/focus-window.ps1 -Refresh` 只在 broker 不可用、拿不到 CLI 时作为兜底（聚焦编辑器并发 Ctrl+R）。
- 大规模移动 vendor 目录后，Unity 可能残留旧的 per-assembly 编译数据（表现为 `CS0006`/`CS2001`
  指向已删除路径）。此时需要重启编辑器。

## 未 vendor

`RecordVideo`（视频录制）、`Watch`（watch 表达式）、uloop CLI 侧工具（compile/run-tests/logo 等，harness 有原生实现）、
以及 uloop 的 `Application`/`Domain`/`Infrastructure`/`Presentation` 服务框架。
`Watch` 依赖完整动态编译服务，harness 只 vendor 了编译器后端，移植需先接好 `IDynamicCompilationService`。

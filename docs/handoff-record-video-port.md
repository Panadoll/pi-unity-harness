# Handoff：把 uloop 录像能力（record-video）迁移进 pi-unity-harness

- 状态：已完成（已于 `feat/uloop-record-video` 分支完成 vendor、命令包装、问题求证与全套真实 Unity 验收）
- 上游基线：`unity-cli-loop @ v3.6.3`（commit `5c0c074c`）
- 承接分支：`feat/uloop-record-video`

## 结论先说

录像能力程序集为 `UnityCLILoop.FirstPartyTools.RecordVideo.Editor`，其 **5 个依赖程序集在本仓库已经全部 vendor 完毕**（其中 2 个是本轮升级时新加的）。
因此本次只需要：manifest 加一行纳入 vendor → 重制 → 加一个 `[CliCommand]` 包装 → 编译 → 真实录制验证。

## 1. 事实基线

| 项 | 值 |
| --- | --- |
| 上游代码 | `unity-cli-loop/Packages/src/Editor/FirstPartyTools/RecordVideo`（23 个 .cs，1542 行） |
| 程序集 | `UnityCLILoop.FirstPartyTools.RecordVideo.Editor`（Editor-only，无平台/版本 define） |
| 工具名 | `record-video`（`UnityCliLoopConstants.TOOL_NAME_RECORD_VIDEO`） |
| 为什么现在没有 | 本轮范围是"已有 vendor 子系统的升级"，录像是新增能力，排在下一阶段 |

依赖核对（全部已在 `Vendor/Uloop` 里，无需新增）：

| 依赖程序集 | vendor 路径 | 状态 |
| --- | --- | --- |
| `UnityCLILoop.ToolContracts` | `Editor/ToolContracts` | 已有（含 `WindowMatchMode`、`EditorFrameWaiter`、`UnityCliLoopConstants`） |
| `Unity.InternalAPIEditorBridge.024` | `Editor/InternalAPIBridge` | 本轮新增 |
| `UnityCLILoop.FirstPartyTools.Common.Preflight.Editor` | `Editor/FirstPartyTools/Common/Preflight` | 已有（`PlayModeToolPreflightService`） |
| `UnityCLILoop.FirstPartyTools.Common.OutputRetention.Editor` | `Editor/FirstPartyTools/Common/OutputRetention` | 已有（`OutputFileRetention`） |
| `UnityCLILoop.FirstPartyTools.Common.EditorWindowFinder.Editor` | `Editor/FirstPartyTools/Common/EditorWindowFinder` | 本轮新增 |

## 2. 上游 API 速查（已核实）

工具类：`RecordVideoTool : UnityCliLoopTool<RecordVideoSchema, RecordVideoResponse>`。

参数（`RecordVideoSchema`）：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `Action` | `RecordVideoAction.start` | `start` / `stop` / `status`（小写枚举） |
| `FrameRate` | 30 | |
| `MaxDurationSeconds` | 60 | 到点自动停，`StoppedBy=max-duration` |
| `OutputPath` | `""` | 空则落到默认目录 |
| `WindowName` | `""` | 非空 = 录指定 Editor 窗口；空 = 录 Game View |
| `MatchMode` | `WindowMatchMode.exact` | `exact` / `prefix` / `contains` |
| `ResolutionScale` | 1.0 | |
| `Quality` | `RecordVideoQuality.medium` | `low` / `medium` / `high` |

响应（`RecordVideoResponse`）：`Message`、`Action`、`IsRecording`、`OutputPath`、`Width`、`Height`、
`FrameRate`、`EncodedFrameCount`、`SkippedFrameCount`、`ElapsedSeconds`、`StoppedBy`、`Quality`、`Success`。

前置条件（重要）：

- **录 Game View 必须在 PlayMode 中**：`ExecuteStartAsync` 会先 `PlayModeToolPreflightService.RequireActive()`，
  不在 PlayMode 直接失败返回。
- **录 Editor 窗口不需要 PlayMode**（注释原文：window recording paints through the Editor loop），
  内部走 `EditorWindowFinder.FindWindowsByName` + `window.ShowTab()`，并等布局稳定（`EDITOR_FRAME_WAIT_TIMEOUT_MS`），
  布局超时会返回 `WindowLayoutTimedOutMessage`。

产物：

- 默认路径 `<project>/<OUTPUT_ROOT_DIR>/<VIDEOS_DIR>/<prefix><timestamp>.<ext>`
  （`gameview_` 前缀录 Game View，`window_` 前缀录窗口）
- 编码：`UnityEditor.Media.MediaEncoder`，Windows/macOS 出 **mp4**，Linux 出 **webm**
- 自动保留：每个目录最多 20 个（`OutputFileRetention.MAX_FILES_PER_DIRECTORY`），超出删最旧
- `LastCompletedRecordingStore` 用 `SessionState` 存最后一次结果，因此 Domain Reload 后 `status` 仍能读到

停止原因（`StoppedBy`）：`cli` / `max-duration` / `play-mode-exit` / `assembly-reload` / `editor-quit` /
`window-closed` / `frame-texture-lost`。

启动钩子：`RecordVideoEditorStartup.Initialize()` → `RecordVideoService.InitializeForEditorStartup()`，
注册 `playModeStateChanged`、`quitting`、`update`，用于退出 PlayMode / 域重载 / 退出编辑器时自动落盘。

## 3. 迁移步骤

### 步骤 1：manifest 加一项

`scripts/vendor-uloop.manifest.json` 的 `assemblies` 里追加：

```json
{ "name": "UnityCLILoop.FirstPartyTools.RecordVideo.Editor" }
```

### 步骤 2：重制 vendor

```bash
cd pi-unity-harness
python scripts/vendor-uloop.py --ref v3.6.3
```

脚本会自动做：复制 `Editor/FirstPartyTools/RecordVideo`、asmdef 的 GUID→程序集名、
`AssemblyInfo.cs` 追加 `InternalsVisibleTo("Pi.UnityHarness.Editor")`、
`RecordVideoEditorStartup.cs` 的 `internal static class` 放开为 `public static class`。
不要手改 vendor 目录里的文件。

### 步骤 3：加 pipeline 包装

新建 `unity/com.pi.unity-harness/Editor/Capabilities/PipelineCommands/PiUloopRecordVideoCommands.cs`
（不要把 `PiUloopUniquePipelineCommands.cs` 继续撑大）。参考实现：

```csharp
#if PI_UNITY_PIPELINE
using System.Threading.Tasks;
using io.github.hatayama.UnityCliLoop.FirstPartyTools;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiUloopRecordVideoCommands
    {
        [CliCommand("record_video", "Record the Game View (PlayMode) or an Editor window to mp4")]
        public static Task<string> RecordVideo(
            [CliArg("action", "start, stop, or status")] string action = "status",
            [CliArg("frame_rate", "Frames per second")] int frameRate = 30,
            [CliArg("max_duration_seconds", "Auto-stop after N seconds")] int maxDurationSeconds = 60,
            [CliArg("output_path", "Absolute output path; empty uses the project default")] string outputPath = "",
            [CliArg("window_name", "Editor window to record; empty records the Game View")] string windowName = "",
            [CliArg("match_mode", "exact, prefix, or contains")] string matchMode = "exact",
            [CliArg("resolution_scale", "Scale factor applied to the source size")] float resolutionScale = 1.0f,
            [CliArg("quality", "low, medium, or high")] string quality = "medium",
            [CliArg("timeout_ms", "Command timeout")] int timeoutMs = 120000)
        {
            var token = new JObject
            {
                ["action"] = action,
                ["frameRate"] = frameRate,
                ["maxDurationSeconds"] = maxDurationSeconds,
                ["outputPath"] = outputPath ?? string.Empty,
                ["windowName"] = windowName ?? string.Empty,
                ["matchMode"] = matchMode,
                ["resolutionScale"] = resolutionScale,
                ["quality"] = quality
            };
            return PiUloopToolRunner.Run(
                ct => new RecordVideoTool().ExecuteAsync(token, ct),
                timeoutMs);
        }
    }
}
#endif
```

配套把 `PiUloopUniquePipelineCommands` 里那个 `private static async Task<string> Run<T>(...)` 抽成
`internal static class PiUloopToolRunner`（同一个目录），两处共用；不要复制粘贴第二份。

注意：不要使用 `JObject.FromObject(schema)`，因为 Newtonsoft 默认将枚举序列化为整数 Token（例如 `status` 变为 `2`），而 uloop 端的 `CaseInsensitiveStringEnumConverter` 会显式拒绝整数 Token 并要求字符串，导致 `ConvertToSchema` 抛出验证失败异常。直接传递字符串 `JObject` 可以复用 uloop 内置的容错解析与校验。同时将 `ct` 传入 ToolRunner 以支持超时取消。

用上游公开重载 `ExecuteAsync(JToken, ct)`，不要再走可见性补丁路线（本轮已经把旧补丁删掉了）。

### 步骤 4：asmdef 引用

`unity/com.pi.unity-harness/Editor/Pi.UnityHarness.Editor.asmdef` 的 `references` 追加：

```
"UnityCLILoop.FirstPartyTools.RecordVideo.Editor"
```

### 步骤 5：注册编辑器启动钩子

`unity/com.pi.unity-harness/Editor/PiUloopVendorBootstrap.cs` 追加一行：

```csharp
RecordVideoEditorStartup.Initialize();
```

（`Initialize` 是 internal，靠 vendor `AssemblyInfo.cs` 里的 `InternalsVisibleTo("Pi.UnityHarness.Editor")` 可见。）

### 步骤 6：编译

```bash
./bin/pi-unity.exe --project-path F:/UnityProjects/2022test compile
./bin/pi-unity.exe --project-path F:/UnityProjects/2022test eval 'return "failed=" + UnityEditor.EditorUtility.scriptCompilationFailed;'
```

注意：`pi-unity` 一定要带 `--project-path`，机器上还有另一个 Unity 工程（`F:/UnityProjectsTemp/2022test-wt-lib`），
不带参数可能连到那个 broker。

### 步骤 7：更新文档

- `docs/uloop-unique-tools-port.md`：命令表加 `record_video`，并把它从"未 vendor"段移除
- `Vendor/Uloop/VENDOR.md` 由脚生成，不用手改

## 4. 验收（Definition of Done）

必须跑真实 Unity（工程 `F:/UnityProjects/2022test`，Unity 2022.3.14f1）：

1. **编译与回归**：`scriptCompilationFailed=False`；`run-tests --mode edit` 仍是 222/222。

2. **Game View 录制**：
   先进入 PlayMode：`pi-unity pipeline editor_application_set_state -p set_playing=true -p is_playing=true`
   （用 `pi-unity pipeline editor_application_get_state` 确认 `IsPlaying=true`；
   响应里的 `IsPlayingOrWillChangePlaymode` 先为 true 是正常的，等一拍再看）
   然后：`pi-unity pipeline record_video -p action=start -p frame_rate=30 -p max_duration_seconds=10`
   → 等 3~5 秒 → `pi-unity pipeline record_video -p action=stop`。
   判定：`Success=true`、`IsRecording=false`、`OutputPath` 文件存在且 `size > 0`、
   `EncodedFrameCount` 与 `FrameRate × ElapsedSeconds` 同量级（`SkippedFrameCount` 可为非 0）、
   `Width/Height` 与 GameView 尺寸一致（受 `ResolutionScale` 影响）。
   文件校验：`head -c 12 file.mp4` 里能看到 `ftyp`；有 ffprobe 的话 `ffprobe -v error -show_format file.mp4`。
3. **Editor 窗口录制（不需要 PlayMode）**：
   退出 PlayMode（`editor_application_set_state -p set_playing=true -p is_playing=false`）后：
   `pi-unity pipeline record_video -p action=start -p window_name=Console -p match_mode=contains` → stop。
   判定同上，文件名前缀应为 `window_`。
   反向用例：`-p window_name=不存在的窗口` 应返回窗口未找到的失败信息，而不是抛异常。
4. **自动停止路径**：start 后直接退出 PlayMode，等 2~3 秒，`-p action=status` 应显示
   `StoppedBy=play-mode-exit` 且文件已落盘。
5. **前置条件反向验证**：EditMode 下不传 `window_name` 调 `-p action=start`，应返回 PlayMode preflight 失败。
6. **资源清理**：录制产物在 `<project>/…/Videos/`，验证完删掉；确认 `OutputFileRetention`
   生效（放超过 20 个文件时最旧的被删）。

## 5. 坑与注意

- **stop 是长任务**：要等编码器 mux 落盘，`timeout_ms` 建议 ≥120s（默认 30s 的 pipeline 超时不够）。
- **start 也是异步的**：窗口录制要等若干编辑器帧让布局稳定，超时会给布局超时错误，别当成 bug。
- **分辨率会被规范化**：奇数尺寸走 `VideoFrameSizePolicy` 处理，最终尺寸以响应里的 `Width/Height` 为准。
- **磁盘/内存**：`MaxDurationSeconds` 默认 60，长录会持续占用；默认目录最多留 20 个文件。
- **命令命名**：不要叫 `eval`/`recompile`/`recompile_status`（`PiUnityPipelineCommandExecutor.ForbiddenCommands` 会拦），
  `record_video` 不冲突。
- **只支持 Windows Editor**（harness 自身限制），因此产物固定是 mp4；Linux 分支（webm）在本仓库不会被走到。
- 需要 `com.unity.modules.video`（测试工程 manifest 已有）。
- **不要给 vendor 打补丁**：可见性靠脚本重放的 `InternalsVisibleTo`；行为改动改 manifest 或 harness 侧胶水。
- 加完 asmdef 引用后如果 Unity 没感知到，先 `pi-unity pipeline assets_refresh` 再 compile
  （compile 现在自己会先 Refresh，但新程序集首次导入仍建议显式刷一次）。

## 6. 相关文件（读这几个就够）

- 上游实现：`unity-cli-loop/Packages/src/Editor/FirstPartyTools/RecordVideo/*`（尤其 `RecordVideoUseCase.cs`、`RecordVideoService.cs`、`RecordVideoSchema.cs`）
- 依赖：`Editor/ToolContracts/UnityCliLoopScreenshotTypes.cs`（`WindowMatchMode`）、`Editor/ToolContracts/EditorFrameWaiter.cs`
- 本仓库包装样例：`unity/com.pi.unity-harness/Editor/Capabilities/PipelineCommands/PiUloopUniquePipelineCommands.cs`
- PlayMode 控制：`unity/com.pi.unity-harness/Editor/Capabilities/PipelineCommands/PiEditorPipelineCommands.cs`
  （`editor_application_set_state` / `editor_application_get_state`，可设 playing 与 paused）
- vendor 工具链：`scripts/vendor-uloop.py`、`scripts/vendor-uloop.manifest.json`、`unity/com.pi.unity-harness/Vendor/Uloop/VENDOR.md`
- 迁移说明：`docs/uloop-unique-tools-port.md`

## 7. 不在本次范围

- `Watch`（watch 表达式）：同样未 vendor，但它是另一类工作——需要先把完整动态编译服务接好
  （harness 只 vendor 了 `ExecuteDynamicCode` 的编译器后端，`IDynamicCompilationService` 需要接线）。
  另外 `Watch` 依赖 `PausePoint`，而 pause point 需要编辑器处于 Debug 代码优化模式。

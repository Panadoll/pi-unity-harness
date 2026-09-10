# Vendor/Uloop 来源与本地补丁

本目录是 [unity-cli-loop](https://github.com/hatayama/unity-cli-loop)（uloop，MIT）`Packages/src`
的子集快照，由 `scripts/vendor-uloop.py` 重制。不要手改：改动会被下一次重制覆盖，
harness 自有内容请放 `scripts/vendor-extras/`，行为补丁请改 `scripts/vendor-uloop.manifest.json`。

- 上游 ref: `v3.6.3`
- 上游 commit: `5c0c074c84dfbc57293b365f2ecfd22874234e96`
- 重制命令: `python scripts/vendor-uloop.py --ref v3.6.3`

## 复制范围

| asmdef | 上游路径（相对 Packages/src） | 文件数 |
| --- | --- | --- |
| `UnityCLILoop.FirstPartyTools.Common.EditorWindowFinder.Editor` | `Editor\FirstPartyTools\Common\EditorWindowFinder` | 2 |
| `UnityCLILoop.ToolContracts` | `Editor\ToolContracts` | 46 |
| `UnityCLILoop.Runtime` | `Runtime` | 32 |
| `UnityCLILoop.PausePoints.Runtime` | `Runtime\PausePoints` | 26 |
| `UnityCLILoop.FirstPartyTools.Common.GameView.Editor` | `Editor\FirstPartyTools\Common\GameView` | 4 |
| `UnityCLILoop.FirstPartyTools.Common.InputRecording.Editor` | `Editor\FirstPartyTools\Common\InputRecording` | 13 |
| `UnityCLILoop.FirstPartyTools.Common.InputSystem.Editor` | `Editor\FirstPartyTools\Common\InputSystem` | 16 |
| `UnityCLILoop.FirstPartyTools.Common.MouseUi.Editor` | `Editor\FirstPartyTools\Common\MouseUi` | 4 |
| `UnityCLILoop.FirstPartyTools.Common.OutputRetention.Editor` | `Editor\FirstPartyTools\Common\OutputRetention` | 2 |
| `UnityCLILoop.FirstPartyTools.Common.Overlay.Editor` | `Editor\FirstPartyTools\Common\Overlay` | 4 |
| `UnityCLILoop.FirstPartyTools.Common.Preflight.Editor` | `Editor\FirstPartyTools\Common\Preflight` | 3 |
| `UnityCLILoop.FirstPartyTools.ExecuteDynamicCode.Editor` | `Editor\FirstPartyTools\ExecuteDynamicCode` | 76 |
| `UnityCLILoop.FirstPartyTools.HotReload.Editor` | `Editor\FirstPartyTools\HotReload` | 213 |
| `UnityCLILoop.FirstPartyTools.HotReload.Patching.Editor` | `Editor\FirstPartyTools\HotReload\Patching` | 28 |
| `UnityCLILoop.FirstPartyTools.HotReload.Shared.Editor` | `Editor\FirstPartyTools\HotReload\Shared` | 26 |
| `UnityCLILoop.FirstPartyTools.HotReload.IntroducedType.Editor` | `Editor\FirstPartyTools\HotReload\IntroducedType` | 12 |
| `UnityCLILoop.FirstPartyTools.PausePoint.Editor` | `Editor\FirstPartyTools\PausePoint` | 72 |
| `UnityCLILoop.FirstPartyTools.RecordInput.Editor` | `Editor\FirstPartyTools\RecordInput` | 7 |
| `UnityCLILoop.FirstPartyTools.ReplayInput.Editor` | `Editor\FirstPartyTools\ReplayInput` | 8 |
| `UnityCLILoop.FirstPartyTools.Screenshot.Editor` | `Editor\FirstPartyTools\Screenshot` | 29 |
| `UnityCLILoop.FirstPartyTools.RecordVideo.Editor` | `Editor\FirstPartyTools\RecordVideo` | 24 |
| `Unity.InternalAPIEditorBridge.024` | `Editor\InternalAPIBridge` | 6 |

不做 vendor 的上游程序集：`UnityCLILoop.Application`、`UnityCLILoop.Domain`、
`UnityCLILoop.Infrastructure`、`UnityCLILoop.Presentation`、`UnityCLILoop.CompositionRoot`，
以及 uloop 自己的工具层（`FirstPartyTools/` 下除清单外的目录、`ExecuteDynamicCode` 的
tool/use-case/schema 文件）。

## 本地补丁（由脚本自动重放）

1. `AssemblyInfo.cs`：追加 `InternalsVisibleTo`，让 harness 程序集能访问 vendor 内部类型。
2. `*EditorStartup.cs` 与 `ToolContracts/MainThreadSwitcher.cs`：把 `internal static`
   放开成 `public static`，harness 从 `[CliCommand]` 包装里直接调用。
3. 包根路径字面量：`"Editor/FirstPartyTools/` 前缀改为 `"Vendor/Uloop/Editor/FirstPartyTools/`，
   让 `HotReloadConstants.WorkerSourcePackageRelativePath` 等常量指向实际位置。
4. asmdef 的 `GUID:` 引用转成程序集名，并丢弃未被 vendor 的程序集引用。

## 未 vendor 的引用（上游 asmdef 里有、但对应程序集没有复制）

- `UnityCLILoop.FirstPartyTools.Common.InputSystem.Editor`: UnityCLILoop.Application
- `UnityCLILoop.FirstPartyTools.ExecuteDynamicCode.Editor`: UnityCLILoop.FirstPartyTools.Common.EditorUtility.Editor, UnityCLILoop.Application, UnityCLILoop.Domain
- `UnityCLILoop.FirstPartyTools.RecordInput.Editor`: UnityCLILoop.Application
- `UnityCLILoop.FirstPartyTools.Screenshot.Editor`: UnityCLILoop.Application

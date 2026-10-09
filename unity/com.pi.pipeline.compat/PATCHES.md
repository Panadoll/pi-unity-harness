# Compat Upstream Boundary

上游基线：`com.unity.pipeline@0.6.0-exp.1`，revision `27af91a943315a5702ac7679241399b649d6a20d`（官方内部仓库地址保留在 `package.json`）。Unity 6 工程不使用此 compat，改用官方 `com.unity.pipeline@0.8.0-exp.1`。

## Patch Inventory

| Local file | Upstream file | Modification type | Reason | Related issue/commit |
|---|---|---|---|---|
| `Runtime/IlInterpreter/ScriptInterpreter.cs` | Unity Pipeline interpreter `ScriptInterpreter.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |
| `Runtime/IlInterpreter/HostBinding.cs` | Unity Pipeline interpreter `HostBinding.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |
| `Runtime/Common/BasePipelineServer.cs` | Unity Pipeline server `BasePipelineServer.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |
| `Runtime/HotReload/InPlaceReloadProcessor.cs` | Unity Pipeline hot-reload `InPlaceReloadProcessor.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |
| `Runtime/Attributes/CliCommandAttribute.cs` | Unity Pipeline `Runtime/Attributes/CliCommandAttribute.cs` | moved/synced | Keep command attributes in the standalone Attributes assembly, matching the official package layout while retaining the compat API. | Pipeline 0.8 assembly split |
| `Runtime/Attributes/CliArgAttribute.cs` | Unity Pipeline `Runtime/Attributes/CliArgAttribute.cs` | moved/synced | Keep command attributes in the standalone Attributes assembly, matching the official package layout while retaining the compat API. | Pipeline 0.8 assembly split |

## Fully Local Files

The compat package contains additional local glue, Unity-version adapters, and project integration files. These are not claimed to be upstream files until provenance is established; each future sync must classify them explicitly in this table or in a companion inventory update.

## Unity Compatibility Matrix

| Unity version | CI/manual coverage | Notes |
|---|---|---|
| 2021.3 | 声明最低版本，未完成真实验收 | 本机未安装该版本；2022.3 通过不能替代此版本证据。 |
| 2022.3.14f1 | 2026-10-09 真实 Editor 验证 | 隔离工程 harness EditMode 222 passed；`F:/SampleProjects/uitest` 完整 EditMode 272 passed，启用 harness PlayMode 定义后 302 passed / 0 failed / 1 inconclusive（外部 UniGameKit UI raycast 用例）。编译重载、SceneView、异步 GameView、observe、after 均已实际执行。证据见 `scratch/boundary-evidence/`。 |

## Update Rule

Any future change under `unity/com.pi.pipeline.compat/` must update this file in the same change. CI should reject a compat diff that does not also touch `PATCHES.md`.

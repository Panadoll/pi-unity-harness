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
| 2021.3 | Package minimum / current declared target | The package manifest declares `"unity": "2021.3"`; run the `Pi.UnityHarness` Editor tests manually. |

## Update Rule

Any future change under `unity/com.pi.pipeline.compat/` must update this file in the same change. CI should reject a compat diff that does not also touch `PATCHES.md`.

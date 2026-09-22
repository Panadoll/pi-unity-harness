# Compat Upstream Boundary

TODO: determine and record the exact upstream Unity Pipeline repository/package version before the next compat sync. Until then `UPSTREAM_VERSION` is `unknown`.

## Patch Inventory

| Local file | Upstream file | Modification type | Reason | Related issue/commit |
|---|---|---|---|---|
| `Runtime/IlInterpreter/ScriptInterpreter.cs` | Unity Pipeline interpreter `ScriptInterpreter.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |
| `Runtime/IlInterpreter/HostBinding.cs` | Unity Pipeline interpreter `HostBinding.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |
| `Runtime/Common/BasePipelineServer.cs` | Unity Pipeline server `BasePipelineServer.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |
| `Runtime/HotReload/InPlaceReloadProcessor.cs` | Unity Pipeline hot-reload `InPlaceReloadProcessor.cs` | rewrite | Local compat copy used by the harness; exact upstream revision is not recorded yet. | TODO: establish upstream provenance |

## Fully Local Files

The compat package contains additional local glue, Unity-version adapters, and project integration files. These are not claimed to be upstream files until provenance is established; each future sync must classify them explicitly in this table or in a companion inventory update.

## Unity Compatibility Matrix

| Unity version | CI/manual coverage | Notes |
|---|---|---|
| 2021.3 | Package minimum / current declared target | The package manifest declares `"unity": "2021.3"`; run the `Pi.UnityHarness` Editor tests manually. |

## Update Rule

Any future change under `unity/com.pi.pipeline.compat/` must update this file in the same change. CI should reject a compat diff that does not also touch `PATCHES.md`.

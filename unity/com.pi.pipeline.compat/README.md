# com.pi.pipeline.compat

`com.unity.pipeline@0.4.0-exp.1` 的 **2021.3 / 2022.x 兼容 fork**，由 `pi-unity-harness` 维护。

## 为什么需要

官方 `com.unity.pipeline` 声明 `"unity": "6000.0"`，UPM 不会给非 Unity 6 工程安装。
多数命令本身不依赖 Unity 6 API，但包内 Roslyn / HotReload CodeGen 在 2022 上编不过。

## 与官方的差异

| | 官方 (Unity 6) | compat (2021.3+) |
|---|---|---|
| UPM 名 | `com.unity.pipeline` | `com.unity.pipeline`（embedded 复制，displayName 标 `Pi Unity Pipeline Compat`） |
| 安装 | registry / embedded | `/unity-install` 自动复制本目录为 `Packages/com.unity.pipeline`（Unity 2022 的 versionDefines 只认 embedded/registry 包） |
| 程序集名 | `Unity.Pipeline` / `Unity.Pipeline.Editor` | **相同**（harness 的 `PI_UNITY_PIPELINE` 仍生效） |
| Roslyn `eval` / HotReload | ✅ | ❌ 已移除；请用 harness `unity_eval` |
| 命令面（uitree / input / vision / tests / scenes…） | ✅ | ✅（经 2022.3.14f1 验证） |

## 安装

`/unity-install` 会按工程 Unity 版本自动选择（非 6：把本目录复制为 embedded `com.unity.pipeline`）：

- **≥ 6000** → 官方 `com.unity.pipeline@0.4.0-exp.1`
- **&lt; 6000** → 复制本目录为 `Packages/com.unity.pipeline`（embedded）

也可手动复制本目录到工程 `Packages/com.unity.pipeline`，并在 `Packages/manifest.json` 写入：

```json
{
  "dependencies": {
    "com.unity.pipeline": "file:com.unity.pipeline",
    "com.unity.inputsystem": "1.7.0"
  }
}
```

## 许可

基于 Unity Companion License 的 `com.unity.pipeline` 源码派生，仅随 harness 用于兼容旧编辑器；Unity 6 工程请继续用官方包。

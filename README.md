# pi-unity-harness

中文 | [English](README.en.md)

pi-unity-harness 是 AI 编码代理（pi）与 Unity Editor 之间的桥接层。它让代理通过 `unity_*` 工具在 Unity Editor 内执行 C# 代码、触发编译与测试、读取场景与日志快照，并复用 Unity 官方 `com.unity.pipeline` 的命令体系。

- `native/` — Rust `cdylib`，在 Unity 进程内持有 named pipe server，域重载期间不销毁
- `unity/com.pi.unity-harness/` — Unity Editor 包，C# 侧负责主线程执行与域重载生命周期
- `.pi/extensions/pi-unity-harness/` — pi TypeScript 扩展，暴露连接、上下文快照、操作审计、Pipeline 与 eval 工具

## Features

- `unity_ping` / `unity_status`：检查 broker 在线状态与 Editor 运行状态
- `unity_eval` / `unity_eval_file`：在 Unity Editor 主线程执行短 C# 代码或多行 `.repl`/`.cs` 文件
- `unity_recompile`：触发脚本编译并返回结果
- `unity_snapshot`：有界获取 Editor 状态、活动场景层级、当前选择和近期日志
- `unity_timeline`：查询追加式操作审计（请求摘要、结果、耗时与成功状态）
- `unity_pipeline`：发现/执行 pipeline `[CliCommand]`；高频命令动态注册为 shortcut（如 `unity_run_tests`）
  - Unity 6+：官方 `com.unity.pipeline`；非 Unity 6（2021.3 / 2022）：内置 `com.pi.pipeline.compat` 兼容 fork

> `com.pi.pipeline.compat` 是 Unity 官方 `com.unity.pipeline` 的兼容 fork，保留其原始许可证（Unity Package Distribution License，见 `unity/com.pi.pipeline.compat/LICENSE.md`），仅用于非 Unity 6 项目的本地 embedded 安装。

## Requirements

- Windows（目前只实现了 named pipe 路径）
- Unity Editor 2021.3+

## Install

### 在 Unity 项目中安装

把 `unity/com.pi.unity-harness` 作为本地 UPM 包加入 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.pi.unity-harness": "file:../pi-unity-harness/unity/com.pi.unity-harness"
  }
}
```

Unity Editor 启动后自动初始化 native broker，写入 `Library/PiUnityHarness/bridge.json`，并在域重载后自动重连。

### 在 pi 中安装扩展

在 `~/.pi/agent/settings.json`（不是 `~/.pi/settings.json`）中注册为全局扩展：

```json
{
  "extensions": [
    "<pi-unity-harness-仓库路径>/.pi/extensions/pi-unity-harness"
  ],
  "pi-unity-harness": {
    "enabled": false
  }
}
```

`enabled` 默认 `false`（不注册任何 `unity_*` 工具），需要时手动开启：

```text
/unity-harness on              # 仅当前会话
/unity-harness on --persist    # 当前会话 + 写入 settings（下次默认也开）
/unity-harness off             # 关掉
/unity-harness status          # 查看 runtime / settings / 当前激活的 unity 工具
```

项目级覆盖写在 `<project>/.pi/settings.json`（同 key，项目优先于全局）；改完 `enabled` 后需新开 pi 会话（或 `/unity-harness on|off`）生效。

## Usage

```text
unity_ping
unity_status
unity_snapshot { maxDepth: 3, maxNodes: 500, logLimit: 50, logLevel: "error" }
unity_timeline { limit: 20, success: "failure" }
unity_eval { code: "UnityEngine.Debug.Log(123); 123" }
unity_eval_file { filePath: "Temp/PiUnityHarness/AgentScratch/probe.repl" }
unity_recompile
```

## License

本仓库代码（`native/`、`unity/com.pi.unity-harness/`、`.pi/`、`docs/`）以 MIT 协议发布，见 [LICENSE](LICENSE)。

`unity/com.pi.pipeline.compat/` 是 Unity 官方 `com.unity.pipeline` 的兼容 fork，按 Unity Package Distribution License 发布，见 `unity/com.pi.pipeline.compat/LICENSE.md`。

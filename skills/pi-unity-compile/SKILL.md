---
name: pi-unity-compile
description: >
  Trigger Unity script recompilation and wait for domain reload to complete
  via pi-unity compile. Use after modifying C# scripts or project assets to
  ensure zero compilation errors before verifying results.
---

# pi-unity-compile

触发 Unity 脚本编译并自动等待域重载（Domain Reload）完成，直至托管状态恢复为 `ready`。

## 基础用法

```bash
# 触发编译并等待完成
pi-unity compile

# 设置自定义超时（默认 120000 毫秒）
pi-unity compile --timeout 180000

# 结构化输出
pi-unity compile --json
```

## 自动化重载机制

1. CLI 向 Unity 发送 `recompile` 指令。
2. Unity 卸载当前 AppDomain 并重新编译所有修改的 C# 程序集（此时 IPC 连接断开）。
3. CLI 自动启动重连轮询，每 500ms 查询一次 `status`。
4. 当 `managedState == "ready"` 时返回成功并报告最新 `generation` 代次。

## 验证闭环准则

修改任意 `.cs` 脚本后，**必须**执行 `pi-unity compile`，并且只有在返回 `ExitCode 0` 且无编译错误时，方可宣称改动有效并进入后续测试验证环节。

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity-compile --event used`。


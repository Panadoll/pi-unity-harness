---
name: pi-unity-eval
description: >
  Execute C# expressions, statements, or script files in the Unity Editor main
  thread via pi-unity eval. Use for querying runtime state, setting values,
  instantiating objects, or executing multi-line .repl probe scripts.
---

# pi-unity-eval

在 Unity Editor 主线程中即时执行 C# 代码表达式或脚本文件，并返回求值结果。

## 基础用法

```bash
# 执行单行表达式
pi-unity eval "UnityEngine.SceneManagement.SceneManager.GetActiveScene().name"

# 执行多行或语句块
pi-unity eval "var go = UnityEngine.GameObject.Find(\"Main Camera\"); go != null ? go.transform.position.ToString() : \"null\";"

# 执行脚本文件（推荐用于复杂逻辑）
pi-unity eval -f "Temp/PiUnityHarness/AgentScratch/probe.repl"

# 输出 JSON
pi-unity eval "UnityEngine.Application.unityVersion" --json
```

## 最佳实践与红线

1. **复杂代码写文件**：多行或包含临时变量的逻辑，优先写入 `Temp/PiUnityHarness/AgentScratch/*.repl` 或 `*.cs` 文件，然后使用 `-f, --file` 执行，避免 shell 转义与多行引号问题。
2. **严禁在 eval 中触发域重载**：禁止调用 `AssetDatabase.Refresh()` 或其它触发编译/域重载的代码。触发编译请使用 `pi-unity compile`。
3. **主线程安全**：eval 代码在 Editor 主线程执行，避免长时间阻塞（如 `Thread.Sleep` 或同步死锁）。

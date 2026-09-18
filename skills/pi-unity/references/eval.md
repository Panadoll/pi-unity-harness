# eval

在 Unity Editor 主线程执行 C#，返回求值结果。最后一行写成裸表达式（值），不要写 `return`。

```bash
pi-unity eval "UnityEngine.SceneManagement.SceneManager.GetActiveScene().name"
pi-unity eval "var go = UnityEngine.GameObject.Find(\"Main Camera\"); go != null ? go.transform.position.ToString() : \"null\";"
pi-unity eval -f "Temp/PiUnityHarness/AgentScratch/probe.repl"
pi-unity eval "UnityEngine.Application.unityVersion" --json
```

## 写法

1. 最后一行是裸表达式，它的值就是结果。顶层 `return` 会被自动剥掉；块内 `return` 编译不过。
2. 需要分支或多个返回时，用 `Func<object>` 收尾包装（`return` 在包装内合法）：

```csharp
((System.Func<object>)(() =>
{
    var go = UnityEngine.GameObject.Find("Player");
    if (go == null) return "not found";
    return go.transform.position.ToString();
}))()
```

   包装内的局部变量属于该 lambda，不保留到下一次 eval。
3. 类型写全限定：预置 `using System;` 和 `using UnityEngine;`，裸写 `Object` 会 CS0104 歧义。用 `UnityEngine.Object`，或自己在文件里显式 `using UnityObject = UnityEngine.Object;`。

## 空安全探针

多行探针写 `Temp/PiUnityHarness/AgentScratch/*.repl`，最后一行仍是裸表达式；对象可能不存在时先判空：

```csharp
var go = UnityEngine.GameObject.Find("Player");
go == null ? "Player not found" : go.transform.position.ToString();
```

## 红线

1. 多行或临时变量写 `Temp/PiUnityHarness/AgentScratch/*.repl`，用 `-f`。避免 shell 转义把代码拆烂。
2. 不要在 eval 里调用 `AssetDatabase.Refresh()` 或其它会触发编译 / 域重载的 API。编译用 `pi-unity compile`。
3. 不要阻塞 Editor 主线程，不要同步死锁。这段代码占着主线程。
4. 别声明未经验证的 pragma；`.repl` 只认首行 `// #repl-mode: top-level|class|auto`。
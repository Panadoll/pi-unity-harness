# eval

在 Unity Editor 主线程执行 C#，返回求值结果。

```bash
pi-unity eval "UnityEngine.SceneManagement.SceneManager.GetActiveScene().name"
pi-unity eval "var go = UnityEngine.GameObject.Find(\"Main Camera\"); go != null ? go.transform.position.ToString() : \"null\";"
pi-unity eval -f "Temp/PiUnityHarness/AgentScratch/probe.repl"
pi-unity eval "UnityEngine.Application.unityVersion" --json
```

## 红线

1. 多行或临时变量写 `Temp/PiUnityHarness/AgentScratch/*.repl`，用 `-f`。避免 shell 转义把代码拆烂。
2. 不要在 eval 里调用 `AssetDatabase.Refresh()` 或其它会触发编译 / 域重载的 API。编译用 `pi-unity compile`。
3. 不要 `Thread.Sleep`，不要同步死锁。这段代码占着 Editor 主线程。

# run-tests

跑 Unity Test Framework。EditMode 或 PlayMode。

```bash
pi-unity run-tests --mode edit
pi-unity run-tests --mode play
pi-unity run-tests --mode edit --filter "VisionAnalyzerSettingsTests"
pi-unity run-tests --mode edit --json
```

默认超时 330 秒。大套件用 `--timeout <ms>` 加长。

PlayMode 依赖当前 Editor 会话。若返回 0 个测试，日志里有 `Run started: 0 test(s)`，先 `pi-unity compile`，还不行就重启 Editor。

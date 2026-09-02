---
name: pi-unity-run-tests
description: >
  Run Unity UTF test suites (EditMode or PlayMode) via pi-unity run-tests.
  Use when verifying C# refactors, running regression tests, or checking test
  pass/fail results with optional name filters.
---

# pi-unity-run-tests

快捷运行 Unity Test Framework (UTF) 自动化测试套件，支持 EditMode 与 PlayMode，并返回详细的测试通过率与耗时统计。

## 基础用法

```bash
# 运行全部 EditMode 测试（默认）
pi-unity run-tests --mode edit

# 运行 PlayMode 测试
pi-unity run-tests --mode play

# 过滤特定名称或命名空间的测试用例
pi-unity run-tests --mode edit --filter "VisionAnalyzerSettingsTests"

# 结构化 JSON 结果输出
pi-unity run-tests --mode edit --json
```

## 注意事项

- **超时配置**：大型测试套件默认超时为 330 秒（5.5 分钟），可通过 `--timeout <ms>` 延长。
- **PlayMode 注意点**：PlayMode 测试依赖 Editor 当前会话状态，若返回 0 个测试且日志提示 `Run started: 0 test(s)`，可先执行一次 `pi-unity compile` 或重启 Editor 恢复。

## 可观测性打点约定

使用本 skill 时先运行 `pi-unity mark --skill pi-unity-run-tests --event used`。


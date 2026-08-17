# 贡献指南

[English](CONTRIBUTING.en.md) | 中文

欢迎提交 issue 与 PR。请先阅读 [README](README.md) 了解项目结构。

## 开发环境

- Windows（native broker 仅实现 named pipe 路径）
- Rust toolchain（构建 `native/`）
- Node.js 18+（运行扩展测试）
- Unity Editor 2021.3+（验证 Editor 包与 pipeline 集成）

## 本地构建与测试

```bash
# native broker
./scripts/build-native.sh          # 或 PowerShell: ./scripts/build-native.ps1

# 扩展单元测试（node:test）
node --test .pi/extensions/pi-unity-harness/*.test.ts

# Rust 单元测试
cd native && cargo test
```

Unity 侧验证：把 `unity/com.pi.unity-harness` 以本地 UPM 包加入项目，运行
EditMode / PlayMode 测试（过滤器 `Pi.UnityHarness`）。

测试宿主必须满足：

- 宿主项目 `Packages/manifest.json` 的 `dependencies` 用 `file:` 引用本包；
- `testables` 数组包含 `"com.pi.unity-harness"`（否则包内测试程序集不会被
  Test Runner 发现，"绿了"不算数）；
- 验证命令（pi 会话内）：
  - `unity_run_tests(mode="editor", filter="Pi.UnityHarness")`
  - `unity_run_tests(mode="playmode", filter="Pi.UnityHarness")`

注意：PlayMode 测试依赖 Editor 会话状态，**每次会话的第一次 PlayMode 运行最
可靠**；若后续运行返回 0 个测试且 Console 出现 `Run started: 0 test(s)`，
重启 Editor 后重新执行。

## 提交规范

- 提交信息遵循 Conventional Commits（`feat:` / `fix:` / `docs:` / `chore:` / `refactor:` / `test:`）
- 不保留对内部项目、个人路径、凭据的引用
- 改动 Unity 包资源时，必须同时提交对应的 `.meta` 文件

## PR 流程

1. fork 并创建功能分支
2. 为改动补充测试（扩展逻辑用 node:test，C# 逻辑用 EditMode/PlayMode 测试）
3. 运行本地测试并确认通过
4. 提交 PR，说明改动动机与验证方式

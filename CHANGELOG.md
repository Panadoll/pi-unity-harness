# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与
[Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added

- 初始开源版本：native broker、Unity Editor 包、pi 扩展、文档

### Fixed

- 重编译闪退防御：PlayMode 运行中（场景存在活跃 PlayableGraph）直接触发强制同步重编译时，
  Domain Reload 销毁 Playable 会回调上一 AppDomain 的托管侧，访问失效 GC handle 导致编辑器
  SIGSEGV 闪退。新增 PiUnityRecompileGuard 守卫：编译前强制安全退出 PlayMode，等待非托管
  资源完全释放、进入 EditMode 稳定后再发起编译；等待状态经 SessionState 持久化跨 Domain
  Reload 存活，60s 超时兜底返回 playmode_exit_timeout，不挂住 pipe 端请求。
- 测试运行期间请求重编译返回 busy，避免打断运行中的测试（含 PlayMode 段）。
- 修正编译协调器延迟检查在 isCompiling 时丢失调度链、请求永不回调的问题。
- 新增 PiUnityRecompileGuard 契约测试（8 个用例，覆盖延迟、busy、超时、重新进入 PlayMode 等分支）。

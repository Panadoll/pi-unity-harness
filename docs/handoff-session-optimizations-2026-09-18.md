# Handoff：稳定性与体验优化后的验证与收尾

## 1. 接手目标

本轮已完成约定范围内的实现和隔离测试。下一位 agent 应优先做代码复核、
重跑基线，并在用户授权的专用 Unity 项目中补齐真实 Editor 验证，而不是重新实现已有修复。
“实现完成”不代表“真实 Unity 端到端集成已验证”。

## 2. 工作区与交付状态

- 仓库：`F:\Projects-Test\unity-ai-tool\pi-unity-harness`，Windows / PowerShell。
- 交接时分支：`master`；HEAD：`cf34e4c`。
- HEAD 标题：`feat(extension): 增加 Unity 实例发现与 harness/pipeline 安装命令`。
- 本轮改动仍在工作区，**未提交、未部署**；包含大量 tracked 修改和 untracked 新文件。
- 不要 reset、clean、批量覆盖或删除这些文件；不要把现有改动都认作下一位 agent 的产出。
- 若切换机器或 worktree，必须一并转移未提交修改及 untracked 文件，仅 checkout HEAD 不够。
- 开始前重新检查 `git status --short` 和 diff；交接记录是时间点快照。

## 3. 先读这些资料

1. `docs/session-optimization-implementation.md`：实际实现、验证和限制的当前台账。
2. `docs/session-2026-09-16-harness-optimization.md`：历史建议，不是全部已实施的承诺。
3. `C:\Users\yingbf\.augment\plans\pi-unity-harness-session-optimizations.md`。
4. `C:\Users\yingbf\.gemini\antigravity-cli\brain\082b95bc-edf4-417a-a27e-1a78d2016e5d\pi-unity-harness-optimization-plan.md`。
5. `README.md`、`docs/protocol.md`、`skills/pi-unity/SKILL.md` 及其 references。

外部计划可能无法跨机器访问；以代码和当前台账为依据，不要用历史建议覆盖已确认的范围。

## 4. 用户已确认的边界

- **Broker 多客户端支持暂缓**，当前仍为单客户端；不要借验证任务引入架构改造。
- **未经明确授权，不操作用户活动中的外部 Unity Editor / 项目**。
- 真实验证前先确认测试项目绝对路径、Unity 版本，以及是否允许安装本地包、重载和进入 PlayMode。
- 不擅自改变 PlayMode 默认行为、PNG 默认格式、默认尺寸或增加自动恢复运行行为。
- 不执行提交、部署、合并或新增外部依赖，除非另有授权。
- 不打印 token、会话凭据或原始敏感 stderr；日志只保留安全诊断及脱敏证据。

## 5. 已实现及不能回退的语义

### Native / 编译 / 连接

- UTF-8 安全采样与截断；mux 单请求 panic 隔离，后续请求可继续工作。
- 编译失败保留初始错误，CLI 返回退出码 1 和 `compiled:false`；mux 同样保留失败数据。
- 只有实际 recompile 成功确认后才观察 ready；no-op 成功不强制 generation 增加。
- `ForceUpdate` 不等于同步编译完成。
- 派发前的 managed-not-ready/reloading 重试受统一 deadline 限制；pipe busy 有独立诊断。
- **已派发、执行结果不确定、I/O 断开或 panic 的业务请求不能盲目重放**。

### TypeScript 扩展 / mux

- 恢复仅覆盖安全的未派发队列项，不重放 in-flight 请求。
- 子进程退出、signal、stderr 采用有界安全分类，不直接暴露原始 stderr。
- 项目绑定优先级：显式选择 → session cwd 最近项目 → 环境/发现兜底。
- 动态 pipeline 工具在成功业务连接后发现，并按 session generation 隔离过期结果。
- 未知命令只建议有限数量的可见近邻命令。

### Unity evaluator / 输入 / 观察

- return 适配以原始 `CS0127` 为门槛，不因源码包含 return 就重写其他编译错误。
- 保留安全的尾 return 适配与 Func 包装；覆盖分支、嵌套 return、组合脚本和表达式尾部。
- 修复末尾行注释导致的不完整输入，以及 Validate 适配成功后残留旧错误。
- 验证副作用仅执行一次、Task/IEnumerator 保留、运行时异常分类及失败后的恢复。
- 鼠标位置和有符号 delta 同步，覆盖 EventSystem 派发及 InputSystem fallback 路径。
- Vision JSON 结构化；capture/observe 保留 `embed:false` 提示契约。
- `embed:false` 和 UI-first 是使用建议，不代表禁止图片嵌入，也没有改变 PNG 默认行为。

## 6. 重点文件定位

以下路径均相对于仓库；Unity 路径前缀为 `unity/com.pi.unity-harness/`。

| 范围 | 入口 / 回归文件 |
|---|---|
| Native | `native/src/bin/pi_unity/{client,commands,discovery,main,output,schema}.rs` |
| Native 测试 | `native/src/bin/pi_unity/mock_pipe.rs`、`native/tests/compile_cli.rs`，以及各模块内测试 |
| 扩展 | `.pi/extensions/pi-unity-harness/index.ts`、`mux.test.ts`、`lifecycle.test.ts` |
| Eval | `Editor/PiUnityEvaluator.cs`、`.CodeScanner.cs`、`.Lint.cs` |
| 编译与命令 | `Editor/PiUnityCompileCoordinator.cs`、`Editor/PiUnityPipelineCommandExecutor.cs` |
| 输入 | `Editor/Capabilities/InputSystem/HarnessInputBackend.cs` |
| 视觉 | `Editor/Capabilities/Vision/{PlaytestVision,VisionJson}.cs` |
| EditMode | `Tests/Editor/PiUnityEvaluatorExecutionTests.cs`、`PiUnityEvaluatorLintTests.cs`、`PiUnityRecompileGuardTests.cs`、`PiUnityPipelineExecutorTests.cs` |
| 视觉测试 | `Tests/Editor/Vision/VisionJsonTests.cs`、`Tests/Editor/Playtest/PlaytestVisionAlgorithmTests.cs` |
| PlayMode | `Tests/PlayMode/Input/MouseSyncDuringDragTests.cs` |
| 独立 Eval 测试 | `scripts/test-evaluator-standalone.ps1` |

## 7. 已实际运行的验证

这些是上轮实测结果，不是本次 handoff 重新运行的结果。修改代码后请重新执行。

| 验证 | 上轮结果 |
|---|---|
| Native 全套 | 95 passed，exit 0：18 broker + 65 CLI unit + 12 process integration |
| TypeScript 扩展 | 50 passed、1 skipped，exit 0；跳过项涉及真实 Unity |
| 独立 evaluator | 编译 exit 0；19 组通过、0 失败；运行 exit 0 |
| diff 空白检查 | `git diff --check`，exit 0 |

在仓库根目录执行：

<augment_code_snippet mode="EXCERPT">
````powershell
cargo test --manifest-path native/Cargo.toml
node --test .pi/extensions/pi-unity-harness/*.test.ts
& ./scripts/test-evaluator-standalone.ps1
git diff --check
````
</augment_code_snippet>

- 分别记录每条命令退出码，不要让最后一条成功掩盖前面的失败。
- Node 命令依赖当前环境的原生 TypeScript 支持；若换环境，先核对版本与测试配置。
- 独立脚本使用 Unity 2022.3.14f1 的真实 Mono.CSharp，但 Unity 宿主 API 使用 stubs。
- 上轮 Unity Data：`F:\Program Files\Unity 2022.3.14f1\Editor\Data`；可用 `-UnityEditorData <path>` 覆盖。
- 独立编译仍有两个既有 `CS0649` 未赋值字段警告；不是测试失败。
- 新增 C# NUnit 测试尚未在真实 Editor 执行；独立语法/JSON 检查不能替代它们。

## 8. 下一轮建议顺序与验收标准

1. **只读复核与基线**：查看工作区差异、重跑上面三套测试，确认没有遗漏 untracked 测试文件。
2. **确认隔离测试环境**：没有明确授权的项目就先询问用户，不自动选活动 Editor。
3. **验证产物版本**：检查构建/安装流程，确保测试实例加载的是本轮 native 与 C# 代码，而非旧 DLL/CLI。
4. **运行 EditMode / PlayMode**：核对 asmdef、测试发现和依赖；记录实际执行、跳过和失败数量。
5. **真实编译链路**：在专用项目测试编译成功、no-op、临时编译错误、修复后恢复、域重载后连接恢复。
   失败不得假成功；deadline 不被重试绕过；产生错误的测试文件须限定位置并可恢复。
6. **真实输入链路**：验证多帧拖拽、正负 delta、静止归零、清理后基线重置；不得重复派发 press/release。
7. **观察与工具链路**：验证 capture/observe 输出、实际截图、工具发现及 session 切换后的 stale generation 隔离。
8. **小步修复并复测**：每个发现的问题先补/更新回归，再作最小修复，复跑相关测试及基线。
9. **交付证据**：更新 implementation 台账，记录版本、命令、退出码、测试结果、残留风险和未验证项。

完成标准：隔离测试保持通过；获授权的真实集成场景有可复核证据；未执行项明确标注，不能写成全部通过。

## 9. 不要误认为已完成的功能

Native `unity_status` 的项目身份字段、Broker 多客户端、`autoResumePlay`、`wait`/`play_probe` macros、
JPG/自动缩放默认值、visual compare/locate、新 `logs`/`analyze` 工具、DDOL-root 扩展、
snapshot filter schemas 和新 UI aliases 均未在本轮交付；只有用户重新确认范围后才开展。
# Session optimization implementation ledger

This is the current implementation and verification record. The historical
recommendations in `docs/session-2026-09-16-harness-optimization.md` and the
external plans were inputs only; those documents are not edited here.

External sources: `~/.augment/plans/pi-unity-harness-session-optimizations.md`
and `~/.gemini/antigravity-cli/brain/082b95bc-edf4-417a-a27e-1a78d2016e5d/pi-unity-harness-optimization-plan.md`.
The user selected stability/UX now and deferred broker multi-client support.

## Implemented scope

### Native / broker boundary

- Unicode-safe output sampling/truncation and per-request mux panic isolation
  are implemented. A panic leaves the request result unknown; it is not blindly
  replayed.
- Compile preserves the initial failure. Success requires the actual successful
  recompile acknowledgement, then a subsequent observed `ready` state with the
  broker generation returned. A generation increment is not required for a
  successful no-op compile. `ForceUpdate` does not imply synchronous compilation.
- Pre-dispatch `managed_reloading`/`managed_not_ready` retries stay within the
  caller deadline. Uncertain or already-dispatched requests are not replayed.
- Named-pipe busy is a distinct bounded diagnostic; it does not claim that a
  live Editor is absent.

### TypeScript extension / mux client

- Untouched, not-yet-dispatched queue entries retain their ownership boundary;
  dispatched or in-flight requests are not replayed after transport loss.
- Child exit/signal and bounded, sanitized stderr-category diagnostics are
  reported, never raw stderr. Binding priority is explicit selection, nearest
  session-cwd project, then inherited environment/discovery fallbacks.
  **Project identity is not yet exposed by the native
  `unity_status` payload; do not claim that status feature is implemented.**
- Dynamic pipeline tools load after a successful Unity business connection and
  are isolated by session generation; stale discovery cannot register tools in
  a later session. Broker multi-client support remains single-client/deferred.
- Unknown-command suggestions are bounded to nearest visible candidates.

### Evaluator, input, and capture UX

- Compatible expression/return blocks use the safe wrapper path; blanket
  `return` stripping is not used. Pointer and mouse deltas use actual movement,
  including drag movement.
- Vision JSON is structured and capture results keep `embed:false` as an
  advisory contract. The default PNG capture behavior is unchanged; UI-first
  `uitree` guidance is advisory rather than removal of image embedding.

## Deferred or not implemented

- Project identity in native `unity_status`; broker multi-client support.
- `autoResumePlay`, `wait`, and `play_probe` macros.
- JPG defaults, default resizing, visual compare/locate, new `logs`/`analyze`
  tools, DDOL-root expansion, snapshot filter schemas, and new UI aliases.

## Tests and reproducible commands

Run from the repository root unless noted:

<augment_code_snippet mode="EXCERPT">
````powershell
cargo test --manifest-path native/Cargo.toml
node --test .pi/extensions/pi-unity-harness/*.test.ts
& ./scripts/test-evaluator-standalone.ps1
````
</augment_code_snippet>

- Native: **95 passed, exit 0** (18 broker, 65 CLI unit, 12 process integration).
- TypeScript: **50 passed, 1 skipped, exit 0**.
- Standalone evaluator: **compile exit 0; 19 groups passed, 0 failed; exit 0**.
  Uses Unity 2022.3.14f1's real Mono.CSharp, with only Unity host API stubs.
  Override the installation using `-UnityEditorData <path>` when needed.
  Two existing unused-field warnings (CS0649) remain.
- `git diff --check`: **exit 0**.
- C# NUnit coverage for input/pipeline/capture was added but not run in a real
  Unity Editor. VisionJson standalone and coordinator syntax checks were
  reported passed; that is not Unity integration verification.

## Known limitations

Real Editor drag behavior, domain reload, end-to-end compile/ready, and actual
capture flows remain unverified. Run the added EditMode/PlayMode regressions in
a dedicated Unity test project before deployment; isolated tests do not prove
the complete Editor integration. No active Editor was operated for this work.

## Source cross-map

- Native behavior: `native/src/bin/pi_unity/{client,commands,discovery,main,output,schema}.rs`.
- Extension/mux behavior: `.pi/extensions/pi-unity-harness/{index,mux.test}.ts`.
- Unity behavior/tests: `PiUnityCompileCoordinator.cs`, input backend,
  `VisionJson.cs`, and the corresponding `Tests/Editor` fixtures.
## 验证轮补记（2026-09-18，真实 Editor：F:\UnityProjects\ctest，2022.3.14f1）

本节记录在真实 Editor 上重跑基线、修复三个真实缺陷的结果。详细证据与未验证项见
`docs/handoff-session-verification-2026-09-18.md`。

### 基线重跑（同一提交工作区）

| 命令 | 结果 |
|---|---|
| `cargo test --manifest-path native/Cargo.toml` | 98 passed, exit 0（18 broker + 68 CLI unit + 10 cli + 2 compile_cli） |
| `node --test .pi/extensions/pi-unity-harness/*.test.ts` | 51 passed, 0 skipped, exit 0 |
| `./scripts/test-evaluator-standalone.ps1` | compile exit 0；25 passed, 0 failed；exit 0 |
| `./scripts/probes/mono-csharp-anon-poison/run.ps1` | 观察型探针，复现污染与清缓存恢复 |
| `git diff --check` | exit 0 |

### 真实 Editor EditMode

- EditMode 发现 258 个测试（harness 208 + 项目 50）；harness 208 个：**205 passed / 3 failed**。
  `Temp/pipeline_test_status.json` 为原始证据。3 个失败见 handoff 第 6 节，尚未修复。
- PlayMode：0 个测试被发现，`Pi.UnityHarness.PlayMode.Tests` 的 `defineConstraints` 依赖
  `PI_UNITY_PLAYMODE_TESTS`，仓库与宿主工程都没有定义。

### 本轮修掉的缺陷

1. eval REPL 被 Mono.CSharp 匿名类型容器永久毒化（真实 Editor 可复现，域重载才能恢复）。
   修法：失败编译后清 `module.anonymous_types` 脏条目（不重建实例、不丢持久变量），
   反射不可用时才重建并明确提示持久变量失效；顶层带值 return 先走适配链以避免必然失败的
   raw 编译。回归：`PiUnityEvaluatorExecutionTests` 4 个新用例。
2. `unity_run_tests` 汇总恒为 0（假阴性）：`shape_run_tests` 读顶层 `summary`，而桥接信封是
   `{output, value}` 且用例字段为 PascalCase。修法：解信封 + 大小写不敏感取值。
   回归：`schema.rs` 3 个新单测。
3. 产物版本错位：扩展 `findPiUnityBinary()` 优先 `bin/pi-unity.exe`（9-10 旧构建），
   本轮 native 语义在真实链路未生效。已重建（`bin/` 与 `dist/` 均在 .gitignore 内，不入库）。

### 环境改动（仓库外，需 review 时知情）

- `F:\UnityProjects\ctest\Packages\manifest.json` 增加 `"testables": ["com.pi.unity-harness"]`，
  否则包内测试程序集不进 Test Runner（备份：`Temp/ctest-manifest-backup/manifest.json.orig`）。
- 清掉 6 个占用单客户端管道的旧 mux 进程；`bin/pi-unity.exe` 已用 release 重新构建。
- `unity/com.pi.unity-harness/Editor/Plugins/x86_64/pi_unity_harness_native.dll`（7-31 构建）
  早于 9-2 的 `lib.rs` 重构提交，本轮未改 lib，未替换；真实链路加载的是该旧 DLL。

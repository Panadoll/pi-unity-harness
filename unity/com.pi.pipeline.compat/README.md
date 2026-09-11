# com.pi.pipeline.compat

`com.pi.pipeline.compat` 是 Unity 官方 `com.unity.pipeline@0.6.0-exp.1` 的兼容派生版本。

## 设计目标与特性

1. **Unity 6+ 完全保留官方特性**：
   - 官方完整的 Roslyn 代码求值（`eval` / `eval_file` / `run_script`）。
   - 官方完整的运行时热重载（`HotReloadInPlaceILPostProcessor` 代码织入、`InPlaceReloadProcessor` 动态编译覆写、`HotReloadRegistry` 运行时分发）。
   - 官方完整的 IL 解释器（`Runtime/IlInterpreter`）及 Analyzers。
   - 官方新增的 `batch` 批处理、`project_audit`、运行时配置等全部命令。
2. **Unity 2021.3 / 2022.x 优雅兼容**：
   - 通过条件编译提供 API 抹平（`FindObjectsByType` 向后回退至 `FindObjectsOfType`）。
   - 避免在旧版本 Unity 中强依赖 Roslyn 编译器引发冲突，提供明确友好的能力降级与指引。
3. **针对 Named Pipe Bridge 的强化补丁**：
   - `BatchCommand.cs`：解耦对 HTTP server 线程变量的强依赖，支持在 Harness Named Pipe 线程下跨重载无缝批处理。
   - `TestCommands.cs` / `PipelineTestRunner.cs`：支持 `dirty_action` 场景策略，测试前自动预加载测试程序集。

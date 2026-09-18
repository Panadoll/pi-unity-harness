# Mono.CSharp 匿名类型污染探针

## 为什么存在

`unity_eval` 用长生命周期的 `Mono.CSharp.Evaluator` 承载 REPL 会话。真实 Editor 里发现：
**某次编译只要带上错误、且文本自身创建了匿名类型**（如 `new { a = 1 }`），Mono 会把半 emit 的
匿名类型容器留在持久的 `module.anonymous_types` 缓存里。之后该 Evaluator 实例的每一次
`Compile` 都会在 EmitContainer 阶段重复抛 `InternalErrorException("builder already exists")`，
实例内没有恢复路径，表现为整个 eval 会话死掉直到域重载。

根因在 `Mono.CSharp.Parameter.ApplyAttributes`：`builder` 字段是一次性标记，第二次走到同一个
`Parameter` 就抛异常；而异常中断的编译不会回滚 `ModuleContainer` 里的半成品容器。

修复（`Editor/PiUnityEvaluator.cs`）分三层：顶层带值 return 先走适配链（避免无意义失败编译）、
失败编译后清掉 `module.anonymous_types` 脏条目（主力，不重建实例因此不丢持久变量）、
以及只有两者都不可用时才重建实例。

## 探针做什么

直接调用 Mono.CSharp（绕开 harness 与 Unity 宿主），对四组场景各建一个全新 Evaluator：

| 场景 | 内容 | 结论 |
|---|---|---|
| A | 先成功声明匿名类型，再执行一条失败语句 | **不中毒**（匿名类型已正常 close，后续失败不影响） |
| B | 同一段文本里匿名类型 + 失败语句 | **中毒**；清缓存后恢复，`x` 因该次失败未落地而消失 |
| C | 先声明普通变量与匿名类型，再失败 | **不中毒**，`keep` 保持可用 |
| D | 失败文本里的匿名类型引用未知名字 | **中毒**；清缓存后恢复 |

输出里 `after` 一行为 `THROW InternalErrorException: builder already exists` 即表示中毒，
随后的 `clear anon cache entries removed = N` 与 `retry: ok compiled=True` 表示清缓存即可恢复。

## 运行

```powershell
& ./scripts/probes/mono-csharp-anon-poison/run.ps1
# Unity 装在别处时：
& ./scripts/probes/mono-csharp-anon-poison/run.ps1 -UnityEditorData 'D:\Unity\Editor\Data'
```

探针只做观察，退出码固定为 0；结果需要人工比对上面的表格。

harness 层的等价回归在 `unity/com.pi.unity-harness/Tests/Editor/PiUnityEvaluatorExecutionTests.cs`
（用例 `failed anonymous type compile keeps session state` /
`failed value return keeps session state`），可用
`& ./scripts/test-evaluator-standalone.ps1` 离线运行。

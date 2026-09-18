using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

namespace Pi.UnityHarness.Editor
{
    internal sealed partial class PiUnityEvaluator
    {
        internal sealed class EvalResult
        {
            public bool Ok;
            public string Output;
            public string Error;
            public string TypeName;
            public IEnumerator Coroutine;
            public bool IsCoroutine;
            public Task AsyncTask;
            public bool IsAsyncTask;

            public static EvalResult FromValue(object value)
            {
                if (value is IEnumerator coroutine)
                {
                    return new EvalResult
                    {
                        Ok = true,
                        IsCoroutine = true,
                        Coroutine = coroutine,
                        Output = "(coroutine)",
                        TypeName = "IEnumerator",
                    };
                }

                if (TryAsTask(value, out Task task))
                {
                    return new EvalResult
                    {
                        Ok = true,
                        IsAsyncTask = true,
                        AsyncTask = task,
                        Output = "(task)",
                        TypeName = task.GetType().FullName,
                    };
                }

                if (TryAsTaskLike(value, out Task taskLike))
                {
                    return new EvalResult
                    {
                        Ok = true,
                        IsAsyncTask = true,
                        AsyncTask = taskLike,
                        Output = "(task-like)",
                        TypeName = value.GetType().FullName,
                    };
                }

                return new EvalResult
                {
                    Ok = true,
                    Output = value == null ? "(null)" : value.ToString(),
                    TypeName = value == null ? "null" : value.GetType().FullName,
                };
            }

            public static EvalResult OkResult()
            {
                return new EvalResult { Ok = true, Output = "(ok)", TypeName = "void" };
            }

            public static EvalResult Fail(string error, string typeName)
            {
                return new EvalResult { Ok = false, Error = error, TypeName = typeName ?? "error" };
            }

            internal static bool TryAsTask(object value, out Task task)
            {
                task = value as Task;
                return task != null;
            }

            /// <summary>
            /// Support awaitables that expose GetAwaiter() with IsCompleted/OnCompleted/GetResult
            /// (e.g. ValueTask, custom awaitables) by wrapping them into a Task.
            /// </summary>
            internal static bool TryAsTaskLike(object value, out Task task)
            {
                task = null;
                if (value == null || value is Task)
                    return false;

                Type type = value.GetType();
                MethodInfo getAwaiter = type.GetMethod("GetAwaiter", Type.EmptyTypes);
                if (getAwaiter == null || getAwaiter.GetParameters().Length != 0)
                    return false;

                object awaiter;
                try
                {
                    awaiter = getAwaiter.Invoke(value, null);
                }
                catch
                {
                    return false;
                }

                if (awaiter == null)
                    return false;

                Type awaiterType = awaiter.GetType();
                PropertyInfo isCompletedProp = awaiterType.GetProperty("IsCompleted");
                MethodInfo onCompleted = awaiterType.GetMethod("OnCompleted", new[] { typeof(Action) });
                MethodInfo getResult = awaiterType.GetMethod("GetResult", Type.EmptyTypes);
                if (isCompletedProp == null || onCompleted == null || getResult == null)
                    return false;

                var tcs = new TaskCompletionSource<object>();
                Action complete = () =>
                {
                    try
                    {
                        object result = getResult.Invoke(awaiter, null);
                        tcs.TrySetResult(result);
                    }
                    catch (TargetInvocationException ex)
                    {
                        tcs.TrySetException(ex.InnerException ?? ex);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                };

                try
                {
                    bool isCompleted = isCompletedProp.GetValue(awaiter) is bool b && b;
                    if (isCompleted)
                        complete();
                    else
                        onCompleted.Invoke(awaiter, new object[] { complete });
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex is TargetInvocationException tie ? (tie.InnerException ?? tie) : ex);
                }

                task = tcs.Task;
                return true;
            }

            internal static object GetTaskResult(Task task)
            {
                if (task == null)
                    return null;

                Type type = task.GetType();
                if (!type.IsGenericType)
                    return null;

                // Task<TResult>
                PropertyInfo resultProp = type.GetProperty("Result");
                if (resultProp == null)
                    return null;

                try
                {
                    return resultProp.GetValue(task);
                }
                catch (TargetInvocationException ex)
                {
                    throw ex.InnerException ?? ex;
                }
            }
        }

        internal struct ReplDiagnostic
        {
            public string Hint;
            public string PatternViolation;
            public bool AutoFixed;
            public string Warnings;
        }

        private object _evaluator;
        private MethodInfo _evaluate;
        private MethodInfo _run;
        private MethodInfo _compile;
        private MethodInfo _compileSingle;
        private Type _compiledMethodType;
        private readonly StringWriter _reportWriter;
        private readonly StringBuilder _reportBuffer;
        private bool _ready;
        private string _initError;
        // Mono.CSharp 把匿名类型容器缓存在持久的 module.anonymous_types 里；一次失败的编译
        // 会把半 emit 的容器留在缓存，之后每次 Compile 都在 EmitContainer 阶段重抛
        // InternalErrorException("builder already exists")，实例内没有恢复路径。
        // 丢掉这些缓存条目可以在不重建实例、不丢持久变量的前提下恢复；重建只作兜底。
        private FieldInfo _moduleField;
        private FieldInfo _anonymousTypesField;
        private bool _lastCompileHadErrors;
        private bool _cacheCleanupUnavailable;
        private bool _compilerPoisoned;
        private string _rebuildReason;
        private bool _rebuildPending;

        public PiUnityEvaluator()
        {
            _reportBuffer = new StringBuilder();
            _reportWriter = new StringWriter(_reportBuffer);
            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                _initError = ex.ToString();
            }
        }

        private void Initialize()
        {
            Assembly asm = LoadMonoCSharpAssembly();
            Type evaluatorType = asm.GetType("Mono.CSharp.Evaluator")
                ?? throw new TypeLoadException("Mono.CSharp.Evaluator not found");
            Type settingsType = asm.GetType("Mono.CSharp.CompilerSettings")
                ?? throw new TypeLoadException("Mono.CSharp.CompilerSettings not found");
            Type contextType = asm.GetType("Mono.CSharp.CompilerContext")
                ?? throw new TypeLoadException("Mono.CSharp.CompilerContext not found");
            Type printerBaseType = asm.GetType("Mono.CSharp.ReportPrinter")
                ?? throw new TypeLoadException("Mono.CSharp.ReportPrinter not found");

            object settings = Activator.CreateInstance(settingsType);
            TrySetMember(settings, "GenerateDebugInfo", false);
            TrySetMember(settings, "WarningLevel", 0);

            object printer = CreateReportPrinter(asm);
            ConstructorInfo contextCtor = contextType.GetConstructor(new[] { settingsType, printerBaseType })
                ?? throw new MissingMethodException("CompilerContext ctor not found");
            object context = contextCtor.Invoke(new[] { settings, printer });

            ConstructorInfo evaluatorCtor = evaluatorType.GetConstructor(new[] { contextType })
                ?? throw new MissingMethodException("Evaluator ctor not found");
            _evaluator = evaluatorCtor.Invoke(new[] { context });

            _moduleField = evaluatorType.GetField(
                "module", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Type moduleType = asm.GetType("Mono.CSharp.ModuleContainer");
            _anonymousTypesField = moduleType?.GetField(
                "anonymous_types", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            _evaluate = evaluatorType.GetMethod("Evaluate", new[]
            {
                typeof(string), typeof(object).MakeByRefType(), typeof(bool).MakeByRefType()
            });
            _run = evaluatorType.GetMethod("Run", new[] { typeof(string) });
            _compiledMethodType = asm.GetType("Mono.CSharp.CompiledMethod");
            if (_compiledMethodType != null)
            {
                _compile = evaluatorType.GetMethod("Compile", new[]
                {
                    typeof(string), _compiledMethodType.MakeByRefType()
                });
            }
            _compileSingle = evaluatorType.GetMethod("Compile", new[] { typeof(string) });

            if (_run == null)
                throw new MissingMethodException("Evaluator.Run not found");
            if (_evaluate == null && _compile == null && _compileSingle == null)
                throw new MissingMethodException("Evaluator Evaluate/Compile not found");

            ReferenceLoadedAssemblies(evaluatorType);
            Run("using System;");
            Run("using System.IO;");
            Run("using System.Linq;");
            Run("using System.Collections;");
            Run("using System.Collections.Generic;");
            Run("using System.Threading.Tasks;");
            Run("using System.Text;");
            Run("using System.Text.RegularExpressions;");
            Run("using UnityEngine;");
            Run("using UnityEditor;");
            // UnityObject 别名：消解 Object 歧义时给出提示（见 ClassifyError CS0104）的出路，
            // 同时保留 using UnityEngine 使 Unity 类型可直接使用。
            Run("using UnityObject = UnityEngine.Object;");

            _reportBuffer.Clear();
            _ready = true;
        }

        public EvalResult Eval(string code)
        {
            EvalResult result = EvalCore(code);
            return DecorateRebuild(result);
        }

        private EvalResult EvalCore(string code)
        {
            string source = NormalizeInput(code);
            if (string.IsNullOrEmpty(source))
                return EvalResult.OkResult();
            if (!_ready)
                return EvalResult.Fail("RUNTIME ERROR: evaluator not initialized\n" + (_initError ?? string.Empty), "runtime_error");

            string pragmaMode;
            source = ExtractPragma(source, out pragmaMode);
            if (string.IsNullOrEmpty(source))
                return EvalResult.OkResult();

            string pragmaError = ValidatePragmaMode(pragmaMode, source);
            if (pragmaError != null)
                return EvalResult.Fail(pragmaError, "compile_error");

            ReplDiagnostic lintDiagnostic;
            source = PreLint(source, out lintDiagnostic);

            try
            {
                EvalResult result;
                if (TryEvalComposite(source, out result))
                    return result;

                string compileError;
                if (TryEvalTail(source, out result, out compileError))
                    return result;

                string enhanced = EnhanceCompileError(compileError ?? "COMPILE ERROR: unknown compile failure", source);
                return EvalResult.Fail(enhanced, "compile_error");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                // Compile exceptions are handled at the compiler boundary, not by type name.
                return EvalResult.Fail("RUNTIME ERROR: " + inner.Message, "runtime_error");
            }
            catch (Exception ex)
            {
                return EvalResult.Fail("RUNTIME ERROR: " + ex.GetType().Name + ": " + ex.Message, "runtime_error");
            }
        }

        public string Validate(string code)
        {
            string result = ValidateCore(code);
            // "(valid)" 时保留待报状态，交给紧随其后的 Eval 如实告知。
            if (_rebuildPending && result != "(valid)")
                return (result ?? string.Empty) + "\n" + RebuildNote();
            return result;
        }

        private string ValidateCore(string code)
        {
            string source = NormalizeInput(code);
            if (string.IsNullOrEmpty(source))
                return "(valid)";
            if (!_ready)
                return "COMPILE ERROR: evaluator not initialized\n" + (_initError ?? string.Empty);

            string pragmaMode;
            source = ExtractPragma(source, out pragmaMode);
            if (string.IsNullOrEmpty(source))
                return "(valid)";

            string pragmaError = ValidatePragmaMode(pragmaMode, source);
            if (pragmaError != null)
                return pragmaError;

            ReplDiagnostic lintDiagnostic;
            source = PreLint(source, out lintDiagnostic);

            try
            {
                string validationError;
                if (TryValidateComposite(source, out validationError))
                    return string.IsNullOrEmpty(validationError) ? "(valid)" : validationError;

                if (TryValidateTail(source, out validationError))
                    return "(valid)";

                return validationError != null
                    ? EnhanceCompileError(validationError, source)
                    : "COMPILE ERROR: unknown compile failure";
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return EnhanceCompileError("COMPILE ERROR: " + inner.Message, source);
            }
            catch (Exception ex)
            {
                return EnhanceCompileError("COMPILE ERROR: " + ex.Message, source);
            }
        }

        /// <summary>
        /// 编译期间检测到 Mono 内部错误并重建了编译器实例时，如实告知调用方：
        /// 重建会丢掉本会话此前的持久变量/类型声明，不能静默。只交付一次。
        /// </summary>
        private EvalResult DecorateRebuild(EvalResult result)
        {
            if (!_rebuildPending || result == null)
                return result;

            _rebuildPending = false;
            if (result.Ok)
            {
                result.Output = RebuildNote() + "\n" + (result.Output ?? string.Empty);
                return result;
            }

            result.Error = (result.Error ?? string.Empty) + "\n" + RebuildNote();
            return result;
        }

        private string RebuildNote()
        {
            return "[harness] Mono 编译器实例已重建（Mono 内部错误: " + (_rebuildReason ?? "unknown")
                + "）；本会话之前的持久变量/类型声明已失效，需要重新声明。";
        }

        /// <summary>
        /// 重建 Mono.CSharp 编译器实例（新的 CompilerContext/Evaluator，并重新导入默认 using）。
        /// 只在编译器状态被污染或初始化失败时调用，调用前不得执行过用户代码。
        /// </summary>
        private void RebuildCompiler()
        {
            _compilerPoisoned = false;
            _rebuildPending = true;
            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                _ready = false;
                _initError = ex.ToString();
            }
        }

        private bool TryEvalComposite(string code, out EvalResult result)
        {
            result = null;
            CompositeScript script;
            if (!TrySplitCompositeScript(code, out script))
                return false;

            string compileError;
            foreach (string usingDirective in script.UsingDirectives)
            {
                if (!TryEvalDirect(usingDirective, out result, out compileError))
                {
                    result = EvalResult.Fail(EnhanceCompileError(compileError, code), "compile_error");
                    return true;
                }
            }

            foreach (string declaration in script.Declarations)
            {
                if (!TryEvalDirect(declaration, out result, out compileError))
                {
                    result = EvalResult.Fail(EnhanceCompileError(compileError, code), "compile_error");
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(script.Tail))
            {
                result = EvalResult.OkResult();
                return true;
            }

            if (!TryEvalTail(script.Tail, out result, out compileError))
                result = EvalResult.Fail(EnhanceCompileError(compileError, code), "compile_error");
            return true;
        }

        private bool TryValidateComposite(string code, out string validationError)
        {
            validationError = null;
            CompositeScript script;
            if (!TrySplitCompositeScript(code, out script))
                return false;

            foreach (string usingDirective in script.UsingDirectives)
            {
                if (!TryValidateDirect(usingDirective, out validationError))
                    return true;
            }

            foreach (string declaration in script.Declarations)
            {
                if (!TryValidateDirect(declaration, out validationError))
                    return true;
            }

            if (!string.IsNullOrWhiteSpace(script.Tail) && !TryValidateTail(script.Tail, out validationError))
                return true;

            return true;
        }

        /// <summary>
        /// 执行语句尾部（分割 using/类型声明后的剩余部分）。
        /// 顶层 return 适配全部放这里，PreLint 不再剥离：
        /// 1) 无顶层带值 return 时先原样编译（裸表达式 / 普通语句 / void return 走这里）；
        /// 2) 顶层带值 return 在 void 交互宿主里必然 CS0127，原样编译不可能成功却会污染
        ///    Mono 持久容器（匿名类型等），因此这类输入直接先进适配链；
        /// 3) 适配链：先试“安全单条最终 return”剥离，再包成立即调用的 System.Func&lt;object&gt;；
        /// 4) 适配全部失败时，带值 return 的输入再原样编译一次，拿到真实的 CS0127 诊断，
        ///    保持与历史一致的报错；该尝试若污染实例，由 TryCompileGuarded 重建兜底。
        /// 所有适配都先 compile-only，失败也不会执行过用户代码；最多只执行一次。
        /// </summary>
        private bool TryEvalTail(string code, out EvalResult result, out string compileError)
        {
            result = null;
            compileError = null;

            bool valueReturnTail = HasTopLevelValueReturn(code);
            if (!valueReturnTail)
            {
                if (TryEvalDirect(code, out result, out compileError))
                    return true;

                if (!ShouldAdaptTopLevelReturn(compileError, code))
                    return false;
            }

            string originalError = compileError;
            string fallbackNotes = null;

            string stripped = TryStripSingleFinalReturn(code);
            if (stripped != null)
            {
                string strippedError;
                if (TryEvalDirect(stripped, out result, out strippedError))
                    return true;
                fallbackNotes = "[strip-adapt failed] " + strippedError;
            }

            string wrapped;
            if (TryWrapTailInFunc(code, out wrapped))
            {
                string wrappedError;
                if (TryEvalDirect(wrapped, out result, out wrappedError))
                    return true;
                string note = "[wrap-adapt failed] " + wrappedError;
                fallbackNotes = fallbackNotes == null ? note : fallbackNotes + "\n" + note;
            }

            if (valueReturnTail)
            {
                string rawError;
                if (TryEvalDirect(code, out result, out rawError))
                    return true;
                originalError = rawError;
            }

            // 保留原始错误（更贴近用户源码），附带回退失败的诊断
            compileError = fallbackNotes == null ? originalError : originalError + "\n" + fallbackNotes;
            return false;
        }

        /// <summary>
        /// Validate 版 TryEvalTail：只编译不执行，适配规则与 Eval 完全一致。
        /// </summary>
        private bool TryValidateTail(string code, out string validationError)
        {
            validationError = null;

            bool valueReturnTail = HasTopLevelValueReturn(code);
            if (!valueReturnTail)
            {
                if (TryValidateDirect(code, out validationError))
                    return true;

                if (!ShouldAdaptTopLevelReturn(validationError, code))
                    return false;
            }

            string originalError = validationError;
            string fallbackNotes = null;

            string stripped = TryStripSingleFinalReturn(code);
            if (stripped != null)
            {
                string strippedError;
                if (TryValidateDirect(stripped, out strippedError))
                {
                    validationError = null;
                    return true;
                }
                fallbackNotes = "[strip-adapt failed] " + strippedError;
            }

            string wrapped;
            if (TryWrapTailInFunc(code, out wrapped))
            {
                string wrappedError;
                if (TryValidateDirect(wrapped, out wrappedError))
                {
                    validationError = null;
                    return true;
                }
                string note = "[wrap-adapt failed] " + wrappedError;
                fallbackNotes = fallbackNotes == null ? note : fallbackNotes + "\n" + note;
            }

            if (valueReturnTail)
            {
                string rawError;
                if (TryValidateDirect(code, out rawError))
                {
                    validationError = null;
                    return true;
                }
                originalError = rawError;
            }

            validationError = fallbackNotes == null ? originalError : originalError + "\n" + fallbackNotes;
            return false;
        }

        private bool TryEvalDirect(string code, out EvalResult result, out string compileError)
        {
            result = null;
            compileError = null;
            _reportBuffer.Clear();

            object compiled;
            string partial;
            string compileFailure;
            CompileOutcome outcome = TryCompileGuarded(code, out compiled, out partial, out compileFailure);
            if (outcome == CompileOutcome.Compiled)
            {
                string diagnostics = _reportBuffer.ToString().Trim();
                if (HasCompileErrors(diagnostics))
                {
                    _lastCompileHadErrors = true;
                    compileError = "COMPILE ERROR: " + diagnostics;
                    return false;
                }
                if (!string.IsNullOrEmpty(partial))
                {
                    _lastCompileHadErrors = true;
                    compileError = "COMPILE ERROR: incomplete input: " + partial;
                    return false;
                }
                if (compiled == null)
                {
                    result = EvalResult.OkResult();
                    return true;
                }

                // CompiledMethod leaves its ref argument untouched for void statements.
                // A sentinel distinguishes that case from an expression returning null.
                object noResult = new object();
                object value;
                try
                {
                    object[] invokeArgs = { noResult };
                    ((Delegate)compiled).DynamicInvoke(invokeArgs);
                    value = invokeArgs[0];
                }
                catch (TargetInvocationException ex)
                {
                    // 已编译成功的委托执行阶段抛出的异常永远是运行时错误
                    // （即使异常类型恰好叫 InternalErrorException），与编译错误区分。
                    Exception inner = ex.InnerException ?? ex;
                    result = EvalResult.Fail(
                        "RUNTIME ERROR: " + (string.IsNullOrEmpty(inner.Message) ? inner.GetType().Name : inner.Message),
                        "runtime_error");
                    return true;
                }
                catch (Exception ex)
                {
                    result = EvalResult.Fail("RUNTIME ERROR: " + ex.GetType().Name + ": " + ex.Message, "runtime_error");
                    return true;
                }

                result = ReferenceEquals(value, noResult) ? EvalResult.OkResult() : EvalResult.FromValue(value);
                return true;
            }

            // Evaluate 仅作为 Compile API 缺失时的回退；编译失败绝不落到 Evaluate
            // （避免重复编译失败，并保证 return 适配 strip/wrap 有机会执行）。
            if (outcome == CompileOutcome.ApiUnavailable && _evaluate != null)
            {
                result = EvalWithEvaluate(code);
                return true;
            }

            compileError = "COMPILE ERROR: " + compileFailure;
            return false;
        }

        private bool TryValidateDirect(string code, out string validationError)
        {
            validationError = null;
            _reportBuffer.Clear();

            object compiled;
            string partial;
            string compileFailure;
            CompileOutcome outcome = TryCompileGuarded(code, out compiled, out partial, out compileFailure);
            if (outcome != CompileOutcome.Compiled)
            {
                // Validate 不执行；Evaluate 也不参与验证（验证只依赖 Compile API）
                validationError = "COMPILE ERROR: " + compileFailure;
                return false;
            }

            string errors = _reportBuffer.ToString().Trim();
            if (HasCompileErrors(errors))
            {
                _lastCompileHadErrors = true;
                validationError = "COMPILE ERROR: " + errors;
                return false;
            }
            if (!string.IsNullOrEmpty(partial))
            {
                _lastCompileHadErrors = true;
                validationError = "COMPILE ERROR: incomplete input: " + partial;
                return false;
            }

            return true;
        }

        private EvalResult EvalWithEvaluate(string code)
        {
            object[] args = { code + "\n", null, false };
            try
            {
                _evaluate.Invoke(_evaluator, args);
            }
            catch (TargetInvocationException ex)
            {
                // Legacy Evaluate has no separate compile phase; only compiler reports
                // provide evidence of compilation failure. Never infer it from a type name.
                string errors = _reportBuffer.ToString().Trim();
                if (HasCompileErrors(errors))
                    return EvalResult.Fail("COMPILE ERROR: " + errors, "compile_error");
                Exception inner = ex.InnerException ?? ex;
                return EvalResult.Fail("RUNTIME ERROR: " + inner.Message, "runtime_error");
            }

            string diagnostics = _reportBuffer.ToString().Trim();
            if (HasCompileErrors(diagnostics))
                return EvalResult.Fail("COMPILE ERROR: " + diagnostics, "compile_error");
            if (!string.IsNullOrEmpty(diagnostics))
                return EvalResult.Fail(diagnostics, "diagnostic");

            bool hasResult = args[2] is bool value && value;
            return hasResult ? EvalResult.FromValue(args[1]) : EvalResult.OkResult();
        }

        private bool TryCompile(string code, out object compiled, out string partial, out string failure)
        {
            // NormalizeInput/composite splitting trim trailing whitespace. Terminate
            // an EOF line comment before Mono adds its interactive-input terminator;
            // otherwise complete expressions can be reported as incomplete input.
            // This is lexical termination only: adaptation still requires CS0127.
            code += "\n";
            failure = null;
            if (_compile != null)
            {
                object[] args = { code, null };
                partial = (string)_compile.Invoke(_evaluator, args);
                compiled = args[1];
                return true;
            }

            if (_compileSingle != null)
            {
                compiled = _compileSingle.Invoke(_evaluator, new object[] { code });
                partial = null;
                return true;
            }

            compiled = null;
            partial = null;
            failure = "Mono.CSharp.Evaluator.Compile API unavailable";
            return false;
        }

        /// <summary>
        /// 带防护的编译调用：Mono 编译器（如 InternalErrorException）通过反射抛出时
        /// 转为 compile error 字符串返回，而不是冒泡成运行时错误。
        /// 三态结果：Compiled=编译成功（可能含诊断）；Failed=编译失败；
        /// ApiUnavailable=Compile API 不存在（仅此状态才允许回退 Evaluate）。
        /// 编译失败从未执行用户代码，因此后续适配（strip/wrap）不存在副作用回放。
        /// 异常路径会保留报告缓冲区里已有的诊断（异常前可能已打印 CS0127 等）。
        /// </summary>
        /// <summary>
        /// 编译失败后 Mono 会把半 emit 的匿名类型容器留在持久缓存里，下一条 Compile 会重走
        /// 该容器并抛 InternalErrorException。丢弃这些缓存条目即可继续使用同一个实例，
        /// 因此不会丢持久变量；反射句柄缺失时置位 _cacheCleanupUnavailable，退化为重建。
        /// </summary>
        private void PrepareCompilerForCompile()
        {
            if (!_lastCompileHadErrors || _cacheCleanupUnavailable)
                return;

            _lastCompileHadErrors = false;
            if (_moduleField == null || _anonymousTypesField == null)
            {
                _cacheCleanupUnavailable = true;
                return;
            }

            try
            {
                object module = _moduleField.GetValue(_evaluator);
                object cache = module == null ? null : _anonymousTypesField.GetValue(module);
                if (cache is IDictionary dictionary && dictionary.Count > 0)
                    dictionary.Clear();
            }
            catch (Exception ex)
            {
                _cacheCleanupUnavailable = true;
                LogVerbose("anonymous type cache cleanup unavailable: " + ex.Message);
            }
        }

        private CompileOutcome TryCompileGuarded(string code, out object compiled, out string partial, out string failure)
        {
            PrepareCompilerForCompile();

            CompileOutcome outcome = TryCompileOnce(code, out compiled, out partial, out failure);
            if (outcome != CompileOutcome.Compiled)
            {
                // 编译带错误就可能留下半 emit 的匿名类型容器；下一条 Compile 前清理。
                _lastCompileHadErrors = true;

                // 兜底：缓存清理没生效（或遇到其它内部错误）时重建编译器实例；
                // 编译阶段从未执行用户代码，重试不存在副作用回放。
                if (_compilerPoisoned && _ready)
                {
                    RebuildCompiler();
                    outcome = TryCompileOnce(code, out compiled, out partial, out failure);
                }
            }

            return outcome;
        }

        private CompileOutcome TryCompileOnce(string code, out object compiled, out string partial, out string failure)
        {
            try
            {
                if (TryCompile(code, out compiled, out partial, out failure))
                    return CompileOutcome.Compiled;
                if (failure != null && failure.IndexOf("API unavailable", StringComparison.Ordinal) >= 0)
                    return CompileOutcome.ApiUnavailable;
                return CompileOutcome.Failed;
            }
            catch (TargetInvocationException ex)
            {
                compiled = null;
                partial = null;
                failure = FormatCompilerException(ex.InnerException ?? ex);
                MarkCompilerPoisoned(ex.InnerException ?? ex);
                AppendBufferDiagnostics(ref failure);
                return CompileOutcome.Failed;
            }
            catch (Exception ex)
            {
                compiled = null;
                partial = null;
                failure = FormatCompilerException(ex);
                MarkCompilerPoisoned(ex);
                AppendBufferDiagnostics(ref failure);
                return CompileOutcome.Failed;
            }
        }

        /// <summary>
        /// Mono.CSharp 抛出 InternalErrorException 说明编译器内部状态已不可信：该类型是
        /// 持久容器被半 emit 污染的强信号（真实 Editor 与独立 Mono.CSharp 都能复现），
        /// 后续每次编译都会重复抛同一异常。标记后由 TryCompileGuarded 重建实例。
        /// </summary>
        private void MarkCompilerPoisoned(Exception ex)
        {
            if (ex == null || ex.GetType().FullName != "Mono.CSharp.InternalErrorException")
                return;

            _compilerPoisoned = true;
            _rebuildReason = ex.Message;
        }

        /// <summary>
        /// 编译异常路径：把报告缓冲区里已有的诊断（如 CS0127）追加到 failure，
        /// 使 ShouldAdaptTopLevelReturn 能识别并触发 return 适配。
        /// </summary>
        private void AppendBufferDiagnostics(ref string failure)
        {
            string bufferDiag = _reportBuffer.ToString().Trim();
            if (!string.IsNullOrEmpty(bufferDiag))
                failure += "\n" + bufferDiag;
        }

        private enum CompileOutcome
        {
            Compiled,
            Failed,
            ApiUnavailable,
        }

        private static string FormatCompilerException(Exception ex)
        {
            if (ex == null)
                return "unknown compiler failure";
            return string.IsNullOrEmpty(ex.Message)
                ? ex.GetType().Name
                : ex.GetType().Name + ": " + ex.Message;
        }

        private static string NormalizeInput(string code)
        {
            return string.IsNullOrEmpty(code) ? string.Empty : code.Trim().TrimStart('\uFEFF');
        }

        private void Run(string code)
        {
            _run.Invoke(_evaluator, new object[] { code });
        }

        private static bool HasCompileErrors(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics))
                return false;

            return diagnostics.StartsWith("error ", StringComparison.Ordinal)
                || diagnostics.IndexOf("\nerror ", StringComparison.Ordinal) >= 0
                || diagnostics.IndexOf(": error ", StringComparison.Ordinal) >= 0
                || diagnostics.IndexOf("error CS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void TrySetMember(object target, string name, object value)
        {
            if (target == null)
                return;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = target.GetType();

            PropertyInfo property = type.GetProperty(name, Flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value, null);
                return;
            }

            FieldInfo field = type.GetField(name, Flags);
            if (field != null)
                field.SetValue(target, value);
        }

        private Assembly LoadMonoCSharpAssembly()
        {
            try
            {
                return Assembly.Load("Mono.CSharp");
            }
            catch
            {
                string contentsDir = EditorApplication.applicationContentsPath;
                string[] paths =
                {
                    Path.Combine(contentsDir, "Resources", "Scripting", "MonoBleedingEdge", "lib", "mono", "4.7.1-api", "Mono.CSharp.dll"),
                    Path.Combine(contentsDir, "Resources", "Scripting", "MonoBleedingEdge", "lib", "mono", "4.5", "Mono.CSharp.dll"),
                    Path.Combine(contentsDir, "MonoBleedingEdge", "lib", "mono", "4.7.1-api", "Mono.CSharp.dll"),
                    Path.Combine(contentsDir, "MonoBleedingEdge", "lib", "mono", "4.5", "Mono.CSharp.dll"),
                };
                for (int i = 0; i < paths.Length; i++)
                {
                    if (File.Exists(paths[i]))
                        return Assembly.LoadFrom(paths[i]);
                }
            }
            throw new FileNotFoundException("Cannot load Mono.CSharp assembly");
        }

        private object CreateReportPrinter(Assembly asm)
        {
            Type streamPrinterType = asm.GetType("Mono.CSharp.StreamReportPrinter");
            if (streamPrinterType != null)
                return Activator.CreateInstance(streamPrinterType, (TextWriter)_reportWriter);

            Type consolePrinterType = asm.GetType("Mono.CSharp.ConsoleReportPrinter")
                ?? throw new TypeLoadException("Mono.CSharp report printer not found");
            ConstructorInfo writerCtor = consolePrinterType.GetConstructor(new[] { typeof(TextWriter) });
            return writerCtor != null
                ? writerCtor.Invoke(new object[] { _reportWriter })
                : Activator.CreateInstance(consolePrinterType);
        }

        private void ReferenceLoadedAssemblies(Type evaluatorType)
        {
            MethodInfo referenceAssembly = evaluatorType.GetMethod("ReferenceAssembly", new[] { typeof(Assembly) });
            if (referenceAssembly == null)
                return;

            HashSet<string> skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mscorlib",
                "System",
                "System.Core",
                "System.Xml",
                "System.Xml.Linq",
                "System.Runtime",
                "System.Numerics",
                "System.Data",
                "System.Drawing",
                "netstandard",
                "Mono.CSharp",
            };
            HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.IsDynamic)
                    continue;

                string name = assembly.GetName().Name;
                if (skipped.Contains(name) || !added.Add(name))
                    continue;

                try
                {
                    referenceAssembly.Invoke(_evaluator, new object[] { assembly });
                }
                catch
                {
                }
            }
        }

        private static void LogVerbose(string message)
        {
        }
    }
}

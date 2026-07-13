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
            Run("using UnityEngine;");
            Run("using UnityEditor;");

            _reportBuffer.Clear();
            _ready = true;
        }

        public EvalResult Eval(string code)
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
                if (TryEvalDirect(source, out result, out compileError))
                    return result;

                string enhanced = EnhanceCompileError(compileError ?? "COMPILE ERROR: unknown compile failure", source);
                return EvalResult.Fail(enhanced, "compile_error");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                string typeName = inner.GetType().Name;
                string prefix = typeName == "InternalErrorException" ? "COMPILE ERROR" : "RUNTIME ERROR";
                return EvalResult.Fail(prefix + ": " + inner.Message, typeName == "InternalErrorException" ? "compile_error" : "runtime_error");
            }
            catch (Exception ex)
            {
                return EvalResult.Fail("RUNTIME ERROR: " + ex.GetType().Name + ": " + ex.Message, "runtime_error");
            }
        }

        public string Validate(string code)
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

                if (TryValidateDirect(source, out validationError))
                    return "(valid)";

                return validationError ?? "COMPILE ERROR: unknown compile failure";
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                return "COMPILE ERROR: " + inner.Message;
            }
            catch (Exception ex)
            {
                return "COMPILE ERROR: " + ex.Message;
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

            if (!TryEvalDirect(script.Tail, out result, out compileError))
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

            if (!string.IsNullOrWhiteSpace(script.Tail) && !TryValidateDirect(script.Tail, out validationError))
                return true;

            return true;
        }

        private bool TryEvalDirect(string code, out EvalResult result, out string compileError)
        {
            result = null;
            compileError = null;
            _reportBuffer.Clear();

            object compiled;
            string partial;
            string compileFailure;
            if (TryCompile(code, out compiled, out partial, out compileFailure))
            {
                string diagnostics = _reportBuffer.ToString().Trim();
                if (HasCompileErrors(diagnostics))
                {
                    compileError = "COMPILE ERROR: " + diagnostics;
                    return false;
                }
                if (!string.IsNullOrEmpty(partial))
                {
                    compileError = "COMPILE ERROR: incomplete input: " + partial;
                    return false;
                }
                if (compiled == null)
                {
                    result = EvalResult.OkResult();
                    return true;
                }

                object[] invokeArgs = { null };
                ((Delegate)compiled).DynamicInvoke(invokeArgs);
                object value = invokeArgs[0];
                result = value == null ? EvalResult.OkResult() : EvalResult.FromValue(value);
                return true;
            }

            if (_evaluate != null)
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
            if (!TryCompile(code, out compiled, out partial, out compileFailure))
            {
                validationError = "COMPILE ERROR: " + compileFailure;
                return false;
            }

            string errors = _reportBuffer.ToString().Trim();
            if (HasCompileErrors(errors))
            {
                validationError = "COMPILE ERROR: " + errors;
                return false;
            }
            if (!string.IsNullOrEmpty(partial))
            {
                validationError = "COMPILE ERROR: incomplete input: " + partial;
                return false;
            }

            return true;
        }

        private EvalResult EvalWithEvaluate(string code)
        {
            object[] args = { code, null, false };
            _evaluate.Invoke(_evaluator, args);

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

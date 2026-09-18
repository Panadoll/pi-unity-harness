using System;
using System.Reflection;

namespace Pi.UnityHarness.Editor.Tests
{
    // The same cases run under NUnit or the standalone Mono runner. Only the Unity
    // host types are stubbed by that runner; PiUnityEvaluator and Mono.CSharp are real.
    internal sealed class PiUnityEvaluatorExecutionTests
    {
#if !PI_EVALUATOR_STANDALONE
        [NUnit.Framework.Test]
        public void RealEvaluatorRegressions()
        {
            RunCases((name, test) =>
            {
                try { test(); }
                catch (Exception ex) { throw new InvalidOperationException(name, ex); }
            });
        }
#else
        public static int Main(string[] args)
        {
            if (args.Length != 2)
                throw new ArgumentException("Expected Unity Editor Data and Mono.CSharp assembly paths.");
            UnityEditor.EditorApplication.applicationContentsPath = args[0];
            Assembly compiler = Assembly.LoadFrom(args[1]);
            Console.WriteLine("Compiler: " + compiler.FullName + " at " + compiler.Location);
            int passed = 0, failed = 0;
            RunCases((name, test) =>
            {
                try { test(); passed++; Console.WriteLine("PASS " + name); }
                catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
            });
            Console.WriteLine("Evaluator regressions: " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
#endif

        private static void RunCases(Action<string, Action> run)
        {
            run("bare expressions retain persistent variables", PersistentExpressions);
            run("default StringBuilder, Regex and UnityObject imports", DefaultImports);
            run("final line comments terminate compiler input", FinalLineComments);
            run("single final return compatibility", SimpleReturn);
            run("unbraced branches choose true and false returns", BranchReturns);
            run("nested-only return and null fallthrough", NestedReturns);
            run("foreach-only return and null fallthrough", ForeachReturns);
            run("using + type declaration + return tail", CompositeReturn);
            run("strings, comments, lambda and member returns stay intact", StringsAndMembers);
            run("comments between guard and return do not permit stripping", GuardComments);
            run("wrapper scanner preserves quoted expression tails", WrapperStringTails);
            run("string literal final expression is not discarded", StringTail);
            run("Task and IEnumerator results survive adaptation", AsyncResults);
            run("adapted side effects execute exactly once", SideEffectOnce);
            run("Validate uses the same preparation without execution", ValidateDoesNotExecute);
            run("failed adaptation exposes diagnostics and recovers", FailedCompileRecovers);
            run("invocation exceptions are always runtime errors", RuntimeExceptions);
            run("wrapper does not overwrite persistent __pi_repl", WrapperDoesNotPollute);
            run("adaptation requires CS0127, not return text", AdaptationGate);
            run("anonymous type return does not poison the session", AnonymousTypeReturnKeepsSessionUsable);
            run("poisoned compiler is rebuilt on the next call", PoisonedCompilerSelfHeals);
            run("value return keeps persistent session state", ValueReturnKeepsSessionState);
            run("top-level value return detection", TopLevelValueReturnGate);
            run("failed anonymous type compile keeps session state", FailedAnonymousTypeCompileKeepsSessionState);
            run("failed value return keeps session state", FailedValueReturnKeepsSessionState);
        }

        /// <summary>
        /// Mono.CSharp 的持久 ModuleContainer 在"顶层 value return + 匿名类型"这类失败编译后
        /// 会留下半 emit 容器，之后每次 Compile 都抛 InternalErrorException("builder already exists")，
        /// 实例内没有恢复路径。真实 Editor 里这会让整个 eval 会话死掉直到域重载，
        /// 因此 harness 必须重建编译器实例并继续服务。
        /// </summary>
        private static void AnonymousTypeReturnKeepsSessionUsable()
        {
            var evaluator = new PiUnityEvaluator();
            Value(evaluator, "1 + 1", "2");
            var result = evaluator.Eval("var count = 3;\nreturn new { count };");
            Require(result.Ok, "anonymous-type return failed: " + result.TypeName + " " + result.Error);
            Contains(result.Output, "count = 3");
            Value(evaluator, "1 + 1", "2");
        }

        private static void PoisonedCompilerSelfHeals()
        {
            var evaluator = new PiUnityEvaluator();
            evaluator.Eval("return new { a = 1 };");
            Value(evaluator, "1 + 1", "2");
            Value(evaluator, "var recovered = 5;\nrecovered", "5");
        }

        private static void PersistentExpressions()
        {
            var evaluator = new PiUnityEvaluator();
            Value(evaluator, "var persistent = 40;\npersistent + 2", "42");
            Value(evaluator, "persistent += 1;\npersistent", "41");
            Value(evaluator, "persistent", "41");
            var declaration = Success(evaluator, "var declarationOnly = 7;");
            Equal("void", declaration.TypeName);
            Equal("(ok)", declaration.Output);
            Null(evaluator, "(object)null");
        }

        private static void DefaultImports()
        {
            var evaluator = new PiUnityEvaluator();
            foreach (string code in new[]
            {
                "new StringBuilder(\"imports\").ToString()",
                "Regex.Replace(\"imports\", \"missing\", \"unused\")",
                "typeof(UnityObject).FullName",
            })
                Valid(evaluator, code);
            Value(evaluator, "new StringBuilder(\"imports\").ToString()", "imports");
            Value(evaluator, "Regex.Replace(\"imports\", \"missing\", \"unused\")", "imports");
            Value(evaluator, "typeof(UnityObject).FullName", "UnityEngine.Object");
        }

        private static void FinalLineComments()
        {
            var evaluator = new PiUnityEvaluator();
            foreach (string code in new[] { "\"tail\" // EOF", "return \"tail\"; // EOF",
                "using System;\n\"tail\" // EOF" })
            {
                Valid(evaluator, code);
                Value(evaluator, code, "tail");
            }
            Value(evaluator, "40 + 2 // EOF", "42");
        }

        private static void SimpleReturn()
        {
            var evaluator = new PiUnityEvaluator();
            Value(evaluator, "return 1 + 2;", "3");
            Value(evaluator, "var simple = 40;\nreturn simple + 2; // final", "42");
            Value(evaluator, "simple", "40");
            Value(evaluator, "return \"return; // literal\";", "return; // literal");
        }

        private static void BranchReturns()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var flag = true;");
            Value(evaluator, "if (flag) return 1; return 2;", "1");
            Success(evaluator, "flag = false;");
            Value(evaluator, "if (flag) return 1; return 2;", "2");
        }

        private static void NestedReturns()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var flag = true;");
            const string code = "if (flag) { if (flag) { return 7; } }";
            Value(evaluator, code, "7");
            Success(evaluator, "flag = false;");
            Null(evaluator, code);
            Null(evaluator, "if (flag) return 8;");
        }

        private static void ForeachReturns()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var wanted = 2;");
            const string code = "foreach (var item in new[] { 1, 2, 3 }) { if (item == wanted) return item; }";
            Value(evaluator, code, "2");
            Success(evaluator, "wanted = 9;");
            Null(evaluator, code);
        }

        private static void CompositeReturn()
        {
            var evaluator = new PiUnityEvaluator();
            Value(evaluator, "using System.Text;\n" +
                "class EvalCompositeProbe { public string Read() { return new StringBuilder(\"member\").ToString(); } }\n" +
                "if (true) { return new EvalCompositeProbe().Read(); }", "member");
            Value(evaluator, "new EvalCompositeProbe().Read()", "member");
        }

        private static void StringsAndMembers()
        {
            var evaluator = new PiUnityEvaluator();
            Value(evaluator, "// return 123;\nvar literal = @\"return 1; \"\"// quoted\"\"\";\n" +
                "/* return 456; */ literal", "return 1; \"// quoted\"");
            Value(evaluator, "literal", "return 1; \"// quoted\"");
            Value(evaluator, "System.Func<int, int> twice = x => { return x * 2; };\ntwice(21)", "42");
            Value(evaluator, "twice(5)", "10");
            Value(evaluator, "class EvalMemberProbe { public int Read() { return 17; } }\nnew EvalMemberProbe().Read()", "17");
            Value(evaluator, "if (true) { return twice(3); }", "6");
        }

        private static void GuardComments()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var flag = true;");
            Value(evaluator, "if (flag) // guard\nreturn System.Math.Abs(-3);", "3");
            Value(evaluator, "if (flag) /* guard */ return System.Math.Abs(-4);", "4");
            Success(evaluator, "flag = false;");
            Null(evaluator, "if (flag) // guard\nreturn System.Math.Abs(-3);");
        }

        private static void WrapperStringTails()
        {
            foreach (string tail in new[] { "\"tail\"", "/* tail */ \"return; // tail\" // end",
                "@\"quoted \"\"tail\"\"\"" })
            {
                string code = "if (flag) return 1;\n" + tail;
                Require(!PiUnityEvaluator.HasUnconditionalTopLevelReturn(code), "Guard misclassified:\n" + code);
                Require(PiUnityEvaluator.TryStripSingleFinalReturn(code) == null, "Tail stripped:\n" + code);
                string wrapped;
                Require(PiUnityEvaluator.TryWrapTailInFunc(code, out wrapped), "Wrapper rejected:\n" + code);
                string expected = tail.StartsWith("/*", StringComparison.Ordinal) ? "\"return; // tail\"" : tail;
                Require(wrapped.Contains("return " + expected + ";"), "Tail lost:\n" + code + "\nWrapper:\n" + wrapped);
            }
        }

        private static void StringTail()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var flag = false;");
            Value(evaluator, "if (flag) return \"early\";\n/* tail */ \"return; // tail\" // end", "return; // tail");
            Value(evaluator, "if (flag) return \"early\";\n@\"quoted \"\"tail\"\"\"", "quoted \"tail\"");
            Success(evaluator, "flag = true;");
            Value(evaluator, "if (flag) return \"early\";\n\"tail\"", "early");
            // Harness contract, not a C# unreachable-code error: never silently
            // discard a bare final result after an unconditional top-level return.
            const string unreachable = "return 1;\n\"must not disappear\"";
            var rejected = evaluator.Eval(unreachable);
            Require(!rejected.Ok && rejected.TypeName == "compile_error", "Mixed unreachable tail must fail:\n" + unreachable);
            string validation = evaluator.Validate(unreachable);
            Require(validation.StartsWith("COMPILE ERROR:", StringComparison.Ordinal), unreachable + "\n" + validation);
        }

        private static void AsyncResults()
        {
            var evaluator = new PiUnityEvaluator();
            var task = Success(evaluator, "System.Threading.Tasks.Task.FromResult(42)");
            Require(task.IsAsyncTask, "Bare Task was not recognized.");
            Equal(42, PiUnityEvaluator.EvalResult.GetTaskResult(task.AsyncTask));
            task = Success(evaluator, "if (true) { return System.Threading.Tasks.Task.FromResult(43); }");
            Require(task.IsAsyncTask, "Wrapped Task was not recognized.");
            Equal(43, PiUnityEvaluator.EvalResult.GetTaskResult(task.AsyncTask));
            Success(evaluator, "class EvalIteratorProbe { public static System.Collections.IEnumerator Run() { yield return 7; } }");
            foreach (string code in new[] { "EvalIteratorProbe.Run()", "if (true) { return EvalIteratorProbe.Run(); }" })
            {
                var coroutine = Success(evaluator, code);
                Require(coroutine.IsCoroutine && coroutine.Coroutine != null, "IEnumerator was not recognized.");
                Require(coroutine.Coroutine.MoveNext(), "Iterator did not yield.");
                Equal(7, coroutine.Coroutine.Current);
                Require(!coroutine.Coroutine.MoveNext(), "Iterator yielded twice.");
            }
        }

        private static void SideEffectOnce()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var counter = 0;");
            Value(evaluator, "counter++; if (counter == 1) { return counter; } return -1;", "1");
            Value(evaluator, "counter", "1");
            Value(evaluator, "counter++; return counter;", "2");
            Value(evaluator, "counter", "2");
        }

        private static void ValidateDoesNotExecute()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var counter = 0; var flag = true;");
            string[] cases =
            {
                "counter++; return counter;",
                "counter++; if (flag) return 1; return 2;",
                "if (flag) { counter++; return counter; }",
                "foreach (var item in new[] { 1, 2 }) { counter++; if (item == 2) return item; }",
                "if (flag) return 1;\n\"tail\"",
                "counter++; if (flag) return 1;\n/* tail */ \"return; // tail\" // end",
                "counter++; if (flag) return 1;\n@\"quoted \"\"tail\"\"\"",
                "throw new System.InvalidOperationException(\"must not execute\");",
                "using System; class EvalValidateProbe { public static int Read() { return 9; } }\n" +
                    "counter++; if (flag) { return EvalValidateProbe.Read(); }",
            };
            foreach (string code in cases)
            {
                Valid(evaluator, code);
                Value(evaluator, "counter", "0");
            }
            string error = evaluator.Validate("counter++; if (flag) { return 1; } return;");
            Contains(error, "[wrap-adapt failed]");
            Value(evaluator, "counter", "0");
            Value(evaluator, "counter++; if (flag) { return counter; }", "1");
        }

        private static void FailedCompileRecovers()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var counter = 0; var flag = true;");
            var failed = evaluator.Eval("counter++; if (flag) { return 1; } return;");
            Require(!failed.Ok, "Invalid wrapped return compiled.");
            Equal("compile_error", failed.TypeName);
            Contains(failed.Error, "CS0127");
            Contains(failed.Error, "[wrap-adapt failed]");
            Value(evaluator, "counter", "0");
            failed = evaluator.Eval("counter++; noSuchEvaluatorName;");
            Require(!failed.Ok, "Unknown name compiled.");
            Equal("compile_error", failed.TypeName);
            Value(evaluator, "counter", "0");
            Value(evaluator, "20 + 22", "42");
        }

        private static void RuntimeExceptions()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var counter = 0;");
            var failed = evaluator.Eval("counter++; throw new System.InvalidOperationException(\"runtime-marker\");");
            Require(!failed.Ok, "Throw unexpectedly succeeded.");
            Equal("runtime_error", failed.TypeName);
            Contains(failed.Error, "runtime-marker");
            Value(evaluator, "counter", "1");
            Success(evaluator, "class InternalErrorException : System.Exception { public InternalErrorException() : base(\"user-runtime\") {} }");
            failed = evaluator.Eval("counter++; if (counter > 0) { throw new InternalErrorException(); } return 1;");
            Equal("runtime_error", failed.TypeName);
            Contains(failed.Error, "user-runtime");
            Require(failed.Error.IndexOf("[HINT]", StringComparison.Ordinal) < 0, "Runtime failure got a compiler hint.");
            Value(evaluator, "counter", "2");
        }

        private static void WrapperDoesNotPollute()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var __pi_repl = 99;");
            Value(evaluator, "if (true) { return 1; }", "1");
            Value(evaluator, "if (true) { return 2; }", "2");
            Value(evaluator, "__pi_repl", "99");
        }

        private static void AdaptationGate()
        {
            Require(!PiUnityEvaluator.ShouldAdaptTopLevelReturn("error CS0103", "return missing;"), "Rewrote unrelated compiler failure.");
            Require(!PiUnityEvaluator.ShouldAdaptTopLevelReturn("InternalErrorException", "return 1;"), "Rewrote without CS0127 evidence.");
            Require(!PiUnityEvaluator.ShouldAdaptTopLevelReturn("COMPILE ERROR: incomplete input", "if (flag) return 1;\n\"tail\" // EOF"), "Rewrote partial input without CS0127 evidence.");
            Require(PiUnityEvaluator.ShouldAdaptTopLevelReturn("InternalErrorException\nerror CS0127", "if (flag) { return 1; }"), "Ignored buffered CS0127.");
        }

        /// <summary>
        /// 顶层带值 return 在 Mono 的 void 交互宿主下必然 CS0127：适配链先跑就能避开
        /// 匿名类型失败编译留下的污染，因此跨调用的持久变量必须保留（不得重建实例）。
        /// </summary>
        private static void ValueReturnKeepsSessionState()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var sessionValue = 7;");
            var result = evaluator.Eval("return new { sessionValue, doubled = sessionValue * 2 };");
            Require(result.Ok, "value-return tail failed: " + result.TypeName + " " + result.Error);
            Contains(result.Output, "doubled = 14");
            Require(result.Output.IndexOf("已重建", StringComparison.Ordinal) < 0,
                "Rebuilt the compiler for a value-return tail; persistent state was discarded: " + result.Output);
            Value(evaluator, "sessionValue", "7");
        }

        private static void TopLevelValueReturnGate()
        {
            Require(PiUnityEvaluator.HasTopLevelValueReturn("var a = 1;\nreturn a;"), "missed top-level value return.");
            Require(PiUnityEvaluator.HasTopLevelValueReturn("if (flag) return 1;"), "missed unbraced guarded return.");
            Require(PiUnityEvaluator.HasTopLevelValueReturn("if (flag) { return 1; } return 2;"), "missed trailing value return.");
            Require(!PiUnityEvaluator.HasTopLevelValueReturn("return;"), "void return treated as value return.");
            Require(!PiUnityEvaluator.HasTopLevelValueReturn("Func<int> f = () => { return 1; };"), "nested return treated as top-level.");
            Require(!PiUnityEvaluator.HasTopLevelValueReturn("\"return 1;\""), "string literal treated as return.");
            Require(!PiUnityEvaluator.HasTopLevelValueReturn("// return 1;"), "comment treated as return.");
            Require(!PiUnityEvaluator.HasTopLevelValueReturn("var returnValue = 1;\nreturnValue"), "identifier prefix treated as return.");
        }

        /// <summary>
        /// 失败编译包含匿名类型时会毒化编译器状态（真实 Editor 与独立 Mono.CSharp 都可复现），
        /// 所以先验证“丢掉脏缓存后持久变量仍在”。
        /// </summary>
        private static void FailedAnonymousTypeCompileKeepsSessionState()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var keepAfterFailure = 42;");
            var failed = evaluator.Eval("var doomed = new { a = 1 };\nnoSuchEvaluatorName;");
            Require(!failed.Ok, "broken anonymous-type input compiled.");
            Equal("compile_error", failed.TypeName);
            Contains(failed.Error, "CS0103");
            Value(evaluator, "keepAfterFailure", "42");
            Value(evaluator, "1 + 1", "2");
            Require(failed.Error.IndexOf("已重建", StringComparison.Ordinal) < 0,
                "Rebuilt the compiler instead of dropping the stale cache entry: " + failed.Error);
            Value(evaluator, "new { b = 2 }.b", "2");
        }

        /// <summary>
        /// 带值 return 且引用了不存在的名字：strip/wrap 都会失败，最后仍会原样编译一次以拿
        /// 到真实的 CS0127 诊断；这一步同样不得丢掉之前声明的持久变量。
        /// </summary>
        private static void FailedValueReturnKeepsSessionState()
        {
            var evaluator = new PiUnityEvaluator();
            Success(evaluator, "var keepBeforeReturn = 7;");
            var failed = evaluator.Eval("return new { keepBeforeReturn, broken = noSuchEvaluatorName };");
            Require(!failed.Ok, "broken value return compiled.");
            Contains(failed.Error, "CS0127");
            Contains(failed.Error, "[strip-adapt failed]");
            Value(evaluator, "keepBeforeReturn", "7");
            var ok = evaluator.Eval("return new { keepBeforeReturn, doubled = keepBeforeReturn * 2 };");
            Require(ok.Ok, "value return after a failure stopped working: " + ok.TypeName + " " + ok.Error);
            Contains(ok.Output, "doubled = 14");
        }

        private static void Valid(PiUnityEvaluator evaluator, string code)
        {
            string result = evaluator.Validate(code);
            Require(result == "(valid)", "Validate input:\n" + code + "\nExpected <(valid)>, got <" + result + ">.");
        }

        private static PiUnityEvaluator.EvalResult Success(PiUnityEvaluator evaluator, string code)
        {
            var result = evaluator.Eval(code);
            Require(result.Ok, code + "\n" + result.TypeName + ": " + result.Error);
            return result;
        }

        private static void Value(PiUnityEvaluator evaluator, string code, string expected)
        {
            string actual = Success(evaluator, code).Output;
            Require(expected == actual, "Eval input:\n" + code + "\nExpected <" + expected + ">, got <" + actual + ">.");
        }

        private static void Null(PiUnityEvaluator evaluator, string code)
        {
            var result = Success(evaluator, code);
            Equal("null", result.TypeName);
            Equal("(null)", result.Output);
        }

        private static void Equal(object expected, object actual)
        {
            Require(object.Equals(expected, actual), "Expected <" + expected + ">, got <" + actual + ">.");
        }

        private static void Contains(string text, string expected)
        {
            Require(text != null && text.IndexOf(expected, StringComparison.Ordinal) >= 0,
                "Expected <" + expected + "> in <" + text + ">.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
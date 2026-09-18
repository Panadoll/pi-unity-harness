using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class PiUnityEvaluatorLintTests
    {
        [Test]
        public void ExtractPragma_StripsFirstLineAndNormalizesMode()
        {
            string code = PiUnityEvaluator.ExtractPragma("// #repl-mode:  CLASS \r\nclass C {}", out string mode);

            Assert.AreEqual("class", mode);
            Assert.AreEqual("class C {}", code);
        }

        [Test]
        public void ExtractPragma_WhenAbsentReturnsAutoAndOriginalCode()
        {
            const string original = "var x = 1;\nx";

            string code = PiUnityEvaluator.ExtractPragma(original, out string mode);

            Assert.AreEqual("auto", mode);
            Assert.AreEqual(original, code);
        }

        [Test]
        public void ValidatePragmaMode_ReportsModeMismatches()
        {
            Assert.IsNull(PiUnityEvaluator.ValidatePragmaMode("top-level", "var x = 1;\nx"));
            StringAssert.Contains("forbids", PiUnityEvaluator.ValidatePragmaMode("top-level", "class C {}"));
            Assert.IsNull(PiUnityEvaluator.ValidatePragmaMode("class", "class C {}"));
            StringAssert.Contains("requires", PiUnityEvaluator.ValidatePragmaMode("class", "1 + 2"));
        }

        [Test]
        public void PreLint_NoLongerStripsTopLevelReturn()
        {
            // return 适配已整体移到编译路径（TryEvalTail），PreLint 不再剥 return
            string result = PiUnityEvaluator.PreLint("return 1 + 2;", out PiUnityEvaluator.ReplDiagnostic diagnostic);

            Assert.AreEqual("return 1 + 2;", result);
            Assert.IsFalse(diagnostic.AutoFixed);
            Assert.IsNull(diagnostic.Warnings);
        }

        [Test]
        public void PreLint_PreservesBranchingReturns()
        {
            const string code = "if (x) return 1;\nreturn 2;";

            string result = PiUnityEvaluator.PreLint(code, out PiUnityEvaluator.ReplDiagnostic diagnostic);

            Assert.AreEqual(code, result);
            Assert.IsFalse(diagnostic.AutoFixed);
        }

        [Test]
        public void PreLint_ReordersMixedDeclarationsBeforeTopLevelStatements()
        {
            string result = PiUnityEvaluator.PreLint("var x = 1;\nclass C { }\nx", out PiUnityEvaluator.ReplDiagnostic diagnostic);

            StringAssert.StartsWith("class C", result);
            StringAssert.Contains("var x = 1;", result);
            StringAssert.Contains("\nx\n", result);
            Assert.IsTrue(diagnostic.AutoFixed);
        }

        [Test]
        public void PreLint_CleanCodePassesThroughUnchanged()
        {
            const string code = "class C { }\nnew C()";

            string result = PiUnityEvaluator.PreLint(code, out PiUnityEvaluator.ReplDiagnostic diagnostic);

            Assert.AreEqual(code, result);
            Assert.IsFalse(diagnostic.AutoFixed);
            Assert.IsNull(diagnostic.Warnings);
        }

        // ── HasTopLevelReturn ──────────────────────────────────────────────

        [Test]
        public void HasTopLevelReturn_DetectsDepthZeroReturns()
        {
            Assert.IsTrue(PiUnityEvaluator.HasTopLevelReturn("return 1 + 2;"));
            Assert.IsTrue(PiUnityEvaluator.HasTopLevelReturn("var x = 1;\nreturn x;"));
            Assert.IsTrue(PiUnityEvaluator.HasTopLevelReturn("if (x) return 1;\nreturn 2;"));
        }

        [Test]
        public void HasTopLevelReturn_IgnoresStringsCommentsAndIdentifiers()
        {
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("var s = \"return 1; return 2;\";"));
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("// return 1;"));
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("/* return 1;\nreturn 2; */"));
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("var returnValue = 1;"));
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("Foo.ReturnValue();"));
        }

        [Test]
        public void HasTopLevelReturn_IgnoresNestedLambdasAndBlocks()
        {
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("Func<int,int> g = x => { return x * 2; };\ng(21)"));
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("class C { public int F() { return 1; } }"));
            Assert.IsFalse(PiUnityEvaluator.HasTopLevelReturn("if (x) { return 1; }"));
        }

        // ── TryStripSingleFinalReturn ─────────────────────────────────────

        [Test]
        public void StripSingleFinalReturn_SimpleFinalReturn()
        {
            string stripped = PiUnityEvaluator.TryStripSingleFinalReturn("return 1 + 2;");
            Assert.AreEqual(" 1 + 2;", stripped);
        }

        [Test]
        public void StripSingleFinalReturn_StatementThenFinalReturn()
        {
            string stripped = PiUnityEvaluator.TryStripSingleFinalReturn("var a = 1;\nreturn a + 1;");
            StringAssert.StartsWith("var a = 1;", stripped);
            StringAssert.EndsWith(" a + 1;", stripped);
        }

        [Test]
        public void StripSingleFinalReturn_RejectsBranchingReturns()
        {
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("if (x) { return 1; }\nreturn 2;"));
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("if (x) return 1;\nreturn 2;"));
        }

        [Test]
        public void StripSingleFinalReturn_RejectsUnbracedControlBodies()
        {
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("if (x) return 1;"));
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("foreach (var s in xs) return s;"));
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("if (x) { return 1; } else return 2;"));
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("label:\nreturn 1;"));
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("if (x) // guard\nreturn F();"));
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("if (x) /* guard */ return F();"));
        }

        [Test]
        public void StripSingleFinalReturn_RejectsNonFinalReturn()
        {
            Assert.IsNull(PiUnityEvaluator.TryStripSingleFinalReturn("return 1;\nFoo();"));
        }

        [Test]
        public void StripSingleFinalReturn_AllowsTrailingComments()
        {
            // 尾部注释不影响“最后一条语句”判定，剥离仍是安全的
            string stripped = PiUnityEvaluator.TryStripSingleFinalReturn("return 1; // trailing comment");
            Assert.IsNotNull(stripped);
            StringAssert.EndsWith(" 1; // trailing comment", stripped);
        }

        // ── TryWrapTailInFunc ──────────────────────────────────────────────

        [Test]
        public void WrapTailInFunc_SimpleReturnBecomesImmediateInvokeWrapper()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("return 1 + 2;", out string output);
            Assert.IsTrue(wrapped);
            Assert.IsNotNull(output);
            StringAssert.Contains("((System.Func<object>)(() => {", output);
            StringAssert.Contains("return 1 + 2;", output);
            StringAssert.EndsWith("}))()", output);
            // 不再使用持久化的 __pi_repl 临时变量（避免跨编译重名冲突）
            StringAssert.DoesNotContain("__pi_repl", output);
        }

        [Test]
        public void WrapTailInFunc_BranchingReturnsPreserved()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("if (x) { return 1; }\nreturn 2;", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("if (x) { return 1; }", output);
            StringAssert.Contains("return 2;", output);
        }

        [Test]
        public void WrapTailInFunc_NestedBlockReturnWraps()
        {
            // 嵌套块 return（无第 0 层 return）：HasTopLevelReturn 数不到，
            // 但 CS0127 证据下必须能包裹（Eval 流程以编译诊断为准）
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("if (true) { return 1; }", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("if (true) { return 1; }", output);
            StringAssert.Contains("return null;", output);
        }

        [Test]
        public void WrapTailInFunc_UnbracedIfReturnPreservedWithFallthrough()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("if (x) return 1;", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("if (x) return 1;", output);
            StringAssert.Contains("return null;", output);
        }

        [Test]
        public void WrapTailInFunc_BlockStatementGetsFallthrough()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("var list = new List<int> { 1, 2 };\nforeach (var s in list) { if (s == 2) return s; }", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("foreach (var s in list) { if (s == 2) return s; };", output);
            StringAssert.Contains("return null;", output);
        }

        [Test]
        public void WrapTailInFunc_StringLiteralTailPreserved()
        {
            // 字符串裸表达式收尾不能被 FindLastMeaningfulChar 忽略而静默丢弃
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("if (c) return 1;\n\"tail\"", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("return \"tail\";", output);
        }

        [Test]
        public void WrapTailInFunc_BareExpressionTailBecomesReturn()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("if (c) return 1;\nbase + inc", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("return base + inc;", output);
        }

        [Test]
        public void WrapTailInFunc_NestedLambdaReturnsUntouched()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("Func<int,int> g = x => { return x * 2; };\nreturn g(21);", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("Func<int,int> g = x => { return x * 2; };", output);
            StringAssert.Contains("return g(21);", output);
        }

        [Test]
        public void WrapTailInFunc_ReturnWithoutSemicolonGetsSemicolon()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("if (x) return 1;\nreturn 2", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("return 2;", output);
        }

        [Test]
        public void WrapTailInFunc_NoReturnKeywordReturnsFalse()
        {
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("var a = 1;\na + 1", out string output);
            Assert.IsFalse(wrapped);
            Assert.IsNull(output);
        }

        [Test]
        public void WrapTailInFunc_IgnoresReturnTextInStringsAndComments()
        {
            // 字符串/注释里的 "return" 不触发包裹
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("var s = \"return 1;\";\nvar t = /* return 2; */ \"ok\";\nt", out string output);
            Assert.IsFalse(wrapped);
            Assert.IsNull(output);
        }

        [Test]
        public void WrapTailInFunc_LambdaOnlyReturnIsWrappable()
        {
            // lambda 内 return 不是无效代码，但有 return 关键字时包裹是安全的
            // （CS0127 证据下的调用方才会调用；lambda return 留在 lambda 内）
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("Func<int,int> g = x => { return x; };\ng(2)", out string output);
            Assert.IsTrue(wrapped);
            Assert.IsNotNull(output);
        }

        [Test]
        public void WrapTailInFunc_MixedUnconditionalReturnPlusBareExpressionDeclined()
        {
            // 无条件顶层 return + 裸表达式收尾：包裹会产生死代码、静默丢弃表达式值，
            // 明确拒绝（调用方保留原始 CS0127 并给出提示）
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("return 1;\n40 + 2", out string output);
            Assert.IsFalse(wrapped);
            Assert.IsNull(output);
        }

        [Test]
        public void WrapTailInFunc_GuardedReturnPlusBareExpressionAllowed()
        {
            // 受控制语句保护的 return + 裸表达式收尾：语义明确，允许包裹
            bool wrapped = PiUnityEvaluator.TryWrapTailInFunc("if (c) return 1;\nbase + inc", out string output);
            Assert.IsTrue(wrapped);
            StringAssert.Contains("return base + inc;", output);
        }

        // ── ContainsAnyReturnKeyword ────────────────────────────────────────

        [Test]
        public void ContainsAnyReturnKeyword_DetectsReturnsAtAnyDepth()
        {
            Assert.IsTrue(PiUnityEvaluator.ContainsAnyReturnKeyword("if (x) { return 1; }"));
            Assert.IsTrue(PiUnityEvaluator.ContainsAnyReturnKeyword("Func<int,int> g = x => { return x; };"));
            Assert.IsFalse(PiUnityEvaluator.ContainsAnyReturnKeyword("var s = \"return 1;\";"));
            Assert.IsFalse(PiUnityEvaluator.ContainsAnyReturnKeyword("// return 1;"));
            Assert.IsFalse(PiUnityEvaluator.ContainsAnyReturnKeyword("var a = 1;\na + 1"));
        }


        // ── ShouldAdaptTopLevelReturn ──────────────────────────────────────

        [Test]
        public void ShouldAdapt_RequiresOriginalCs0127Diagnostic()
        {
            Assert.IsTrue(PiUnityEvaluator.ShouldAdaptTopLevelReturn("(1,2): error CS0127: return", "return 1;"));
            Assert.IsTrue(PiUnityEvaluator.ShouldAdaptTopLevelReturn("(1,2): error CS0127: return", "var a = 1;"));
            Assert.IsTrue(PiUnityEvaluator.ShouldAdaptTopLevelReturn("(1,2): error CS0127: return", "if (x) { return 1; }"));
            Assert.IsFalse(PiUnityEvaluator.ShouldAdaptTopLevelReturn(null, "return 1;"));
            Assert.IsFalse(PiUnityEvaluator.ShouldAdaptTopLevelReturn("(1,4): error CS1002: ; expected", "var a = 1;"));
            Assert.IsFalse(PiUnityEvaluator.ShouldAdaptTopLevelReturn("error CS0103: missing", "return missing;"));
            Assert.IsFalse(PiUnityEvaluator.ShouldAdaptTopLevelReturn("InternalErrorException: failed", "return 1;"));
        }

        // ── ClassifyError ──────────────────────────────────────────────────

        [Test]
        public void ClassifyError_MapsRepresentativeCompilerErrors()
        {
            Assert.AreEqual("top_level_return", PiUnityEvaluator.ClassifyError("error CS0127").pattern);
            Assert.AreEqual("mixed_mode", PiUnityEvaluator.ClassifyError("error CS8803").pattern);
            Assert.AreEqual("missing_semicolon", PiUnityEvaluator.ClassifyError("error CS1002").pattern);
            Assert.AreEqual("missing_reference", PiUnityEvaluator.ClassifyError("error CS0103").pattern);
            Assert.AreEqual("ambiguous_reference", PiUnityEvaluator.ClassifyError("error CS0104").pattern);
            Assert.AreEqual("instance_type_mismatch", PiUnityEvaluator.ClassifyError("error CS1929").pattern);
        }

        [Test]
        public void ClassifyError_Cs0127HintNoLongerMentionsUhEval()
        {
            string hint = PiUnityEvaluator.ClassifyError("error CS0127").hint;
            StringAssert.DoesNotContain("uh eval", hint);
            StringAssert.Contains("Func<object>", hint);
        }

        [Test]
        public void ClassifyError_Cs0104RecommendsUnityEngineObject()
        {
            string hint = PiUnityEvaluator.ClassifyError("error CS0104").hint;
            StringAssert.Contains("UnityEngine.Object", hint);
        }

        [Test]
        public void ClassifyError_Cs1929SuggestFirstDiagnostic()
        {
            string hint = PiUnityEvaluator.ClassifyError("error CS1929").hint;
            StringAssert.Contains("FIRST", hint);
        }
    }
}
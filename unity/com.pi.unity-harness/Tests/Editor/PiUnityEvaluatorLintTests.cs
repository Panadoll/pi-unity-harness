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
        public void PreLint_StripsTopLevelReturn()
        {
            string result = PiUnityEvaluator.PreLint("return 1 + 2;", out PiUnityEvaluator.ReplDiagnostic diagnostic);

            Assert.AreEqual("1 + 2;", result);
            Assert.IsTrue(diagnostic.AutoFixed);
            StringAssert.Contains("top-level 'return'", diagnostic.Warnings);
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

        [Test]
        public void ClassifyError_MapsRepresentativeCompilerErrors()
        {
            Assert.AreEqual("top_level_return", PiUnityEvaluator.ClassifyError("error CS0127").pattern);
            Assert.AreEqual("mixed_mode", PiUnityEvaluator.ClassifyError("error CS8803").pattern);
            Assert.AreEqual("missing_semicolon", PiUnityEvaluator.ClassifyError("error CS1002").pattern);
            Assert.AreEqual("missing_reference", PiUnityEvaluator.ClassifyError("error CS0103").pattern);
        }
    }
}
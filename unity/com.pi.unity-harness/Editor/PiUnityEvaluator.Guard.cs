using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.UnityHarness.Editor
{
    internal sealed partial class PiUnityEvaluator
    {
        // =========================================================
        // Execution guard: reject calls that block the main thread or
        // trigger domain reload before the code reaches the compiler.
        // Mono.CSharp has no semantic model, so this is a token-level
        // check on code with strings/comments masked out. It only lists
        // patterns that are unambiguous by name; aliases and indirect
        // calls are not detected. It is a guard, not a sandbox.
        // =========================================================

        internal const string GuardCategoryBlocking = "blocking";
        internal const string GuardCategoryReload = "reload";

        private const string AllowPragmaPrefix = "#repl-allow:";

        private sealed class GuardRule
        {
            public readonly string Category;
            public readonly string Name;
            public readonly Regex Pattern;

            public GuardRule(string category, string name, string pattern)
            {
                Category = category;
                Name = name;
                Pattern = new Regex(pattern, RegexOptions.CultureInvariant);
            }
        }

        private static readonly GuardRule[] GuardRules =
        {
            new GuardRule(GuardCategoryBlocking, "Thread.Sleep", @"\bThread\s*\.\s*Sleep\s*\("),
            new GuardRule(GuardCategoryBlocking, "Task.WaitAll/WaitAny", @"\bTask\s*\.\s*Wait(?:All|Any)\s*\("),
            new GuardRule(GuardCategoryBlocking, ".GetAwaiter().GetResult()", @"\.\s*GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(\s*\)"),
            new GuardRule(GuardCategoryBlocking, "SpinWait.SpinUntil", @"\bSpinWait\s*\.\s*SpinUntil\s*\("),
            new GuardRule(GuardCategoryReload, "AssetDatabase.Refresh", @"\bAssetDatabase\s*\.\s*Refresh\s*\("),
            new GuardRule(GuardCategoryReload, "CompilationPipeline.RequestScriptCompilation", @"\bCompilationPipeline\s*\.\s*RequestScriptCompilation\s*\("),
            new GuardRule(GuardCategoryReload, "EditorUtility.RequestScriptReload", @"\bEditorUtility\s*\.\s*RequestScriptReload\s*\("),
        };

        /// <summary>
        /// Returns null when the code passes, otherwise a "COMPILE ERROR: [guard] ..." message
        /// so validate/eval callers treat it like any other pre-compile rejection.
        /// Categories can be allowed explicitly with a line such as
        /// <c>// #repl-allow: blocking, reload</c>.
        /// </summary>
        internal static string CheckExecutionGuard(string code)
        {
            if (string.IsNullOrEmpty(code))
                return null;

            HashSet<string> allowed = ParseAllowPragmas(code);
            string masked = MaskStringsAndComments(code);
            var violations = new List<string>();
            var seenCategories = new HashSet<string>(StringComparer.Ordinal);

            foreach (GuardRule rule in GuardRules)
            {
                if (allowed.Contains(rule.Category))
                    continue;
                Match match = rule.Pattern.Match(masked);
                if (!match.Success)
                    continue;
                violations.Add(rule.Name + " (" + rule.Category + ", line " + LineOf(masked, match.Index) + ")");
                seenCategories.Add(rule.Category);
            }

            if (violations.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.Append("COMPILE ERROR: [guard] rejected before compile: ");
            sb.Append(string.Join("; ", violations));
            sb.Append('.');
            if (seenCategories.Contains(GuardCategoryBlocking))
            {
                sb.Append("\n[HINT] blocking: eval runs on the Unity main thread; a blocking wait freezes the Editor and the harness heartbeat. ");
                sb.Append("Return a Task instead, e.g. '((Func<Task<object>>)(async () => { await Task.Delay(ms); return value; }))()' (async eval is awaited), or return an IEnumerator coroutine. ");
                sb.Append("If the call really runs off the main thread, add '// #repl-allow: blocking'.");
            }
            if (seenCategories.Contains(GuardCategoryReload))
            {
                sb.Append("\n[HINT] reload: this triggers import/compile/domain reload outside the harness recompile guard. ");
                sb.Append("Use unity_recompile (pi-unity compile) instead. To override, add '// #repl-allow: reload'.");
            }
            return sb.ToString();
        }

        private static HashSet<string> ParseAllowPragmas(string code)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawLine in code.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                line = line.Substring(2).TrimStart();
                if (!line.StartsWith(AllowPragmaPrefix, StringComparison.Ordinal))
                    continue;
                foreach (string token in line.Substring(AllowPragmaPrefix.Length).Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    allowed.Add(token.Trim().ToLowerInvariant());
            }
            return allowed;
        }

        /// <summary>
        /// Replaces string/char literals and comments with spaces (newlines kept) so
        /// pattern matching only sees code and reported line numbers stay accurate.
        /// </summary>
        internal static string MaskStringsAndComments(string code)
        {
            var chars = code.ToCharArray();
            for (int i = 0; i < code.Length; i++)
            {
                int start = i;
                if (!TrySkipQuoted(code, ref i) && !TrySkipComment(code, ref i))
                    continue;
                int end = Math.Min(i, code.Length - 1);
                for (int k = start; k <= end; k++)
                {
                    if (chars[k] != '\n')
                        chars[k] = ' ';
                }
            }
            return new string(chars);
        }

        private static int LineOf(string code, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < code.Length; i++)
            {
                if (code[i] == '\n')
                    line++;
            }
            return line;
        }
    }
}

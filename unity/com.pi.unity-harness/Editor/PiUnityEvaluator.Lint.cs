using System;
using System.Collections.Generic;
using System.Text;

namespace Pi.UnityHarness.Editor
{
    internal sealed partial class PiUnityEvaluator
    {
        /// <summary>
        /// Extract optional pragma from first line. Returns the mode and
        /// the code with the pragma line stripped.
        /// Modes: "top-level", "class", "auto" (default when no pragma).
        /// </summary>
        internal static string ExtractPragma(string code, out string mode)
        {
            mode = "auto";
            if (string.IsNullOrEmpty(code))
                return code;

            const string prefix = "// #repl-mode:";
            if (!code.StartsWith(prefix, StringComparison.Ordinal))
                return code;

            int nl = code.IndexOf('\n');
            if (nl < 0)
            {
                // Entire code is just the pragma line
                mode = code.Substring(prefix.Length).Trim().ToLowerInvariant();
                return string.Empty;
            }

            mode = code.Substring(prefix.Length, nl - prefix.Length).Trim().TrimEnd('\r').ToLowerInvariant();
            return code.Substring(nl + 1);
        }

        /// <summary>
        /// Validate code against declared pragma mode.
        /// Returns null if valid, error message if violation detected.
        /// </summary>
        internal static string ValidatePragmaMode(string mode, string code)
        {
            if (mode == "auto") return null;

            if (mode == "top-level")
            {
                // top-level mode: scan entire code body — class/struct/enum
                // declarations are forbidden anywhere, not just at the start.
                int pos = 0;
                while (pos < code.Length)
                {
                    pos = SkipWhitespaceAndComments(code, pos);
                    if (pos >= code.Length) break;

                    if (StartsWithKeyword(code, pos, "using"))
                    {
                        int end = FindStatementEnd(code, pos);
                        if (end < 0) break;
                        pos = end;
                        continue;
                    }

                    int declStart;
                    if (TryFindTypeDeclarationStart(code, pos, out declStart) && declStart == pos)
                    {
                        return "COMPILE ERROR: #repl-mode: top-level forbids class/struct/enum declarations. " +
                            "Use simple top-level statements + bare expression, or switch to #repl-mode: class.";
                    }

                    // Skip this top-level statement and continue scanning
                    int stmtEnd = FindStatementEnd(code, pos);
                    if (stmtEnd < 0) break; // remainder is a bare expression, no more to scan
                    pos = stmtEnd;
                }
            }
            else if (mode == "class")
            {
                // class mode: must have at least one class declaration
                int pos = 0;
                bool hasDeclaration = false;
                while (pos < code.Length)
                {
                    pos = SkipWhitespaceAndComments(code, pos);
                    if (pos >= code.Length) break;

                    if (StartsWithKeyword(code, pos, "using"))
                    {
                        int end = FindStatementEnd(code, pos);
                        if (end < 0) break;
                        pos = end;
                        continue;
                    }

                    int declStart;
                    if (TryFindTypeDeclarationStart(code, pos, out declStart) && declStart == pos)
                    {
                        hasDeclaration = true;
                        break;
                    }
                    break;
                }
                if (!hasDeclaration)
                {
                    return "COMPILE ERROR: #repl-mode: class requires at least one class/struct declaration. " +
                        "Declare a class first, then call it as bare expression on last line.";
                }
            }

            return null;
        }

        // =========================================================
        // Pre-lint: auto-fix common agent errors before compilation
        // =========================================================

        /// <summary>
        /// 预检并自动修复常见的 .repl 编写错误。
        /// 注意：不再在此处剥离顶层 return——return 适配整体移到编译路径
        /// （TryEvalTail 的 strip/wrap 两步回退），否则会剥坏
        /// `if (x) return 1; return 2;` 这类分支/多 return 代码。
        /// Returns the (possibly fixed) code and populates diagnostic.
        /// </summary>
        internal static string PreLint(string code, out ReplDiagnostic diagnostic)
        {
            diagnostic = default(ReplDiagnostic);
            var fixes = new List<string>();
            string result = code;

            // Rule: Detect class declaration after top-level statements (mixed mode)
            string afterReorder = TryReorderMixedMode(result);
            if (afterReorder != null)
            {
                result = afterReorder;
                fixes.Add("AUTO_FIX: reordered class declaration before top-level statements (Pattern B)");
            }

            if (fixes.Count > 0)
            {
                diagnostic.AutoFixed = true;
                diagnostic.Warnings = string.Join("; ", fixes);
                LogVerbose($"[Harness] PreLint applied {fixes.Count} fix(es): {diagnostic.Warnings}");
            }

            return result;
        }

        /// <summary>
        /// Detects top-level statements followed by class declarations and reorders
        /// to put declarations first (converting to Pattern B).
        /// Returns fixed code, or null if no fix needed.
        /// </summary>
        private static string TryReorderMixedMode(string code)
        {
            // Use the existing composite splitter to analyze structure.
            // If it finds no composite structure, there's nothing to reorder.
            var usings = new List<string>();
            var topLevel = new List<string>();
            var declarations = new List<string>();

            int pos = 0;
            int length = code.Length;
            bool seenTopLevel = false;
            bool seenDeclAfterTopLevel = false;

            // Phase 1: collect usings
            while (pos < length)
            {
                pos = SkipWhitespaceAndComments(code, pos);
                if (pos >= length) break;

                if (StartsWithKeyword(code, pos, "using"))
                {
                    int end = FindStatementEnd(code, pos);
                    if (end < 0) return null;
                    usings.Add(code.Substring(pos, end - pos).Trim());
                    pos = end;
                    continue;
                }
                break;
            }

            // Phase 2: scan remaining code for interleaved top-level + declarations
            while (pos < length)
            {
                pos = SkipWhitespaceAndComments(code, pos);
                if (pos >= length) break;

                int declStart;
                if (TryFindTypeDeclarationStart(code, pos, out declStart) && declStart == pos)
                {
                    int end = FindDeclarationEnd(code, declStart);
                    if (end < 0) return null;
                    declarations.Add(code.Substring(declStart, end - declStart).Trim());
                    if (seenTopLevel) seenDeclAfterTopLevel = true;
                    pos = end;
                    continue;
                }

                // This is a top-level statement
                seenTopLevel = true;
                int stmtEnd = FindStatementEnd(code, pos);
                if (stmtEnd < 0)
                {
                    // Remaining code is one expression (possibly bare expression without semicolon)
                    topLevel.Add(code.Substring(pos).Trim());
                    pos = length;
                    break;
                }
                topLevel.Add(code.Substring(pos, stmtEnd - pos).Trim());
                pos = stmtEnd;
            }

            if (!seenDeclAfterTopLevel) return null;

            // Reorder: usings → declarations → top-level
            var sb = new StringBuilder();
            foreach (string u in usings)
            {
                sb.AppendLine(u);
            }
            if (usings.Count > 0) sb.AppendLine();

            foreach (string d in declarations)
            {
                sb.AppendLine(d);
                sb.AppendLine();
            }

            foreach (string t in topLevel)
            {
                sb.AppendLine(t);
            }

            return sb.ToString().TrimEnd() + "\n";
        }

        // ───────────────────────────────────────────────
        // Error classification shared by EnhanceCompileError and BuildErrorDiagnostic
        // ───────────────────────────────────────────────

        internal static (string pattern, string hint) ClassifyError(string error)
        {
            if (string.IsNullOrEmpty(error))
                return default;

            if (error.IndexOf("CS8803", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("Top-level statements", StringComparison.OrdinalIgnoreCase) >= 0)
                return ("mixed_mode", "Class declared after top-level statements. Use Pattern B: declare class FIRST, then call it as bare expression on last line.");
            if (error.IndexOf("CS0127", StringComparison.Ordinal) >= 0)
                return ("top_level_return", "Top-level 'return <value>' is auto-adapted: a single trailing 'return <expr>;' is stripped to the bare expression; branching/nested returns (if/foreach/switch/try blocks) get wrapped in an immediately-invoked Func<object> (fallthrough returns null). A guarded return followed by a bare final expression is supported. An unconditional top-level return followed by a bare final expression is deliberately rejected by the harness: remove the unreachable expression or use an explicit final 'return <expr>;' to make the intent clear. If CS0127 still appears, also check for a value return inside a void-returning lambda/Action.");
            if (error.IndexOf("CS0104", StringComparison.Ordinal) >= 0)
                return ("ambiguous_reference", "Ambiguous type name — qualify it: use 'UnityEngine.Object' for Unity objects, 'global::System.Object' for plain C#, or rely on the predefined 'UnityObject' alias.");
            if (error.IndexOf("CS1929", StringComparison.Ordinal) >= 0)
                return ("instance_type_mismatch", "Instance type mismatch in a method call. Fix the FIRST reported diagnostic first — the later errors usually cascade from the first failing statement.");
            if (error.IndexOf("CS1012", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("CS1009", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("CS1010", StringComparison.Ordinal) >= 0)
                return ("string_escape", "String/char escaping appears broken. Inspect the generated .repl file; shell quoting may have removed backslashes. Rewrite the .repl with literal file contents or double-escape backslashes in string and char literals.");
            if (error.IndexOf("CS1002", StringComparison.Ordinal) >= 0)
                return ("missing_semicolon", "Missing semicolon. All statements except the final bare expression need ';'.");
            if (error.IndexOf("CS0103", StringComparison.Ordinal) >= 0)
                return ("missing_reference", "Name not found. Add a 'using' directive or use the fully qualified namespace.");

            return default;
        }

        /// <summary>
        /// 判断是否应尝试顶层 return 适配（strip / Func&lt;object&gt; wrap）。
        /// 仅以原始编译诊断中的 CS0127 为证据，不能因为源码包含 return
        /// 就重写其他编译失败。嵌套块 return 不要求出现在第 0 层。
        /// </summary>
        internal static bool ShouldAdaptTopLevelReturn(string error, string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;
            return error != null && error.IndexOf("CS0127", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 是否存在第 0 层“带值 return”（return 后跟表达式，而不是裸 return;）。
        /// Mono.CSharp 的交互宿主方法返回 void，因此这种输入原样编译时必然报 CS0127：
        /// 原样编译不可能成功，却会在匿名类型这类表达式上留下被污染的持久容器，
        /// 所以适配链可以直接先跑（见 TryEvalTail）。
        /// 字符串/注释/大括号内的 return 不计入。
        /// </summary>
        internal static bool HasTopLevelValueReturn(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            int depth = 0;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '{')
                {
                    depth++;
                    continue;
                }

                if (c == '}')
                {
                    if (depth > 0)
                        depth--;
                    continue;
                }

                if (depth != 0 || !StartsWithKeyword(code, i, "return"))
                    continue;

                int next = SkipWhitespaceAndComments(code, i + "return".Length);
                if (next >= code.Length)
                    return false; // 输入在 return 后截断：交给不完整输入诊断
                if (code[next] == ';')
                    continue; // 裸 return; 在 void 宿主里合法
                if (IsYieldBeforeReturn(code, i))
                    continue;

                return true;
            }

            return false;
        }

        private static bool IsYieldBeforeReturn(string code, int returnIndex)
        {
            int previous = SkipWhitespaceAndCommentsBackwards(code, returnIndex - 1);
            if (previous < 0 || !IsIdentifierPart(code[previous]))
                return false;

            int start = previous;
            while (start > 0 && IsIdentifierPart(code[start - 1]))
                start--;
            return string.Equals(code.Substring(start, previous - start + 1), "yield", StringComparison.Ordinal);
        }

        // ───────────────────────────────────────────────
        // Error enhancement
        // ───────────────────────────────────────────────

        /// <summary>
        /// Pattern-match known compile errors and append agent-actionable hints.
        /// </summary>
        private static string EnhanceCompileError(string rawError, string code)
        {
            if (string.IsNullOrEmpty(rawError)) return rawError;
            var (_, hint) = ClassifyError(rawError);
            return string.IsNullOrEmpty(hint) ? rawError : rawError + "\n[HINT] " + hint;
        }


    }
}
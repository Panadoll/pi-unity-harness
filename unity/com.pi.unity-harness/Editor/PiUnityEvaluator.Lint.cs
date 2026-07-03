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
        private static string ExtractPragma(string code, out string mode)
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
        private static string ValidatePragmaMode(string mode, string code)
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
        /// Detects and auto-fixes common .repl authoring mistakes.
        /// Returns the (possibly fixed) code and populates diagnostic.
        /// </summary>
        private static string PreLint(string code, out ReplDiagnostic diagnostic)
        {
            diagnostic = default(ReplDiagnostic);
            var fixes = new List<string>();
            string result = code;

            // Rule 1: Strip top-level 'return' statements
            string afterReturn = TryStripTopLevelReturn(result);
            if (afterReturn != null)
            {
                result = afterReturn;
                fixes.Add("AUTO_FIX: removed top-level 'return' — use bare expression instead");
            }

            // Rule 2: Detect class declaration after top-level statements (mixed mode)
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
        /// Detects top-level 'return expr;' and converts to bare 'expr;'.
        /// Returns fixed code, or null if no fix needed.
        /// </summary>
        private static string TryStripTopLevelReturn(string code)
        {
            // Strategy: scan for 'return' keyword at brace-depth 0, skipping
            // strings, comments, and class/method bodies.
            int depth = 0;
            bool modified = false;
            var sb = new StringBuilder(code.Length);
            int i = 0;

            while (i < code.Length)
            {
                // Try to skip quoted strings — preserve them verbatim
                int before = i;
                if (TrySkipQuoted(code, ref i))
                {
                    sb.Append(code, before, i - before + 1);
                    i++;
                    continue;
                }

                // Try to skip comments — preserve them verbatim
                if (TrySkipComment(code, ref i))
                {
                    sb.Append(code, before, i - before + 1);
                    i++;
                    continue;
                }

                char c = code[i];

                if (c == '{') depth++;
                else if (c == '}' && depth > 0) depth--;

                if (depth == 0 && StartsWithKeyword(code, i, "return"))
                {
                    // Found top-level 'return'. Remove it.
                    modified = true;
                    i += 6; // skip 'return'
                    // Skip whitespace after 'return'
                    while (i < code.Length && (code[i] == ' ' || code[i] == '\t'))
                        i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return modified ? sb.ToString() : null;
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

        private static (string pattern, string hint) ClassifyError(string error)
        {
            if (string.IsNullOrEmpty(error))
                return default;

            if (error.IndexOf("CS8803", StringComparison.Ordinal) >= 0 ||
                error.IndexOf("Top-level statements", StringComparison.OrdinalIgnoreCase) >= 0)
                return ("mixed_mode", "Class declared after top-level statements. Use Pattern B: declare class FIRST, then call it as bare expression on last line.");
            if (error.IndexOf("CS0127", StringComparison.Ordinal) >= 0)
                return ("top_level_return", "uh eval does not support 'return'. Use bare expression as last line.");
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

        /// <summary>
        /// Build a diagnostic from a compile error, classifying the pattern violation.
        /// </summary>
        private static ReplDiagnostic BuildErrorDiagnostic(string enhancedError, string code)
        {
            var (pattern, hint) = ClassifyError(enhancedError);
            return new ReplDiagnostic { PatternViolation = pattern, Hint = hint };
        }
    }
}
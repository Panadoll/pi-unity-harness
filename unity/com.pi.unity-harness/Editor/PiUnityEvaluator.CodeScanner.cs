using System;
using System.Collections.Generic;
using System.Text;

namespace Pi.UnityHarness.Editor
{
    internal sealed partial class PiUnityEvaluator
    {
        private static bool TrySplitCompositeScript(string code, out CompositeScript script)
        {
            script = new CompositeScript();
            var usings = new List<string>();
            var declarations = new List<string>();

            int pos = 0;
            int length = code.Length;
            while (pos < length)
            {
                pos = SkipWhitespaceAndComments(code, pos);
                if (pos >= length)
                    break;

                if (StartsWithKeyword(code, pos, "using"))
                {
                    int end = FindStatementEnd(code, pos);
                    if (end < 0)
                        return false;

                    usings.Add(code.Substring(pos, end - pos).Trim());
                    pos = end;
                    continue;
                }

                if (TryFindTypeDeclarationStart(code, pos, out int declarationStart))
                {
                    if (declarationStart != pos)
                        break;

                    int end = FindDeclarationEnd(code, declarationStart);
                    if (end < 0)
                        return false;

                    declarations.Add(code.Substring(declarationStart, end - declarationStart).Trim());
                    pos = end;
                    continue;
                }

                break;
            }

            if (usings.Count == 0 && declarations.Count == 0)
                return false;

            script.UsingDirectives = usings;
            script.Declarations = declarations;
            script.Tail = code.Substring(Math.Min(pos, length)).Trim();
            return true;
        }

        private static bool TryFindTypeDeclarationStart(string code, int pos, out int declarationStart)
        {
            declarationStart = pos;
            int original = pos;
            while (pos < code.Length)
            {
                pos = SkipWhitespaceAndComments(code, pos);
                if (TryReadAttributeBlock(code, pos, out int afterAttribute))
                {
                    pos = afterAttribute;
                    continue;
                }

                if (StartsWithAnyKeyword(code, pos, TypeDeclarationModifiers))
                {
                    pos = ReadIdentifierEnd(code, pos);
                    continue;
                }

                declarationStart = original;
                return StartsWithAnyKeyword(code, pos, TypeDeclarationKeywords);
            }

            declarationStart = original;
            return false;
        }

        private static int FindStatementEnd(string code, int start)
        {
            int depthParen = 0;
            int depthBracket = 0;
            int depthBrace = 0;
            for (int i = start; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '(') depthParen++;
                else if (c == ')' && depthParen > 0) depthParen--;
                else if (c == '[') depthBracket++;
                else if (c == ']' && depthBracket > 0) depthBracket--;
                else if (c == '{') depthBrace++;
                else if (c == '}' && depthBrace > 0) depthBrace--;
                else if (c == ';' && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
                    return i + 1;
            }

            return -1;
        }

        private static int FindDeclarationEnd(string code, int start)
        {
            int openBrace = FindNextTopLevelChar(code, start, '{');
            if (openBrace < 0)
                return -1;

            int depth = 0;
            for (int i = openBrace; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i + 1;
                }
            }

            return -1;
        }

        private static int FindNextTopLevelChar(string code, int start, char target)
        {
            for (int i = start; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;
                if (c == target)
                    return i;
            }

            return -1;
        }

        private static int SkipWhitespaceAndComments(string code, int pos)
        {
            while (pos < code.Length)
            {
                if (char.IsWhiteSpace(code[pos]))
                {
                    pos++;
                    continue;
                }

                if (pos + 1 < code.Length && code[pos] == '/' && code[pos + 1] == '/')
                {
                    pos += 2;
                    while (pos < code.Length && code[pos] != '\n')
                        pos++;
                    continue;
                }

                if (pos + 1 < code.Length && code[pos] == '/' && code[pos + 1] == '*')
                {
                    pos += 2;
                    while (pos + 1 < code.Length && !(code[pos] == '*' && code[pos + 1] == '/'))
                        pos++;
                    pos = Math.Min(pos + 2, code.Length);
                    continue;
                }

                break;
            }

            return pos;
        }

        private static bool TryReadAttributeBlock(string code, int pos, out int end)
        {
            end = pos;
            if (pos >= code.Length || code[pos] != '[')
                return false;

            int depth = 0;
            for (int i = pos; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '[')
                    depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i + 1;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TrySkipQuoted(string code, ref int index)
        {
            char c = code[index];
            if (c == '@' && index + 1 < code.Length && code[index + 1] == '"')
            {
                index += 2;
                while (index < code.Length)
                {
                    if (code[index] == '"')
                    {
                        if (index + 1 < code.Length && code[index + 1] == '"')
                        {
                            index += 2;
                            continue;
                        }

                        return true;
                    }

                    index++;
                }

                index = code.Length - 1;
                return true;
            }

            if (c == '$' && index + 1 < code.Length && code[index + 1] == '@' &&
                index + 2 < code.Length && code[index + 2] == '"')
            {
                index++;
                return TrySkipQuoted(code, ref index);
            }

            if (c != '"' && c != '\'')
                return false;

            char quote = c;
            index++;
            while (index < code.Length)
            {
                if (code[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (code[index] == quote)
                    return true;

                index++;
            }

            index = code.Length - 1;
            return true;
        }

        private static bool TrySkipComment(string code, ref int index)
        {
            if (index + 1 >= code.Length || code[index] != '/')
                return false;

            if (code[index + 1] == '/')
            {
                index += 2;
                while (index < code.Length && code[index] != '\n')
                    index++;
                return true;
            }

            if (code[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < code.Length && !(code[index] == '*' && code[index + 1] == '/'))
                    index++;
                index = Math.Min(index + 1, code.Length - 1);
                return true;
            }

            return false;
        }

        private static bool StartsWithAnyKeyword(string code, int pos, string[] keywords)
        {
            foreach (string keyword in keywords)
            {
                if (StartsWithKeyword(code, pos, keyword))
                    return true;
            }

            return false;
        }

        private static bool StartsWithKeyword(string code, int pos, string keyword)
        {
            if (pos < 0 || pos + keyword.Length > code.Length)
                return false;

            if (!string.Equals(code.Substring(pos, keyword.Length), keyword, StringComparison.Ordinal))
                return false;

            bool beforeOk = pos == 0 || !IsIdentifierPart(code[pos - 1]);
            bool afterOk = pos + keyword.Length == code.Length || !IsIdentifierPart(code[pos + keyword.Length]);
            return beforeOk && afterOk;
        }

        private static int ReadIdentifierEnd(string code, int pos)
        {
            while (pos < code.Length && IsIdentifierPart(code[pos]))
                pos++;
            return pos;
        }

        private static bool IsIdentifierPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        // ───────────────────────────────────────────────
        // 顶层 return 适配（语句尾部专用）
        // ───────────────────────────────────────────────

        /// <summary>
        /// 判断代码中是否存在“顶层 return”关键字（第 0 层、非字符串/注释/标识符）。
        /// 只跟踪括号/花括号深度；lambda/成员/嵌套块内的 return 不计入。
        /// 注意：嵌套块内的 return（如 `if (x) { return 1; }`）不在此计数——
        /// 适配决策以编译诊断 CS0127 为证据（见 ShouldAdaptTopLevelReturn），
        /// 包裹路径不依赖本方法。
        /// </summary>
        internal static bool HasTopLevelReturn(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            int depthParen = 0;
            int depthBracket = 0;
            int depthBrace = 0;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '(') depthParen++;
                else if (c == ')' && depthParen > 0) depthParen--;
                else if (c == '[') depthBracket++;
                else if (c == ']' && depthBracket > 0) depthBracket--;
                else if (c == '{') depthBrace++;
                else if (c == '}' && depthBrace > 0) depthBrace--;
                else if (depthParen == 0 && depthBracket == 0 && depthBrace == 0
                         && StartsWithKeyword(code, i, "return"))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 代码中是否出现任何 return 关键字（任意深度，跳过字符串/注释/标识符）。
        /// 用于包裹前的最低门槛：语句尾部不含类型/成员体，任何 return 都必然可被
        /// Func&lt;object&gt; 包裹接纳（lambda 内 return 留在 lambda 内）。
        /// </summary>
        internal static bool ContainsAnyReturnKeyword(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;
                if (StartsWithKeyword(code, i, "return"))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 是否存在“无条件顶层 return”：第 0 层、且前一个有效字符不是 ) / : / else / do
        /// （即未被控制语句护住的 return，执行到必然返回）。
        /// </summary>
        internal static bool HasUnconditionalTopLevelReturn(string code)
        {
            if (string.IsNullOrEmpty(code))
                return false;

            int depthParen = 0;
            int depthBracket = 0;
            int depthBrace = 0;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '(') depthParen++;
                else if (c == ')' && depthParen > 0) depthParen--;
                else if (c == '[') depthBracket++;
                else if (c == ']' && depthBracket > 0) depthBracket--;
                else if (c == '{') depthBrace++;
                else if (c == '}' && depthBrace > 0) depthBrace--;
                else if (depthParen == 0 && depthBracket == 0 && depthBrace == 0
                         && StartsWithKeyword(code, i, "return"))
                {
                    int prev = SkipWhitespaceAndCommentsBackwards(code, i - 1);
                    if (prev >= 0)
                    {
                        char pc = code[prev];
                        if (pc == ')' || pc == ':')
                            continue;
                        if (StartsKnownControlKeywordBackwards(code, prev))
                            continue;
                    }

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 最后一个顶层分号之后是否还有“裸表达式”收尾（非 return 语句、非块语句）。
        /// </summary>
        internal static bool HasTrailingBareExpression(string code)
        {
            int lastSemi = FindLastTopLevelSemicolon(code);
            string remainder = (lastSemi < 0 ? code : code.Substring(lastSemi + 1)).Trim();
            remainder = remainder.Substring(SkipWhitespaceAndComments(remainder, 0));
            if (remainder.Length == 0)
                return false;
            if (StartsWithKeyword(remainder, 0, "return"))
                return false;

            int lastMeaningful = FindLastMeaningfulChar(remainder);
            return lastMeaningful >= 0 && remainder[lastMeaningful] != '}';
        }

        /// <summary>
        /// 尝试“安全单条最终 return”降级：仅当整个尾部恰好只有一个 return、
        /// 位于第 0 层且是最后一条语句时才把 `return &lt;expr&gt;;` 换成 `&lt;expr&gt;;`。
        /// 拒绝未加花括号控制语句体（if/while/for/foreach/else/do、标签）后的 return，
        /// 这类情况交给 Func&lt;object&gt; 包裹路径处理。
        /// 返回 null 表示不做转换。
        /// </summary>
        internal static string TryStripSingleFinalReturn(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            int depth = 0;
            int returnIndex = -1;
            int count = 0;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '{') depth++;
                else if (c == '}' && depth > 0) depth--;
                // count 统计任意深度的 return：存在非顶层 return（块/lambda 内）时
                // 即使剥掉顶层那条，剩余 return 仍会 CS0127，strip 尝试必然失败，直接跳过。
                // returnIndex 只取第 0 层的第一个。
                else if (StartsWithKeyword(code, i, "return"))
                {
                    count++;
                    if (depth == 0 && returnIndex < 0) returnIndex = i;
                }
            }

            if (count != 1 || returnIndex < 0)
                return null;

            // return 语句必须带分号且是最后一条语句（其后只有空白/注释）
            int stmtEnd = FindStatementEnd(code, returnIndex);
            if (stmtEnd < 0 || FindLastMeaningfulChar(code.Substring(stmtEnd)) >= 0)
                return null;

            // 前一个有效字符不能是未加花括号控制语句的收尾（)| else | do | 标签冒号）
            int prev = SkipWhitespaceAndCommentsBackwards(code, returnIndex - 1);
            if (prev >= 0)
            {
                char pc = code[prev];
                if (pc == ')' || pc == ':')
                    return null;
                if (StartsKnownControlKeywordBackwards(code, prev))
                    return null;
            }

            // 去掉 'return' 关键字本身，表达式连同分号保留（如 `return 1 + 2;` → `1 + 2;`）
            return code.Substring(0, returnIndex) + code.Substring(returnIndex + "return".Length);
        }

        /// <summary>
        /// 将语句尾部包进立即调用的 System.Func&lt;object&gt;，使顶层 `return &lt;value&gt;;` 合法：
        /// 尾部是裸表达式时转换为 `return &lt;expr&gt;;`，否则追加 `return null;` 兜底。
        /// 仅作用于语句尾部；类型/成员/lambda 内部的 return 原样保留。
        /// 是否包裹由调用方依据编译证据（CS0127）决定，本方法不再自行
        /// 扫描“顶层 return”——嵌套块 return（if/foreach/switch/try）同样需要包裹。
        /// 拒绝“无条件顶层 return + 裸表达式收尾”的混合写法（包裹会产生死代码、
        /// 静默丢弃表达式值）：返回 false 且 wrapped 为 null，由调用方保留原始
        /// CS0127 错误并给出提示。
        /// </summary>
        internal static bool TryWrapTailInFunc(string code, out string wrapped)
        {
            wrapped = null;
            if (string.IsNullOrWhiteSpace(code))
                return false;

            string body = code.Trim();
            if (!ContainsAnyReturnKeyword(body))
                return false;

            // 混合写法：无条件顶层 return 之后还有裸表达式收尾 => 结果不确定，拒绝包裹
            if (HasUnconditionalTopLevelReturn(body) && HasTrailingBareExpression(body))
                return false;

            int lastSemi = FindLastTopLevelSemicolon(body);
            string remainder = lastSemi < 0 ? body : body.Substring(lastSemi + 1);
            remainder = remainder.Trim();
            remainder = remainder.Substring(SkipWhitespaceAndComments(remainder, 0));

            var sb = new StringBuilder();
            sb.Append("((System.Func<object>)(() => {\n");
            if (lastSemi >= 0)
            {
                sb.Append(body.Substring(0, lastSemi + 1));
                sb.Append("\n");
            }

            if (StartsWithKeyword(remainder, 0, "return"))
            {
                // 尾部是缺少分号的 return 语句（如 `return 1 + 2`）：直接补分号
                sb.Append(remainder.Substring(0, FindLastMeaningfulChar(remainder) + 1));
                sb.Append(";\n");
            }
            else
            {
                int lastMeaningful = FindLastMeaningfulChar(remainder);
                if (lastMeaningful < 0)
                {
                    // 尾部以分号语句结束：追加 return null 兜底（含 if/foreach 等块后无返回的情况）
                    sb.Append("return null;\n");
                }
                else if (remainder[lastMeaningful] == '}')
                {
                    // 块语句（if/else、foreach、switch、try）：原样保留并补分号 + return null 兜底
                    sb.Append(remainder.Substring(0, lastMeaningful + 1));
                    sb.Append(";\nreturn null;\n");
                }
                else if (lastSemi < 0)
                {
                    // 无前置顶层分号且收尾不是块/return 语句（如 `if (x == 41) return 1 + 2`
                    // 缺分号）：形状不明确，任何包裹都可能丢弃或改写用户代码——拒绝包裹，
                    // 保留原始编译诊断（CS0127 等），绝不静默吞掉代码。
                    return false;
                }
                else
                {
                    // 裸表达式收尾：转成 return <expr>; 保留其值
                    sb.Append("return ");
                    sb.Append(remainder.Substring(0, lastMeaningful + 1));
                    sb.Append(";\n");
                }
            }

            sb.Append("}))()");
            wrapped = sb.ToString();
            return true;
        }

        /// <summary>
        /// 最后一个“顶层分号”（括号/方括号/花括号深度均为 0，跳过字符串与注释）的位置，找不到返回 -1。
        /// </summary>
        private static int FindLastTopLevelSemicolon(string code)
        {
            int depthParen = 0;
            int depthBracket = 0;
            int depthBrace = 0;
            int last = -1;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (TrySkipQuoted(code, ref i))
                    continue;
                if (TrySkipComment(code, ref i))
                    continue;

                if (c == '(') depthParen++;
                else if (c == ')' && depthParen > 0) depthParen--;
                else if (c == '[') depthBracket++;
                else if (c == ']' && depthBracket > 0) depthBracket--;
                else if (c == '{') depthBrace++;
                else if (c == '}' && depthBrace > 0) depthBrace--;
                else if (c == ';' && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
                    last = i;
            }
            return last;
        }

        /// <summary>
        /// 最后一个不属于注释且非空白的字符下标，找不到返回 -1。
        /// 字符串/字符字面量整体视为有效内容（以其结束引号位置为准），
        /// 避免把 `"tail"` 这类字符串裸表达式误判为“无内容”而静默丢弃。
        /// </summary>
        private static int FindLastMeaningfulChar(string text)
        {
            int last = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\''
                    || (c == '@' && i + 1 < text.Length && text[i + 1] == '"'))
                {
                    int mark = i;
                    if (TrySkipQuoted(text, ref i))
                    {
                        last = i;
                        continue;
                    }

                    i = mark;
                }

                if (TrySkipComment(text, ref i))
                    continue;
                if (!char.IsWhiteSpace(text[i]))
                    last = i;
            }

            return last;
        }

        /// <summary>
        /// 从 pos 往回跳过空白与注释，返回前一个有效字符下标，无则 -1。
        /// </summary>
        private static int SkipWhitespaceAndCommentsBackwards(string code, int pos)
        {
            // Comments can only be identified reliably by scanning forwards: a line
            // comment ends at the newline, not at its opening // marker.
            return pos < 0 ? -1 : FindLastMeaningfulChar(code.Substring(0, pos + 1));
        }

        /// <summary>
        /// 判断指定位置（前一个有效字符）是否直接位于 else / do 关键字之后。
        /// </summary>
        private static bool StartsKnownControlKeywordBackwards(string code, int index)
        {
            foreach (string keyword in new[] { "else", "do" })
            {
                int start = index - keyword.Length + 1;
                if (start < 0)
                    continue;
                if (string.Equals(code.Substring(start, keyword.Length), keyword, StringComparison.Ordinal)
                    && (start == 0 || !IsIdentifierPart(code[start - 1])))
                    return true;
            }
            return false;
        }

        private static readonly string[] TypeDeclarationKeywords =
        {
            "class", "struct", "interface", "enum", "delegate", "namespace"
        };

        private static readonly string[] TypeDeclarationModifiers =
        {
            "public", "internal", "private", "protected", "static", "abstract",
            "sealed", "partial", "unsafe", "new", "readonly", "ref"
        };

        private struct CompositeScript
        {
            public List<string> UsingDirectives;
            public List<string> Declarations;
            public string Tail;
        }
    }
}
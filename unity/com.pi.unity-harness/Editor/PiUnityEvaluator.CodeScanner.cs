using System;
using System.Collections.Generic;

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
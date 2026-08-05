using System;
using System.Collections;
using UnityEditor;

namespace Pi.UnityHarness.Editor.Capabilities.Input
{
    public static partial class HarnessInput
    {
        public static IEnumerator RunSequenceJson(string sequenceJson)
        {
            string normalizedSequenceJson = NormalizeSequenceJson(sequenceJson);
            SequenceAction[] actions;
            string parseError = null;
            try
            {
                var wrapper = UnityEngine.JsonUtility.FromJson<SequenceWrapper>(
                    normalizedSequenceJson);
                actions = wrapper?.actions;
            }
            catch (Exception ex)
            {
                actions = null;
                parseError = ex.Message;
            }

            if (parseError != null)
            {
                yield return Fail("Invalid sequence JSON: " + parseError, "usage");
                yield break;
            }

            if (actions == null || actions.Length == 0)
            {
                yield return Fail("Sequence must contain at least one action.", "usage");
                yield break;
            }

            if (!ValidateSequenceActions(normalizedSequenceJson, actions, out string validationError))
            {
                yield return Fail(validationError, "usage");
                yield break;
            }

            if (!EnsureReady(out string errorJson))
            {
                yield return errorJson;
                yield break;
            }

            int executed = 0;
            try
            {
                for (int i = 0; i < actions.Length; i++)
                {
                    var a = actions[i];
                    string actionError = null;

                    switch (a.type)
                    {
                        case "wait":
                            int waitMs = a.ms > 0 ? a.ms : 100;
                            double deadline = EditorApplication.timeSinceStartup + waitMs / 1000.0;
                            while (EditorApplication.timeSinceStartup < deadline)
                                yield return null;
                            break;

                        case "click":
                            var clickEnum = ClickJson(a.x, a.y, string.IsNullOrEmpty(a.button) ? "left" : a.button);
                            while (clickEnum.MoveNext())
                            {
                                if (clickEnum.Current is string s && s.Contains("\"failed\""))
                                { actionError = s; break; }
                                yield return clickEnum.Current;
                            }
                            break;

                        case "double_click":
                            var dblEnum = DoubleClickJson(a.x, a.y, string.IsNullOrEmpty(a.button) ? "left" : a.button);
                            while (dblEnum.MoveNext())
                            {
                                if (dblEnum.Current is string s2 && s2.Contains("\"failed\""))
                                { actionError = s2; break; }
                                yield return dblEnum.Current;
                            }
                            break;

                        case "drag":
                            var dragEnum = DragJson(a.from_x, a.from_y, a.to_x, a.to_y,
                                string.IsNullOrEmpty(a.button) ? "left" : a.button, a.steps);
                            while (dragEnum.MoveNext())
                            {
                                if (dragEnum.Current is string s3 && s3.Contains("\"failed\""))
                                { actionError = s3; break; }
                                yield return dragEnum.Current;
                            }
                            break;

                        case "scroll":
                            var scrollEnum = ScrollJson(a.x, a.y, a.delta_x, a.delta_y);
                            while (scrollEnum.MoveNext())
                            {
                                if (scrollEnum.Current is string sScroll && sScroll.Contains("\"failed\""))
                                { actionError = sScroll; break; }
                                yield return scrollEnum.Current;
                            }
                            break;

                        case "drag_start":
                            var dragStartEnum = DragStartJson(a.x, a.y,
                                string.IsNullOrEmpty(a.button) ? "left" : a.button);
                            while (dragStartEnum.MoveNext())
                            {
                                if (dragStartEnum.Current is string sDragStart && sDragStart.Contains("\"failed\""))
                                { actionError = sDragStart; break; }
                                yield return dragStartEnum.Current;
                            }
                            break;

                        case "drag_move":
                            var dragMoveEnum = DragMoveJson(a.x, a.y, a.steps);
                            while (dragMoveEnum.MoveNext())
                            {
                                if (dragMoveEnum.Current is string sDragMove && sDragMove.Contains("\"failed\""))
                                { actionError = sDragMove; break; }
                                yield return dragMoveEnum.Current;
                            }
                            break;

                        case "drag_end":
                            var dragEndEnum = DragEndJson(a.x, a.y, a.steps);
                            while (dragEndEnum.MoveNext())
                            {
                                if (dragEndEnum.Current is string sDragEnd && sDragEnd.Contains("\"failed\""))
                                { actionError = sDragEnd; break; }
                                yield return dragEndEnum.Current;
                            }
                            break;

                        case "type_text":
                            if (string.IsNullOrEmpty(a.text))
                            { actionError = Fail("type_text action requires 'text' field.", "usage"); break; }
                            var typeEnum = TypeTextJson(a.text);
                            while (typeEnum.MoveNext())
                            {
                                if (typeEnum.Current is string s4 && s4.Contains("\"failed\""))
                                { actionError = s4; break; }
                                yield return typeEnum.Current;
                            }
                            break;

                        case "key_chord":
                            if (a.keys == null || a.keys.Length == 0)
                            { actionError = Fail("key_chord action requires 'keys' field.", "usage"); break; }
                            var chordEnum = KeyChordJson(InputJson.StringArray(a.keys));
                            while (chordEnum.MoveNext())
                            {
                                if (chordEnum.Current is string s5 && s5.Contains("\"failed\""))
                                { actionError = s5; break; }
                                yield return chordEnum.Current;
                            }
                            break;

                        default:
                            actionError = Fail("Unknown action type: " + a.type, "usage");
                            break;
                    }

                    if (actionError != null)
                    {
                        yield return InputJson.Object()
                            .String("status", "failed")
                            .String("action", "run_sequence")
                            .Number("executed_count", executed)
                            .Number("failed_at", i)
                            .String("failed_action_type", a.type)
                            .String("failed_action_result", actionError)
                            .String("error", "Action " + i + " (" + a.type + ") failed")
                            .String("error_type", "runtime")
                            .ToString();
                        yield break;
                    }

                    executed++;
                }

                yield return InputJson.Object()
                    .String("status", "succeeded")
                    .String("action", "run_sequence")
                    .Number("executed_count", executed)
                    .ToString();
            }
            finally
            {
                ResetSplitDragState();
                try { InvokeBackendVoid("ClearAllInput"); }
                catch { /* best-effort cleanup */ }
            }
        }

        // =========================================================
        // Sequence JSON helpers
        // =========================================================

        [Serializable]
        private class SequenceWrapper
        {
            public SequenceAction[] actions;
        }

        [Serializable]
        private struct SequenceAction
        {
            public string type;
            // click / double_click / scroll
            public float x, y;
            public string button;
            // drag
            public float from_x, from_y, to_x, to_y;
            public int steps;
            // scroll
            public float delta_x, delta_y;
            // wait
            public int ms;
            // type_text
            public string text;
            // key_chord
            public string[] keys;
        }

        [Serializable]
        private class StringArrayWrapper
        {
            public string[] items;
        }

        /// <summary>Normalize sequence JSON so callers can pass either {actions:[...]} or bare [...].</summary>
        private static string NormalizeSequenceJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return InputJson.Object().Raw("actions", "[]").ToString();

            string trimmed = json.Trim();
            if (trimmed.StartsWith("["))
                return InputJson.Object().Raw("actions", trimmed).ToString();

            return trimmed;
        }

        private static bool ValidateSequenceActions(string normalizedJson,
            SequenceAction[] actions, out string error)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                string prefix = "Action " + i + " (" + (actions[i].type ?? "<missing>") + ")";
                switch (actions[i].type)
                {
                    case "wait":
                        break;
                    case "click":
                    case "double_click":
                        if (!SequenceActionHasField(normalizedJson, i, "x") ||
                            !SequenceActionHasField(normalizedJson, i, "y"))
                            return FailSequenceValidation(prefix + " requires 'x' and 'y' fields.", out error);
                        if (!TryValidateFinite("x", actions[i].x, out string clickNumberError) ||
                            !TryValidateFinite("y", actions[i].y, out clickNumberError))
                            return FailSequenceValidation(prefix + " has invalid " + clickNumberError, out error);
                        break;
                    case "drag":
                        if (!SequenceActionHasField(normalizedJson, i, "from_x") ||
                            !SequenceActionHasField(normalizedJson, i, "from_y") ||
                            !SequenceActionHasField(normalizedJson, i, "to_x") ||
                            !SequenceActionHasField(normalizedJson, i, "to_y"))
                            return FailSequenceValidation(prefix +
                                " requires 'from_x', 'from_y', 'to_x', and 'to_y' fields.", out error);
                        if (!TryValidateFinite("from_x", actions[i].from_x, out string dragNumberError) ||
                            !TryValidateFinite("from_y", actions[i].from_y, out dragNumberError) ||
                            !TryValidateFinite("to_x", actions[i].to_x, out dragNumberError) ||
                            !TryValidateFinite("to_y", actions[i].to_y, out dragNumberError))
                            return FailSequenceValidation(prefix + " has invalid " + dragNumberError, out error);
                        break;
                    case "scroll":
                        if (!SequenceActionHasField(normalizedJson, i, "x") ||
                            !SequenceActionHasField(normalizedJson, i, "y") ||
                            !SequenceActionHasField(normalizedJson, i, "delta_x") ||
                            !SequenceActionHasField(normalizedJson, i, "delta_y"))
                            return FailSequenceValidation(prefix +
                                " requires 'x', 'y', 'delta_x', and 'delta_y' fields.", out error);
                        if (!TryValidateFinite("x", actions[i].x, out string scrollNumberError) ||
                            !TryValidateFinite("y", actions[i].y, out scrollNumberError) ||
                            !TryValidateFinite("delta_x", actions[i].delta_x, out scrollNumberError) ||
                            !TryValidateFinite("delta_y", actions[i].delta_y, out scrollNumberError))
                            return FailSequenceValidation(prefix + " has invalid " + scrollNumberError, out error);
                        break;
                    case "drag_start":
                    case "drag_move":
                    case "drag_end":
                        if (!SequenceActionHasField(normalizedJson, i, "x") ||
                            !SequenceActionHasField(normalizedJson, i, "y"))
                            return FailSequenceValidation(prefix + " requires 'x' and 'y' fields.", out error);
                        if (!TryValidateFinite("x", actions[i].x, out string splitDragNumberError) ||
                            !TryValidateFinite("y", actions[i].y, out splitDragNumberError))
                            return FailSequenceValidation(prefix + " has invalid " + splitDragNumberError, out error);
                        break;
                }
            }

            error = null;
            return true;
        }

        private static bool FailSequenceValidation(string message, out string error)
        {
            error = message;
            return false;
        }

        private static bool SequenceActionHasField(string normalizedJson, int actionIndex, string fieldName)
        {
            string actionsKey = "\"actions\"";
            int keyIndex = normalizedJson.IndexOf(actionsKey, StringComparison.Ordinal);
            if (keyIndex < 0) return false;

            int arrayStart = normalizedJson.IndexOf('[', keyIndex + actionsKey.Length);
            if (arrayStart < 0) return false;

            if (!TryFindJsonArrayElement(normalizedJson, arrayStart, actionIndex,
                out int objectStart, out int objectEnd))
                return false;

            return JsonObjectHasTopLevelField(normalizedJson, objectStart, objectEnd, fieldName);
        }

        private static bool TryFindJsonArrayElement(string json, int arrayStart, int targetIndex,
            out int objectStart, out int objectEnd)
        {
            objectStart = -1;
            objectEnd = -1;
            bool inString = false;
            bool escape = false;
            int depth = 0;
            int currentIndex = -1;

            for (int i = arrayStart; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    if (i > arrayStart && depth == 1)
                    {
                        currentIndex++;
                        if (currentIndex == targetIndex)
                            objectStart = i;
                    }
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 1 && currentIndex == targetIndex)
                    {
                        objectEnd = i;
                        return true;
                    }
                }
                else if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return false;
                }
            }

            return false;
        }

        private static bool JsonObjectHasTopLevelField(string json, int objectStart, int objectEnd, string fieldName)
        {
            bool inString = false;
            bool escape = false;
            int depth = 0;
            string expected = "\"" + fieldName + "\"";

            for (int i = objectStart; i <= objectEnd; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"')
                    {
                        inString = false;
                        if (depth == 1 && StringAt(json, i - expected.Length + 1, expected) &&
                            NextNonWhitespaceIs(json, i + 1, ':'))
                            return true;
                    }
                    continue;
                }

                if (c == '"')
                    inString = true;
                else if (c == '{' || c == '[')
                    depth++;
                else if (c == '}' || c == ']')
                    depth--;
            }

            return false;
        }

        private static bool StringAt(string value, int index, string expected)
        {
            if (index < 0 || index + expected.Length > value.Length) return false;
            return string.CompareOrdinal(value, index, expected, 0, expected.Length) == 0;
        }

        private static bool NextNonWhitespaceIs(string value, int index, char expected)
        {
            for (int i = index; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i])) continue;
                return value[i] == expected;
            }
            return false;
        }

        /// <summary>Parse a simple JSON string array like ["A","B"]. Returns null on failure.</summary>
        private static string[] ParseSimpleStringArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                string trimmed = json.Trim();
                if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return null;
                var wrapper = UnityEngine.JsonUtility.FromJson<StringArrayWrapper>(
                    "{\"items\":" + trimmed + "}");
                return wrapper?.items;
            }
            catch
            {
                return null;
            }
        }
    }
}

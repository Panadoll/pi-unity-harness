using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Pi.UnityHarness.Editor.Capabilities.Shared;

namespace Pi.UnityHarness.Editor.Capabilities.Vision
{
    /// <summary>
    /// Unified JSON builder for vision capture and analysis responses.
    ///
    /// Replaces raw string concatenation in HarnessVision with structured JSON generation.
    /// All methods return valid JSON strings using proper escaping via <see cref="PiAbilityJson.Escape"/>.
    ///
    /// Schemas:
    ///   - capture status:       harness.vision.capture.v1
    ///   - analysis status:      harness.vision.analysis.v1
    ///   - compound response:    harness.vision.capture_analysis.v1
    ///   - analysis request:     harness.vision.analysis_request.v1
    /// </summary>
    internal static class VisionJson
    {
        private const string SchemaCaptureAnalysisV1 = "harness.vision.capture_analysis.v1";
        private const string SchemaAnalysisV1 = "harness.vision.analysis.v1";
        private const string SchemaAnalysisRequestV1 = "harness.vision.analysis_request.v1";
        private const string SchemaProviderTestV1 = "harness.vision.provider_test.v1";
        private const string SchemaCaptureV1 = "harness.vision.capture.v1";

        private static readonly string[] CaptureImageFieldOrder =
        {
            "path",
            "source",
            "width",
            "height",
            "bytes",
            "output_size",
            "gameview_size",
            "capture_source_size",
            "scale",
            "screenshot_to_gameview",
            "captured_at_utc"
        };

        // --- Capture JSON -------------------------------------------------

        /// <summary>
        /// Build capture JSON string from individual fields.
        /// Keep status as the first field for compatibility with simple Agent checks.
        /// </summary>
        public static string BuildCaptureJson(
            string status,
            string path,
            string source,
            int width,
            int height,
            long bytes,
            int? outputWidth,
            int? outputHeight,
            float? gameViewWidth,
            float? gameViewHeight,
            int? sourceWidth,
            int? sourceHeight,
            float? scaleX,
            float? scaleY,
            float? screenshotToGameViewX,
            float? screenshotToGameViewY,
            string capturedAtUtc,
            string error = null,
            string errorType = null)
        {
            var sb = new StringBuilder();
            sb.Append("{");

            AppendStringField(sb, "status", status, true);
            AppendStringField(sb, "schema", SchemaCaptureV1);
            if (status == "succeeded")
            {
                AppendStringField(sb, "path", path);
                AppendStringField(sb, "source", source);
                AppendIntField(sb, "width", width);
                AppendIntField(sb, "height", height);
                AppendLongField(sb, "bytes", bytes);

                if (outputWidth.HasValue && outputHeight.HasValue)
                    AppendObjectField(sb, "output_size", $"\"w\":{outputWidth.Value},\"h\":{outputHeight.Value}");

                if (source == "game")
                {
                    if (gameViewWidth.HasValue && gameViewHeight.HasValue)
                        AppendObjectField(sb, "gameview_size", $"\"w\":{FloatStr(gameViewWidth.Value)},\"h\":{FloatStr(gameViewHeight.Value)}");
                    if (sourceWidth.HasValue && sourceHeight.HasValue)
                        AppendObjectField(sb, "capture_source_size", $"\"w\":{sourceWidth.Value},\"h\":{sourceHeight.Value}");
                    if (scaleX.HasValue && scaleY.HasValue)
                        AppendObjectField(sb, "scale", $"\"x\":{FloatStr(scaleX.Value)},\"y\":{FloatStr(scaleY.Value)}");
                    if (screenshotToGameViewX.HasValue && screenshotToGameViewY.HasValue)
                        AppendObjectField(sb, "screenshot_to_gameview", $"\"x\":{FloatStr(screenshotToGameViewX.Value)},\"y\":{FloatStr(screenshotToGameViewY.Value)}");
                }

                if (!string.IsNullOrEmpty(capturedAtUtc))
                    AppendStringField(sb, "captured_at_utc", capturedAtUtc);
            }
            else
            {
                AppendStringField(sb, "error", error ?? "Unknown error");
                AppendStringField(sb, "error_type", errorType ?? "runtime");
            }

            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Build a succeeded capture JSON for an existing image while preserving
        /// metadata from CaptureJson(), especially GameView coordinate mapping fields.
        /// </summary>
        public static string BuildCaptureJsonFromMetadata(
            string path,
            string imageMetaJson,
            int detectedWidth,
            int detectedHeight,
            long fileBytes,
            string capturedAtUtc)
        {
            var fields = GetCaptureFieldMap(imageMetaJson);

            string source = GetString(fields, "source") ?? "unknown";
            int width = detectedWidth > 0 ? detectedWidth : (GetInt(fields, "width") ?? 0);
            int height = detectedHeight > 0 ? detectedHeight : (GetInt(fields, "height") ?? 0);
            long bytes = fileBytes > 0 ? fileBytes : (GetLong(fields, "bytes") ?? 0);
            string capturedAt = GetString(fields, "captured_at_utc") ?? capturedAtUtc;

            if (width < 0) width = 0;
            if (height < 0) height = 0;
            if (bytes < 0) bytes = 0;

            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "status", "succeeded", true);
            AppendStringField(sb, "schema", SchemaCaptureV1);
            AppendStringField(sb, "path", path);
            AppendStringField(sb, "source", source);
            AppendIntField(sb, "width", width);
            AppendIntField(sb, "height", height);
            AppendLongField(sb, "bytes", bytes);

            if (TryGetObject(fields, "output_size", out var outputSize))
                AppendRawField(sb, "output_size", outputSize);
            else
                AppendObjectField(sb, "output_size", $"\"w\":{width},\"h\":{height}");

            if (TryGetObject(fields, "gameview_size", out var gameViewSize))
                AppendRawField(sb, "gameview_size", gameViewSize);
            if (TryGetObject(fields, "capture_source_size", out var captureSourceSize))
                AppendRawField(sb, "capture_source_size", captureSourceSize);
            if (TryGetObject(fields, "scale", out var scale))
                AppendRawField(sb, "scale", scale);
            if (TryGetObject(fields, "screenshot_to_gameview", out var screenshotToGameView))
                AppendRawField(sb, "screenshot_to_gameview", screenshotToGameView);

            if (!string.IsNullOrEmpty(capturedAt))
                AppendStringField(sb, "captured_at_utc", capturedAt);

            sb.Append("}");
            return sb.ToString();
        }

        // --- Analysis JSON (unavailable / skipped / failed) -------------

        /// <summary>
        /// Build analysis JSON for the "unavailable" provider case.
        /// </summary>
        public static string BuildAnalysisUnavailableJson(string question)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "schema", SchemaAnalysisV1, true);
            AppendStringField(sb, "status", "unavailable");
            AppendStringField(sb, "question", question);
            AppendStringField(sb, "answer", "Screenshot was captured but no vision analyzer is configured.");
            AppendArrayField(sb, "visible_text", "[]");
            AppendArrayField(sb, "targets", "[]");
            AppendArrayField(sb, "suggested_actions", "[]");
            AppendObjectField(sb, "error",
                "\"type\":\"provider_unavailable\"," +
                "\"message\":\"No external analyzer command or provider is configured.\"");
            AppendObjectField(sb, "diagnostics", "\"provider\":\"none\"");
            AppendArrayField(sb, "warnings",
                "[\"Screenshot was captured but not analyzed.\"]");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Build analysis JSON for when capture failed (analysis skipped).
        /// </summary>
        public static string BuildAnalysisSkippedJson()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "schema", SchemaAnalysisV1, true);
            AppendStringField(sb, "status", "skipped");
            AppendStringField(sb, "answer", "Analysis was skipped because screenshot capture failed.");
            AppendArrayField(sb, "visible_text", "[]");
            AppendArrayField(sb, "targets", "[]");
            AppendArrayField(sb, "suggested_actions", "[]");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Build analysis JSON for failed analysis.
        /// </summary>
        public static string BuildAnalysisFailedJson(string question, string error, string errorType)
            => BuildAnalysisFailedJson(question, error, errorType, "none", null, null, -1);

        public static string BuildAnalysisFailedJson(
            string question,
            string error,
            string errorType,
            string provider,
            string model,
            string endpoint,
            long latencyMs)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "schema", SchemaAnalysisV1, true);
            AppendStringField(sb, "status", "failed");
            AppendStringField(sb, "question", question);
            AppendStringField(sb, "answer", "Analysis failed.");
            AppendArrayField(sb, "visible_text", "[]");
            AppendArrayField(sb, "targets", "[]");
            AppendArrayField(sb, "suggested_actions", "[]");
            AppendObjectField(sb, "error",
                "\"type\":\"" + EscapeJson(errorType) + "\"," +
                "\"message\":\"" + EscapeJson(error) + "\"");
            AppendObjectField(sb, "diagnostics", BuildDiagnosticsInner(provider, model, endpoint, latencyMs));
            sb.Append("}");
            return sb.ToString();
        }

        public static string BuildAnalysisSucceededJson(
            string question,
            string answer,
            string provider,
            string model,
            string endpoint,
            long latencyMs)
            => BuildAnalysisSucceededJson(question, answer, "[]", "[]", "[]", provider, model, endpoint, latencyMs);

        public static string BuildAnalysisSucceededJson(
            string question,
            string answer,
            string visibleTextJson,
            string targetsJson,
            string suggestedActionsJson,
            string provider,
            string model,
            string endpoint,
            long latencyMs)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "schema", SchemaAnalysisV1, true);
            AppendStringField(sb, "status", "succeeded");
            AppendStringField(sb, "question", question);
            AppendStringField(sb, "answer", answer);
            AppendArrayField(sb, "visible_text", NormalizeJsonArray(visibleTextJson));
            AppendArrayField(sb, "targets", NormalizeJsonArray(targetsJson));
            AppendArrayField(sb, "suggested_actions", NormalizeJsonArray(suggestedActionsJson));
            AppendObjectField(sb, "diagnostics", BuildDiagnosticsInner(provider, model, endpoint, latencyMs));
            AppendArrayField(sb, "warnings", "[]");
            sb.Append("}");
            return sb.ToString();
        }

        public static string BuildProviderTestJson(
            string status,
            string provider,
            string model,
            string endpoint,
            bool hasApiKey,
            string apiKeySource,
            long latencyMs,
            string error = null,
            string errorType = null,
            string warning = null)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "schema", SchemaProviderTestV1, true);
            AppendStringField(sb, "status", status);
            AppendObjectField(sb, "diagnostics",
                BuildDiagnosticsInner(provider, model, endpoint, latencyMs) +
                ",\"has_api_key\":" + (hasApiKey ? "true" : "false") +
                ",\"api_key_source\":\"" + EscapeJson(apiKeySource ?? "none") + "\"");
            if (!string.IsNullOrWhiteSpace(error))
            {
                AppendObjectField(sb, "error",
                    "\"type\":\"" + EscapeJson(errorType ?? "runtime") + "\"," +
                    "\"message\":\"" + EscapeJson(error) + "\"");
            }
            AppendArrayField(sb, "warnings",
                string.IsNullOrWhiteSpace(warning) ? "[]" : "[\"" + EscapeJson(warning) + "\"]");
            sb.Append("}");
            return sb.ToString();
        }

        // --- Compound Response (capture + analysis) ---------------------

        /// <summary>
        /// Build compound capture+analysis response JSON.
        /// </summary>
        public static string BuildCompoundCaptureAnalysisJson(
            string captureJson,
            string analysisJson,
            string compoundStatus)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "schema", SchemaCaptureAnalysisV1, true);
            AppendStringField(sb, "status", compoundStatus);
            sb.Append(",\"capture\":").Append(string.IsNullOrWhiteSpace(captureJson) ? "{}" : captureJson);
            sb.Append(",\"analysis\":").Append(string.IsNullOrWhiteSpace(analysisJson) ? "{}" : analysisJson);
            sb.Append("}");
            return sb.ToString();
        }

        // --- Analysis Request JSON --------------------------------------

        /// <summary>
        /// Build analysis request JSON for external analyzer.
        /// </summary>
        public static string BuildAnalysisRequestJson(
            string question,
            string captureJson,
            string contextJson,
            string requestId = null)
        {
            var imageFields = GetCaptureFieldMap(captureJson);
            string normalizedContext = NormalizeJsonObject(contextJson);

            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "schema", SchemaAnalysisRequestV1, true);
            if (!string.IsNullOrEmpty(requestId))
                AppendStringField(sb, "request_id", requestId);
            AppendStringField(sb, "question", question);

            sb.Append(",\"image\":{");
            bool first = true;
            foreach (var fieldName in CaptureImageFieldOrder)
            {
                if (!imageFields.TryGetValue(fieldName, out var value))
                    continue;
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"").Append(EscapeJson(fieldName)).Append("\":").Append(value);
            }
            sb.Append("}");

            sb.Append(",\"context\":").Append(normalizedContext);

            sb.Append(",\"output\":{");
            sb.Append("\"language\":\"zh-CN\",");
            sb.Append("\"coordinate_space\":\"screenshot_top_left\",");
            sb.Append("\"include_visible_text\":true,");
            sb.Append("\"include_targets\":true,");
            sb.Append("\"include_suggested_actions\":true,");
            sb.Append("\"max_targets\":10");
            sb.Append("}");

            sb.Append(",\"constraints\":{");
            sb.Append("\"do_not_guess\":true,");
            sb.Append("\"min_confidence_for_action\":0.7");
            sb.Append("}");

            sb.Append("}");
            return sb.ToString();
        }

        // --- Internal Helpers ------------------------------------------

        private static void AppendStringField(StringBuilder sb, string name, string value, bool isFirst = false)
        {
            if (!isFirst) sb.Append(",");
            sb.Append("\"").Append(EscapeJson(name)).Append("\":\"");
            sb.Append(EscapeJson(value)).Append("\"");
        }

        private static void AppendIntField(StringBuilder sb, string name, int value, bool isFirst = false)
        {
            if (!isFirst) sb.Append(",");
            sb.Append("\"").Append(EscapeJson(name)).Append("\":").Append(value);
        }

        private static void AppendLongField(StringBuilder sb, string name, long value, bool isFirst = false)
        {
            if (!isFirst) sb.Append(",");
            sb.Append("\"").Append(EscapeJson(name)).Append("\":").Append(value);
        }

        private static void AppendObjectField(StringBuilder sb, string name, string innerContent, bool isFirst = false)
        {
            if (!isFirst) sb.Append(",");
            sb.Append("\"").Append(EscapeJson(name)).Append("\":{").Append(innerContent).Append("}");
        }

        private static void AppendArrayField(StringBuilder sb, string name, string innerContent, bool isFirst = false)
        {
            if (!isFirst) sb.Append(",");
            sb.Append("\"").Append(EscapeJson(name)).Append("\":").Append(innerContent);
        }

        private static void AppendRawField(StringBuilder sb, string name, string value, bool isFirst = false)
        {
            if (!isFirst) sb.Append(",");
            sb.Append("\"").Append(EscapeJson(name)).Append("\":").Append(value);
        }

        private static string BuildDiagnosticsInner(string provider, string model, string endpoint, long latencyMs)
        {
            var sb = new StringBuilder();
            sb.Append("\"provider\":\"").Append(EscapeJson(provider ?? "none")).Append("\"");
            if (!string.IsNullOrEmpty(model))
                sb.Append(",\"model\":\"").Append(EscapeJson(model)).Append("\"");
            if (!string.IsNullOrEmpty(endpoint))
                sb.Append(",\"endpoint\":\"").Append(EscapeJson(endpoint)).Append("\"");
            if (latencyMs >= 0)
                sb.Append(",\"latency_ms\":").Append(latencyMs);
            return sb.ToString();
        }

        public static bool TryGetCaptureString(string json, string name, out string value)
        {
            value = GetString(GetCaptureFieldMap(json), name);
            return value != null;
        }

        internal static bool TryParseObjectFields(string json, out Dictionary<string, string> fields)
        {
            fields = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(json))
                return false;

            int index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{')
                return false;
            index++;

            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    return false;

                if (json[index] == '}')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                    return index == json.Length;
                }

                if (!TryReadJsonString(json, ref index, out var key))
                    return false;

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':')
                    return false;
                index++;

                SkipWhitespace(json, ref index);
                int valueStart = index;
                if (!TrySkipJsonValue(json, ref index))
                    return false;

                string valueText = json.Substring(valueStart, index - valueStart).Trim();
                fields[key] = valueText;

                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    return false;
                if (json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (json[index] == '}')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                    return index == json.Length;
                }
                return false;
            }
        }

        internal static bool TryGetArrayElement(string json, int elementIndex, out string elementJson)
        {
            elementJson = null;
            if (string.IsNullOrWhiteSpace(json) || elementIndex < 0)
                return false;

            int index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '[')
                return false;
            index++;

            int current = 0;
            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    return false;
                if (json[index] == ']')
                    return false;

                int valueStart = index;
                if (!TrySkipJsonValue(json, ref index))
                    return false;

                if (current == elementIndex)
                {
                    elementJson = json.Substring(valueStart, index - valueStart).Trim();
                    return true;
                }

                current++;
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                    return false;
                if (json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (json[index] == ']')
                    return false;
                return false;
            }
        }

        internal static bool TryReadJsonStringValue(string json, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            int index = 0;
            SkipWhitespace(json, ref index);
            if (!TryReadJsonString(json, ref index, out value))
                return false;
            SkipWhitespace(json, ref index);
            return index == json.Length;
        }

        private static Dictionary<string, string> GetCaptureFieldMap(string json)
        {
            var fields = ParseObjectFieldsOrEmpty(json);
            if (fields.TryGetValue("capture", out var nestedCapture) && IsJsonObject(nestedCapture))
                fields = ParseObjectFieldsOrEmpty(nestedCapture);
            return fields;
        }

        private static string NormalizeJsonObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return "{}";

            string trimmed = json.Trim();
            return IsJsonObject(trimmed) ? trimmed : "{}";
        }

        private static string NormalizeJsonArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return "[]";

            string trimmed = json.Trim();
            return IsJsonArray(trimmed) ? trimmed : "[]";
        }

        private static bool IsJsonObject(string json)
        {
            return TryParseObjectFields(json, out _);
        }

        internal static bool IsJsonArray(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            int index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '[')
                return false;
            if (!TrySkipComposite(json, ref index))
                return false;
            SkipWhitespace(json, ref index);
            return index == json.Length;
        }

        private static Dictionary<string, string> ParseObjectFieldsOrEmpty(string json)
        {
            return TryParseObjectFields(json, out var fields) ? fields : new Dictionary<string, string>();
        }


        private static bool TryGetObject(Dictionary<string, string> fields, string name, out string value)
        {
            value = null;
            if (!fields.TryGetValue(name, out var raw) || !IsJsonObject(raw))
                return false;
            value = raw;
            return true;
        }

        private static string GetString(Dictionary<string, string> fields, string name)
        {
            if (!fields.TryGetValue(name, out var raw))
                return null;

            raw = raw.Trim();
            int index = 0;
            return TryReadJsonString(raw, ref index, out var value) ? value : null;
        }

        private static int? GetInt(Dictionary<string, string> fields, string name)
        {
            if (!fields.TryGetValue(name, out var raw))
                return null;
            return int.TryParse(raw.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (int?)null;
        }

        private static long? GetLong(Dictionary<string, string> fields, string name)
        {
            if (!fields.TryGetValue(name, out var raw))
                return null;
            return long.TryParse(raw.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (long?)null;
        }

        private static bool TrySkipJsonValue(string json, ref int index)
        {
            if (index >= json.Length)
                return false;

            char c = json[index];
            if (c == '"')
                return TrySkipJsonString(json, ref index);
            if (c == '{' || c == '[')
                return TrySkipComposite(json, ref index);

            if (json.IndexOf("true", index, StringComparison.Ordinal) == index)
            {
                index += 4;
                return true;
            }
            if (json.IndexOf("false", index, StringComparison.Ordinal) == index)
            {
                index += 5;
                return true;
            }
            if (json.IndexOf("null", index, StringComparison.Ordinal) == index)
            {
                index += 4;
                return true;
            }
            return TrySkipJsonNumber(json, ref index);
        }

        private static bool TrySkipJsonNumber(string json, ref int index)
        {
            int start = index;
            if (index < json.Length && json[index] == '-')
                index++;

            if (index >= json.Length)
                return false;

            if (json[index] == '0')
            {
                index++;
            }
            else if (char.IsDigit(json[index]) && json[index] != '0')
            {
                while (index < json.Length && char.IsDigit(json[index]))
                    index++;
            }
            else
            {
                return false;
            }

            if (index < json.Length && json[index] == '.')
            {
                index++;
                int fractionStart = index;
                while (index < json.Length && char.IsDigit(json[index]))
                    index++;
                if (index == fractionStart)
                    return false;
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-'))
                    index++;
                int exponentStart = index;
                while (index < json.Length && char.IsDigit(json[index]))
                    index++;
                if (index == exponentStart)
                    return false;
            }

            return index > start;
        }

        private static bool TrySkipComposite(string json, ref int index)
        {
            int depth = 0;
            while (index < json.Length)
            {
                char c = json[index];
                if (c == '"')
                {
                    if (!TrySkipJsonString(json, ref index))
                        return false;
                    continue;
                }

                if (c == '{' || c == '[')
                {
                    depth++;
                    index++;
                    continue;
                }

                if (c == '}' || c == ']')
                {
                    depth--;
                    index++;
                    if (depth == 0)
                        return true;
                    if (depth < 0)
                        return false;
                    continue;
                }

                index++;
            }

            return false;
        }

        private static bool TryReadJsonString(string json, ref int index, out string value)
        {
            value = null;
            if (index >= json.Length || json[index] != '"')
                return false;

            index++;
            var sb = new StringBuilder();
            while (index < json.Length)
            {
                char c = json[index++];
                if (c == '"')
                {
                    value = sb.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (index >= json.Length)
                    return false;

                char escaped = json[index++];
                switch (escaped)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length)
                            return false;
                        string hex = json.Substring(index, 4);
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            return false;
                        sb.Append((char)code);
                        index += 4;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }

        private static bool TrySkipJsonString(string json, ref int index)
        {
            if (index >= json.Length || json[index] != '"')
                return false;

            index++;
            while (index < json.Length)
            {
                char c = json[index++];
                if (c == '"')
                    return true;
                if (c == '\\')
                {
                    if (index >= json.Length)
                        return false;
                    index++;
                }
            }

            return false;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }

        private static string EscapeJson(string value)
        {
            return PiAbilityJson.Escape(value);
        }

        private static string FloatStr(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}

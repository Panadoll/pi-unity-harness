using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Capabilities.Vision
{
    internal static class OpenAiCompatibleVisionAnalyzer
    {
        private static readonly HttpClient s_httpClient = new HttpClient();

        public static IEnumerator AnalyzeJson(string question, string captureJson, string contextJson)
        {
            var settings = VisionSettings.Instance;
            if (!settings.TryValidateOpenAiCompatible(out string validationError))
            {
                yield return VisionJson.BuildAnalysisFailedJson(question, validationError, "configuration");
                yield break;
            }

            if (!VisionJson.TryGetCaptureString(captureJson, "path", out string imagePath) ||
                string.IsNullOrWhiteSpace(imagePath) ||
                !File.Exists(imagePath))
            {
                yield return VisionJson.BuildAnalysisFailedJson(
                    question,
                    "Captured image file is missing.",
                    "runtime",
                    "openai-compatible",
                    settings.OpenAiModelOrDefault,
                    settings.BuildOpenAiEndpoint(),
                    -1);
                yield break;
            }

            byte[] imageBytes = null;
            string imageReadError = null;
            try
            {
                imageBytes = File.ReadAllBytes(imagePath);
            }
            catch (Exception ex)
            {
                imageReadError = ex.Message;
            }

            if (imageReadError != null)
            {
                yield return VisionJson.BuildAnalysisFailedJson(
                    question,
                    "Failed to read captured image: " + imageReadError,
                    "runtime",
                    "openai-compatible",
                    settings.OpenAiModelOrDefault,
                    settings.BuildOpenAiEndpoint(),
                    -1);
                yield break;
            }

            string requestBody = BuildRequestBody(
                settings.OpenAiModelOrDefault,
                question,
                captureJson,
                contextJson,
                imageBytes,
                settings.OpenAiMaxCompletionTokensClamped);

            var task = SendVisionRequestAsync(
                question,
                requestBody,
                settings.BuildOpenAiEndpoint(),
                settings.OpenAiModelOrDefault,
                settings.OpenAiTimeoutMsClamped,
                settings.OpenAiApiKeyHeaderOrDefault,
                settings.OpenAiAuthSchemeNormalized,
                settings.OpenAiApiKey);

            while (!task.IsCompleted)
                yield return null;

            yield return ReadTaskResult(
                task,
                question,
                settings.OpenAiModelOrDefault,
                settings.BuildOpenAiEndpoint());
        }

        public static IEnumerator TestProviderJson()
        {
            var task = TestProviderAsync();
            while (!task.IsCompleted)
                yield return null;
            yield return ReadProviderTestTaskResult(task);
        }

        internal static Task<string> TestProviderAsync()
        {
            var settings = VisionSettings.Instance;
            if (!settings.TryValidateOpenAiCompatible(out string validationError))
            {
                return Task.FromResult(VisionJson.BuildProviderTestJson(
                    "failed",
                    settings.ProviderNormalized,
                    settings.OpenAiModelOrDefault,
                    settings.BuildOpenAiEndpoint(),
                    settings.HasOpenAiApiKey,
                    settings.OpenAiApiKeySource,
                    -1,
                    validationError,
                    "configuration"));
            }

            string requestBody = BuildTextOnlyRequestBody(settings.OpenAiModelOrDefault);
            return SendProviderTestAsync(
                requestBody,
                settings.BuildOpenAiEndpoint(),
                settings.OpenAiModelOrDefault,
                settings.OpenAiTimeoutMsClamped,
                settings.OpenAiApiKeyHeaderOrDefault,
                settings.OpenAiAuthSchemeNormalized,
                settings.OpenAiApiKey,
                settings.HasOpenAiApiKey,
                settings.OpenAiApiKeySource);
        }

        internal static string BuildRequestBody(
            string model,
            string question,
            string captureJson,
            string contextJson,
            byte[] imageBytes,
            int maxCompletionTokens)
        {
            string dataUrl = "data:image/png;base64," + Convert.ToBase64String(imageBytes ?? Array.Empty<byte>());
            string prompt = BuildUserPrompt(question, captureJson, contextJson);

            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "model", model, true);
            sb.Append(",\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(SystemPrompt));
            sb.Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":[");
            sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(dataUrl));
            sb.Append("\"}},");
            sb.Append("{\"type\":\"text\",\"text\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(prompt));
            sb.Append("\"}");
            sb.Append("]}");
            sb.Append("],");
            sb.Append("\"max_completion_tokens\":").Append(Math.Max(1, maxCompletionTokens));
            sb.Append("}");
            return sb.ToString();
        }

        internal static string ExtractAssistantText(string responseJson)
        {
            return TryExtractAssistantContent(responseJson, out string content, out _) ? content : null;
        }

        internal static bool TryExtractAssistantContent(string responseJson, out string content, out string error)
        {
            content = null;
            error = null;

            if (string.IsNullOrWhiteSpace(responseJson))
            {
                error = "Provider returned an empty response body.";
                return false;
            }

            if (!VisionJson.TryParseObjectFields(responseJson, out var root))
            {
                error = "Provider response body is not a JSON object.";
                return false;
            }

            if (!root.TryGetValue("choices", out var choicesRaw) ||
                !VisionJson.TryGetArrayElement(choicesRaw, 0, out var firstChoice) ||
                !VisionJson.TryParseObjectFields(firstChoice, out var choiceFields) ||
                !choiceFields.TryGetValue("message", out var messageRaw) ||
                !VisionJson.TryParseObjectFields(messageRaw, out var messageFields) ||
                !messageFields.TryGetValue("content", out var contentRaw))
            {
                error = "Provider response did not contain choices[0].message.content.";
                return false;
            }

            if (VisionJson.TryReadJsonStringValue(contentRaw, out var stringContent))
            {
                content = stringContent;
                return true;
            }

            // Some OpenAI-compatible APIs may return content as an array. Preserve it as text.
            content = contentRaw;
            return true;
        }

        internal static string BuildAnalysisJsonFromProviderResponse(
            string question,
            string responseText,
            string provider,
            string model,
            string endpoint,
            long latencyMs)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return VisionJson.BuildAnalysisFailedJson(
                    question,
                    "OpenAI-compatible provider returned an empty response body.",
                    "provider_response_empty",
                    provider,
                    model,
                    endpoint,
                    latencyMs);
            }

            if (!TryExtractAssistantContent(responseText, out string content, out string parseError))
            {
                return VisionJson.BuildAnalysisFailedJson(
                    question,
                    parseError + " Raw response: " + Truncate(responseText, 500),
                    "provider_response_parse",
                    provider,
                    model,
                    endpoint,
                    latencyMs);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return VisionJson.BuildAnalysisFailedJson(
                    question,
                    "OpenAI-compatible provider returned an empty assistant message.",
                    "provider_response_empty",
                    provider,
                    model,
                    endpoint,
                    latencyMs);
            }

            return BuildSucceededAnalysisFromAssistantContent(question, content, provider, model, endpoint, latencyMs);
        }

        private static async Task<string> SendVisionRequestAsync(
            string question,
            string requestBody,
            string endpoint,
            string model,
            int timeoutMs,
            string apiKeyHeader,
            string authScheme,
            string apiKey)
        {
            return await SendOpenAiCompatibleRequestAsync(
                requestBody,
                endpoint,
                timeoutMs,
                apiKeyHeader,
                authScheme,
                apiKey,
                (responseText, latencyMs) => BuildAnalysisJsonFromProviderResponse(
                    question, responseText, "openai-compatible", model, endpoint, latencyMs),
                (statusCode, responseText, latencyMs) => VisionJson.BuildAnalysisFailedJson(
                    question,
                    BuildHttpErrorMessage("OpenAI-compatible vision request failed", statusCode, responseText),
                    "provider_http",
                    "openai-compatible",
                    model,
                    endpoint,
                    latencyMs),
                latencyMs => VisionJson.BuildAnalysisFailedJson(
                    question,
                    "OpenAI-compatible vision request timed out after " + timeoutMs + "ms.",
                    "timeout",
                    "openai-compatible",
                    model,
                    endpoint,
                    latencyMs),
                (ex, latencyMs) => VisionJson.BuildAnalysisFailedJson(
                    question,
                    "OpenAI-compatible vision request failed: " + ex.Message,
                    "provider_http",
                    "openai-compatible",
                    model,
                    endpoint,
                    latencyMs)).ConfigureAwait(false);
        }

        private static async Task<string> SendProviderTestAsync(
            string requestBody,
            string endpoint,
            string model,
            int timeoutMs,
            string apiKeyHeader,
            string authScheme,
            string apiKey,
            bool hasApiKey,
            string apiKeySource)
        {
            return await SendOpenAiCompatibleRequestAsync(
                requestBody,
                endpoint,
                timeoutMs,
                apiKeyHeader,
                authScheme,
                apiKey,
                (responseText, latencyMs) => BuildProviderTestSuccessJson(
                    responseText, model, endpoint, hasApiKey, apiKeySource, latencyMs),
                (statusCode, responseText, latencyMs) => VisionJson.BuildProviderTestJson(
                    "failed", "openai-compatible", model, endpoint, hasApiKey, apiKeySource,
                    latencyMs,
                    BuildHttpErrorMessage("Provider test failed", statusCode, responseText),
                    "provider_http"),
                latencyMs => VisionJson.BuildProviderTestJson(
                    "failed", "openai-compatible", model, endpoint, hasApiKey, apiKeySource,
                    latencyMs,
                    "Provider test timed out after " + timeoutMs + "ms.",
                    "timeout"),
                (ex, latencyMs) => VisionJson.BuildProviderTestJson(
                    "failed", "openai-compatible", model, endpoint, hasApiKey, apiKeySource,
                    latencyMs,
                    "Provider test failed: " + ex.Message,
                    "provider_http")).ConfigureAwait(false);
        }

        private static async Task<string> SendOpenAiCompatibleRequestAsync(
            string requestBody,
            string endpoint,
            int timeoutMs,
            string apiKeyHeader,
            string authScheme,
            string apiKey,
            Func<string, long, string> onSuccess,
            Func<int, string, long, string> onHttpError,
            Func<long, string> onTimeout,
            Func<Exception, long, string> onException)
        {
            var stopwatch = Stopwatch.StartNew();
            using (var cts = new CancellationTokenSource())
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                cts.CancelAfter(timeoutMs);
                request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                ApplyAuthHeader(request, apiKeyHeader, authScheme, apiKey);

                try
                {
                    using (var response = await s_httpClient.SendAsync(request,
                        HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        string responseText = response.Content != null
                            ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                            : "";
                        stopwatch.Stop();

                        if (!response.IsSuccessStatusCode)
                            return onHttpError((int)response.StatusCode, responseText, stopwatch.ElapsedMilliseconds);

                        return onSuccess(responseText, stopwatch.ElapsedMilliseconds);
                    }
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    return onTimeout(stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    return onException(ex, stopwatch.ElapsedMilliseconds);
                }
            }
        }

        private static string BuildProviderTestSuccessJson(string responseText, string model, string endpoint,
            bool hasApiKey, string apiKeySource, long latencyMs)
        {
            string warning = null;
            if (!TryExtractAssistantContent(responseText, out string content, out string parseError) ||
                string.IsNullOrWhiteSpace(content))
            {
                warning = string.IsNullOrWhiteSpace(parseError)
                    ? "Provider returned HTTP success but empty assistant content for the text-only health check. Endpoint/auth are reachable; run CaptureAndAnalyzeAsyncJson to verify vision output."
                    : parseError;
            }

            return VisionJson.BuildProviderTestJson(
                "succeeded", "openai-compatible", model, endpoint, hasApiKey, apiKeySource,
                latencyMs, null, null, warning);
        }

        private static string BuildHttpErrorMessage(string prefix, int statusCode, string responseText)
        {
            string message = prefix + " with HTTP " + statusCode;
            if (!string.IsNullOrWhiteSpace(responseText))
                message += " | " + Truncate(responseText, 1000);
            return message;
        }

        private static string BuildSucceededAnalysisFromAssistantContent(
            string question,
            string content,
            string provider,
            string model,
            string endpoint,
            long latencyMs)
        {
            string cleaned = StripCodeFence(content).Trim();
            string answer = cleaned;
            string visibleTextJson = "[]";
            string targetsJson = "[]";
            string suggestedActionsJson = "[]";

            if (VisionJson.TryParseObjectFields(cleaned, out var fields))
            {
                if (fields.TryGetValue("answer", out var answerRaw) &&
                    VisionJson.TryReadJsonStringValue(answerRaw, out var parsedAnswer) &&
                    !string.IsNullOrWhiteSpace(parsedAnswer))
                    answer = parsedAnswer;

                if (fields.TryGetValue("visible_text", out var visibleTextRaw) && VisionJson.IsJsonArray(visibleTextRaw))
                    visibleTextJson = visibleTextRaw;
                if (fields.TryGetValue("targets", out var targetsRaw) && VisionJson.IsJsonArray(targetsRaw))
                    targetsJson = targetsRaw;
                if (fields.TryGetValue("suggested_actions", out var suggestedActionsRaw) && VisionJson.IsJsonArray(suggestedActionsRaw))
                    suggestedActionsJson = suggestedActionsRaw;
            }

            return VisionJson.BuildAnalysisSucceededJson(
                question,
                answer,
                visibleTextJson,
                targetsJson,
                suggestedActionsJson,
                provider,
                model,
                endpoint,
                latencyMs);
        }

        private static string BuildTextOnlyRequestBody(string model)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            AppendStringField(sb, "model", model, true);
            sb.Append(",\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"Reply with a tiny JSON object only.\"},");
            sb.Append("{\"role\":\"user\",\"content\":\"Return exactly: {\\\"ok\\\":true}\"}");
            sb.Append("],\"max_completion_tokens\":32}");
            return sb.ToString();
        }

        private static string BuildUserPrompt(string question, string captureJson, string contextJson)
        {
            string requestJson = VisionJson.BuildAnalysisRequestJson(
                string.IsNullOrWhiteSpace(question) ? "Describe what is visible in the screenshot." : question,
                captureJson,
                contextJson);

            var sb = new StringBuilder();
            sb.Append("Analyze the Unity screenshot for an automation agent.\n");
            sb.Append("Return ONLY one valid JSON object, no markdown, no code fences.\n");
            sb.Append("The JSON object must use this shape:\n");
            sb.Append("{\"answer\":\"zh-CN concise answer\",\"visible_text\":[],\"targets\":[],\"suggested_actions\":[]}\n");
            sb.Append("targets items should include label, confidence, coordinate_space, center {x,y}, and bbox {x,y,w,h} when visible.\n");
            sb.Append("suggested_actions items should include type, target_label, confidence, and reason when useful.\n");
            sb.Append("Use coordinate_space=\"screenshot_top_left\" for all screenshot coordinates. Do not guess uncertain targets.\n\n");
            sb.Append("Analysis request JSON:\n").Append(requestJson);
            return sb.ToString();
        }

        private static string ReadTaskResult(Task<string> task, string question, string model, string endpoint)
        {
            if (task.IsCanceled)
            {
                return VisionJson.BuildAnalysisFailedJson(
                    question,
                    "OpenAI-compatible vision request was canceled.",
                    "timeout",
                    "openai-compatible",
                    model,
                    endpoint,
                    -1);
            }

            if (task.IsFaulted)
            {
                string message = task.Exception?.InnerException?.Message ?? task.Exception?.Message ?? "Unknown error";
                return VisionJson.BuildAnalysisFailedJson(
                    question,
                    "OpenAI-compatible vision request failed: " + message,
                    "provider_http",
                    "openai-compatible",
                    model,
                    endpoint,
                    -1);
            }

            return task.Result;
        }

        private static string ReadProviderTestTaskResult(Task<string> task)
        {
            if (task.IsCanceled)
                return VisionJson.BuildProviderTestJson("failed", "openai-compatible", null, null, false, "none", -1, "Provider test was canceled.", "timeout");
            if (task.IsFaulted)
            {
                string message = task.Exception?.InnerException?.Message ?? task.Exception?.Message ?? "Unknown error";
                return VisionJson.BuildProviderTestJson("failed", "openai-compatible", null, null, false, "none", -1, message, "provider_http");
            }
            return task.Result;
        }

        private static void ApplyAuthHeader(HttpRequestMessage request, string apiKeyHeader, string authScheme, string apiKey)
        {
            string scheme = string.IsNullOrWhiteSpace(authScheme) ? "bearer" : authScheme.Trim().ToLowerInvariant();
            if (scheme == "none") return;
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            string value = scheme == "bearer" ? "Bearer " + apiKey : apiKey;
            request.Headers.TryAddWithoutValidation(
                string.IsNullOrWhiteSpace(apiKeyHeader) ? "Authorization" : apiKeyHeader.Trim(),
                value);
        }

        private static string StripCodeFence(string value)
        {
            string trimmed = (value ?? "").Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return trimmed;

            int firstNewline = trimmed.IndexOf('\n');
            int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline < 0 || lastFence <= firstNewline)
                return trimmed;

            return trimmed.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
        }

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value;
            return value.Substring(0, maxChars) + "...";
        }

        private static void AppendStringField(StringBuilder sb, string name, string value, bool isFirst = false)
        {
            if (!isFirst) sb.Append(",");
            sb.Append("\"").Append(PiUnityJsonHelper.EscapeJson(name)).Append("\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(value)).Append("\"");
        }

        private const string SystemPrompt =
            "You are a Unity Editor vision analyzer for an automation agent. " +
            "Always answer in zh-CN. Return only valid JSON with answer, visible_text, targets, and suggested_actions. " +
            "Coordinates must use screenshot_top_left pixels when provided. Do not guess uncertain UI targets.";
    }
}

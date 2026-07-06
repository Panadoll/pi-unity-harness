using System;
using System.Collections;
using System.IO;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using Pi.UnityHarness.Runtime.Capabilities.Vision;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Vision
{
    /// <summary>
    /// Public Vision API facade. Provides screenshot capture via public static methods
    /// that can be called from uh eval -f recipes.
    ///
    /// Usage in uh eval -f:
    ///   Harness.Vision.Editor.HarnessVision.CaptureJson()
    ///   Harness.Vision.Editor.HarnessVision.CaptureGameViewAsyncJson()
    ///   Harness.Vision.Editor.HarnessVision.CaptureJson("scene", "Temp/my.png", 1280, 720)
    ///   Harness.Vision.Editor.HarnessVision.CaptureJsonAsync("game", "Temp/my.png", 0, 0)
    ///   Harness.Vision.Editor.HarnessVision.CaptureAndAnalyzeJson("Describe the SceneView.", "scene", null, 1280, 720, null)
    ///   Harness.Vision.Editor.HarnessVision.CaptureAndAnalyzeAsyncJson("Find the Start button.", "game", null, 0, 0, null)
    ///   Harness.Vision.Editor.HarnessVision.BuildAnalysisRequestJson("What is visible?", captureJson, null)
    ///
    /// IMPORTANT: uh eval does NOT support top-level return-expression syntax.
    /// Use bare expressions in .repl files.
    /// </summary>
    public static class HarnessVision
    {
        private const int DefaultSceneCaptureWidth = 1280;
        private const int DefaultSceneCaptureHeight = 720;
        private const int MinCaptureWidth = 64;
        private const int MinCaptureHeight = 64;
        private const int MaxCaptureWidth = 7680;
        private const int MaxCaptureHeight = 4320;

        // ─── Capture API ────────────────────────────────────

        /// <summary>
        /// Capture using synchronous auto mode. Synchronous capture never reads the
        /// GameView framebuffer; use CaptureJsonAsync/CaptureGameViewAsyncJson for GameView.
        /// SceneView output defaults to 1280x720 when width/height are omitted.
        /// Returns JSON: {"status":"succeeded","path":"...","source":"scene","width":N,"height":N,"bytes":N,...}
        /// or {"status":"failed","error":"...","error_type":"..."}
        /// </summary>
        public static string CaptureJson()
            => CaptureJson("auto", null, 0, 0);

        /// <summary>
        /// Capture using auto mode. In PlayMode, GameView capture waits for the true
        /// runtime end-of-frame before reading the framebuffer. When width/height are
        /// omitted, GameView output uses the captured framebuffer's native resolution.
        /// </summary>
        public static IEnumerator CaptureJsonAsync()
            => CaptureJsonAsync("auto", null, 0, 0);

        /// <summary>
        /// Capture GameView in PlayMode after WaitForEndOfFrame. This is the preferred
        /// path when ScreenSpaceOverlay UI must be included reliably.
        /// </summary>
        public static IEnumerator CaptureGameViewAsyncJson()
            => CaptureJsonAsync("game", null, 0, 0);

        /// <summary>
        /// Async capture with explicit mode (auto|scene|game), optional path and dimensions.
        /// GameView mode uses a runtime coroutine and WaitForEndOfFrame when PlayMode is active.
        /// Passing width/height <= 0 keeps GameView's native framebuffer resolution.
        /// </summary>
        public static IEnumerator CaptureJsonAsync(string mode, string path, int width, int height)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.ToLowerInvariant();
            NormalizeRequestedSize(width, height, out int gameWidth, out int gameHeight, out int sceneWidth, out int sceneHeight);

            if (mode != "auto" && mode != "scene" && mode != "game")
            {
                yield return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                    null, null, null, null, null, null,
                    null, null, null, null, null,
                    "Invalid screenshot mode. Use auto, scene, or game.", "usage");
                yield break;
            }

            string resolvedPath = ResolvePath(path);
            string dir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            Camera sceneCamera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            Camera gameCamera = mode != "scene" ? FindGameViewCameraCandidate() : null;

            if (mode == "scene" || gameCamera == null)
            {
                if (mode == "game")
                {
                    yield return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                        null, null, null, null, null, null,
                        null, null, null, null, null,
                        "No enabled GameView camera found for game screenshot. Enable a camera that renders to GameView, enter PlayMode, or use auto/scene mode.", "not_supported");
                    yield break;
                }

                yield return CaptureSceneCameraJson(sceneCamera, resolvedPath, sceneWidth, sceneHeight);
                yield break;
            }

            if (!TryStartEndOfFrameGameCapture(resolvedPath, gameWidth, gameHeight, out var pending, out string startError))
            {
                if (mode == "auto" && sceneCamera != null)
                {
                    yield return CaptureSceneCameraJson(sceneCamera, resolvedPath, sceneWidth, sceneHeight);
                    yield break;
                }

                yield return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                    null, null, null, null, null, null,
                    null, null, null, null, null,
                    startError, "not_supported");
                yield break;
            }

            while (!pending.IsDone)
                yield return null;

            if (pending.Result.Success)
            {
                yield return BuildCompletedCaptureJson(resolvedPath, "game",
                    pending.Result.CapturedWidth, pending.Result.CapturedHeight,
                    pending.Result.SourceWidth, pending.Result.SourceHeight);
                yield break;
            }

            if (mode == "auto" && sceneCamera != null && IsNoTextureError(pending.Result.Error))
            {
                yield return CaptureSceneCameraJson(sceneCamera, resolvedPath, sceneWidth, sceneHeight);
                yield break;
            }

            yield return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                null, null, null, null, null, null,
                null, null, null, null, null,
                BuildCaptureFailureMessage(mode, new InvalidOperationException(pending.Result.Error)), "runtime");
        }

        /// <summary>
        /// Capture with explicit mode (auto|scene|game), optional path and dimensions.
        /// Synchronous game mode is intentionally unsupported; use CaptureJsonAsync("game", ...).
        /// path: relative to project root or absolute; null/empty auto-generates under Temp/Harness/vision/.
        /// SceneView width/height <= 0 uses defaults (1280x720); clamped to [64, 7680] x [64, 4320].
        /// Returns JSON: {"status":"succeeded","path":"...","source":"scene","width":N,"height":N,"bytes":N,...}
        /// or {"status":"failed","error":"...","error_type":"not_supported|usage|runtime"}
        /// </summary>
        public static string CaptureJson(string mode, string path, int width, int height)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.ToLowerInvariant();
            NormalizeSceneSize(width, height, out int sceneWidth, out int sceneHeight);

            if (mode != "auto" && mode != "scene" && mode != "game")
                return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                    null, null, null, null, null, null,
                    null, null, null, null, null,
                    "Invalid screenshot mode. Use auto, scene, or game.", "usage");

            if (mode == "game")
                return BuildSyncGameUnsupportedJson();

            string resolvedPath = ResolvePath(path);
            string dir = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            Camera sceneCamera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            return CaptureSceneCameraJson(sceneCamera, resolvedPath, sceneWidth, sceneHeight);
        }

        // ─── New Analysis API ────────────────────────────────────────

        /// <summary>
        /// Build a standard vision analysis request JSON from a question + capture result + optional context.
        /// This can be passed to an external analyzer (CLI, sidecar, or multi-modal API).
        ///
        /// Returns JSON conforming to schema harness.vision.analysis_request.v1.
        /// This method does NOT invoke any external analyzer — it only constructs the request.
        ///
        /// Usage in uh eval -f:
        ///   Harness.Vision.Editor.HarnessVision.BuildAnalysisRequestJson("Find Start button.", captureJson, null)
        ///   Harness.Vision.Editor.HarnessVision.BuildAnalysisRequestJson("What is visible?", captureJson, "{\"task\":\"...\"}")
        /// </summary>
        /// <param name="question">The question the Agent wants answered from the screenshot.</param>
        /// <param name="captureJson">JSON string from a previous CaptureJson() call.</param>
        /// <param name="contextJson">Optional JSON with task/status/hierarchy/uitree/logs context.</param>
        /// <returns>Analysis request JSON string.</returns>
        public static string BuildAnalysisRequestJson(string question, string captureJson, string contextJson)
        {
            if (string.IsNullOrWhiteSpace(question))
                question = "Describe what is visible in the screenshot.";
            if (string.IsNullOrWhiteSpace(captureJson))
                captureJson = "{}";

            return VisionJson.BuildAnalysisRequestJson(question, captureJson, contextJson);
        }

        /// <summary>
        /// Analyze an existing screenshot image with a question and metadata.
        /// Returns JSON with capture metadata + analysis result.
        /// Default provider is "none", so analysis returns status=unavailable unless configured.
        ///
        /// Usage in uh eval -f:
        ///   Harness.Vision.Editor.HarnessVision.AnalyzeImageJson("Where is the Start button?",
        ///       "Temp/Harness/vision/shot.png",
        ///       "{\"width\":1280,\"height\":720}",
        ///       null)
        /// </summary>
        /// <param name="question">The question about the image content.</param>
        /// <param name="imagePath">Path to the PNG screenshot file (relative or absolute).</param>
        /// <param name="imageMetaJson">Optional JSON with image metadata (width, height, source, etc.).</param>
        /// <param name="contextJson">Optional JSON with task/status/uitree context.</param>
        /// <returns>Compound JSON with capture info and analysis.</returns>
        public static string AnalyzeImageJson(string question, string imagePath, string imageMetaJson, string contextJson)
        {
            if (string.IsNullOrWhiteSpace(question))
                question = "Describe what is visible in the screenshot.";

            string captureJson = BuildExistingImageCaptureJson(imagePath, imageMetaJson);
            string analysisJson = captureJson.Contains("\"status\":\"succeeded\"")
                ? VisionJson.BuildAnalysisUnavailableJson(question)
                : VisionJson.BuildAnalysisSkippedJson();
            string compoundStatus = captureJson.Contains("\"status\":\"succeeded\"") ? "partial" : "failed";
            return VisionJson.BuildCompoundCaptureAnalysisJson(captureJson, analysisJson, compoundStatus);
        }

        public static IEnumerator AnalyzeImageAsyncJson(string question, string imagePath, string imageMetaJson, string contextJson)
        {
            if (string.IsNullOrWhiteSpace(question))
                question = "Describe what is visible in the screenshot.";

            string captureJson = BuildExistingImageCaptureJson(imagePath, imageMetaJson);
            if (!captureJson.Contains("\"status\":\"succeeded\""))
            {
                yield return VisionJson.BuildCompoundCaptureAnalysisJson(
                    captureJson,
                    VisionJson.BuildAnalysisSkippedJson(),
                    "failed");
                yield break;
            }

            var sub = AnalyzeCaptureCoroutine(question, captureJson, contextJson);
            while (sub.MoveNext())
                yield return sub.Current;
        }

        /// <summary>
        /// Capture a screenshot and attempt visual analysis in one call.
        /// Returns compound JSON with capture info and analysis result.
        /// Default provider is "none", so analysis returns status=unavailable unless configured.
        ///
        /// Usage in uh eval -f:
        ///   Harness.Vision.Editor.HarnessVision.CaptureAndAnalyzeJson(
        ///       "Find the Start button and return clickable coordinates.",
        ///       "game", null, 0, 0, null)
        /// </summary>
        /// <param name="question">The question the Agent wants answered from the screenshot.</param>
        /// <param name="mode">Capture mode: "auto", "scene", or "game".</param>
        /// <param name="path">Optional output path (relative or absolute).</param>
        /// <param name="width">Output width (0 for default 1280).</param>
        /// <param name="height">Output height (0 for default 720).</param>
        /// <param name="contextJson">Optional JSON context (task, status, hierarchy, uitree, logs).</param>
        /// <returns>Compound JSON with capture + analysis.</returns>
        public static string CaptureAndAnalyzeJson(string question, string mode, string path, int width, int height, string contextJson)
        {
            if (string.IsNullOrWhiteSpace(question))
                question = "Describe what is visible in the screenshot.";

            string captureJson = CaptureJson(mode, path, width, height);
            string analysisJson = captureJson.Contains("\"status\":\"succeeded\"")
                ? VisionJson.BuildAnalysisUnavailableJson(question)
                : VisionJson.BuildAnalysisSkippedJson();
            string compoundStatus = captureJson.Contains("\"status\":\"succeeded\"") ? "partial" : "failed";
            return VisionJson.BuildCompoundCaptureAnalysisJson(captureJson, analysisJson, compoundStatus);
        }

        public static IEnumerator CaptureAndAnalyzeAsyncJson(string question, string mode, string path, int width, int height, string contextJson)
        {
            if (string.IsNullOrWhiteSpace(question))
                question = "Describe what is visible in the screenshot.";

            string captureJson = null;
            var capture = CaptureJsonAsync(mode, path, width, height);
            while (capture.MoveNext())
            {
                if (capture.Current is string current)
                    captureJson = current;
                else
                    yield return capture.Current;
            }

            if (string.IsNullOrEmpty(captureJson) || !captureJson.Contains("\"status\":\"succeeded\""))
            {
                yield return VisionJson.BuildCompoundCaptureAnalysisJson(
                    captureJson,
                    VisionJson.BuildAnalysisSkippedJson(),
                    "failed");
                yield break;
            }

            var sub = AnalyzeCaptureCoroutine(question, captureJson, contextJson);
            while (sub.MoveNext())
                yield return sub.Current;
        }

        public static string GetVisionSettingsJson()
        {
            var settings = VisionSettings.Instance;
            return VisionJson.BuildProviderTestJson(
                "configured",
                settings.ProviderNormalized,
                settings.OpenAiModelOrDefault,
                settings.BuildOpenAiEndpoint(),
                settings.HasOpenAiApiKey,
                settings.OpenAiApiKeySource,
                -1,
                null,
                null);
        }

        public static IEnumerator TestVisionProviderJson()
        {
            var sub = OpenAiCompatibleVisionAnalyzer.TestProviderJson();
            while (sub.MoveNext())
                yield return sub.Current;
        }

        // ─── Private Helpers ─────────────────────────────────────────

        private static IEnumerator AnalyzeCaptureCoroutine(string question, string captureJson, string contextJson)
        {
            string provider = VisionSettings.Instance.ProviderNormalized;
            if (provider == "openai-compatible")
            {
                object last = null;
                var analyzer = OpenAiCompatibleVisionAnalyzer.AnalyzeJson(question, captureJson, contextJson);
                while (analyzer.MoveNext())
                {
                    last = analyzer.Current;
                    yield return analyzer.Current;
                }

                string analysisJson = last as string ?? VisionJson.BuildAnalysisFailedJson(
                    question,
                    "OpenAI-compatible analyzer returned no result.",
                    "runtime",
                    "openai-compatible",
                    VisionSettings.Instance.OpenAiModelOrDefault,
                    VisionSettings.Instance.BuildOpenAiEndpoint(),
                    -1);
                string status = analysisJson.Contains("\"status\":\"succeeded\"") ? "succeeded" : "partial";
                yield return VisionJson.BuildCompoundCaptureAnalysisJson(captureJson, analysisJson, status);
                yield break;
            }

            yield return VisionJson.BuildCompoundCaptureAnalysisJson(
                captureJson,
                VisionJson.BuildAnalysisUnavailableJson(question),
                "partial");
        }

        private static string BuildExistingImageCaptureJson(string imagePath, string imageMetaJson)
        {
            string resolvedPath = ResolvePath(imagePath);
            if (!File.Exists(resolvedPath))
            {
                return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                    null, null, null, null, null, null,
                    null, null, null, null, null,
                    "Image file not found: " + resolvedPath, "runtime");
            }

            int width = 0;
            int height = 0;
            long bytes = 0;

            try
            {
                bytes = new FileInfo(resolvedPath).Length;
                ReadPngDimensions(resolvedPath, out width, out height);
            }
            catch
            {
                // The file exists, but metadata probing is best-effort for analyzer requests.
            }

            return VisionJson.BuildCaptureJsonFromMetadata(
                resolvedPath,
                imageMetaJson,
                width,
                height,
                bytes,
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        }

        internal static void NormalizeRequestedSize(int requestedWidth, int requestedHeight,
            out int gameWidth, out int gameHeight, out int sceneWidth, out int sceneHeight)
        {
            NormalizeSceneSize(requestedWidth, requestedHeight, out sceneWidth, out sceneHeight);

            if (requestedWidth <= 0 && requestedHeight <= 0)
            {
                gameWidth = 0;
                gameHeight = 0;
                return;
            }

            gameWidth = requestedWidth > 0 ? Mathf.Clamp(requestedWidth, MinCaptureWidth, MaxCaptureWidth) : 0;
            gameHeight = requestedHeight > 0 ? Mathf.Clamp(requestedHeight, MinCaptureHeight, MaxCaptureHeight) : 0;
        }

        private static void NormalizeSceneSize(int requestedWidth, int requestedHeight, out int width, out int height)
        {
            width = Mathf.Clamp(requestedWidth > 0 ? requestedWidth : DefaultSceneCaptureWidth, MinCaptureWidth, MaxCaptureWidth);
            height = Mathf.Clamp(requestedHeight > 0 ? requestedHeight : DefaultSceneCaptureHeight, MinCaptureHeight, MaxCaptureHeight);
        }

        internal static Camera FindGameViewCameraCandidate()
        {
            if (IsGameViewCameraCandidate(Camera.main))
                return Camera.main;

            foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                if (IsGameViewCameraCandidate(camera))
                    return camera;
            }

            return null;
        }

        internal static bool IsGameViewCameraCandidate(Camera camera)
        {
            return camera != null &&
                   camera.enabled &&
                   camera.gameObject != null &&
                   camera.gameObject.activeInHierarchy &&
                   camera.targetTexture == null;
        }

        private static string CaptureSceneCameraJson(Camera sceneCamera, string path, int width, int height)
        {
            if (sceneCamera == null)
                return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                    null, null, null, null, null, null,
                    null, null, null, null, null,
                    "No SceneView or GameView camera is available. Open SceneView, enter PlayMode, or use an explicit mode.", "not_supported");

            try
            {
                RenderCamera(sceneCamera, path, width, height);
                return BuildCompletedCaptureJson(path, "scene", width, height, width, height);
            }
            catch (Exception ex)
            {
                return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                    null, null, null, null, null, null,
                    null, null, null, null, null,
                    "Failed to capture screenshot: " + ex.Message, "runtime");
            }
        }

        private static bool TryStartEndOfFrameGameCapture(string path, int width, int height,
            out HarnessVisionRuntimeCaptureRequest request, out string error)
        {
            return HarnessVisionRuntimeCaptureRunner.TryCaptureGameViewEndOfFrame(
                path, width, height, out request, out error);
        }

        private static string BuildCompletedCaptureJson(string path, string source,
            int capturedWidth, int capturedHeight, int sourceWidth, int sourceHeight)
        {
            var info = new FileInfo(path);
            string capturedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            float? gameViewWidth = null, gameViewHeight = null;
            float? scaleX = null, scaleY = null;
            float? s2gX = null, s2gY = null;

            if (source == "game")
            {
                Vector2 gvSize = PiGameViewCoordinates.GetGameViewSize();
                gameViewWidth = gvSize.x;
                gameViewHeight = gvSize.y;
                scaleX = gvSize.x > 0 ? (float)capturedWidth / gvSize.x : 0f;
                scaleY = gvSize.y > 0 ? (float)capturedHeight / gvSize.y : 0f;
                s2gX = capturedWidth > 0 ? gvSize.x / capturedWidth : 0f;
                s2gY = capturedHeight > 0 ? gvSize.y / capturedHeight : 0f;
            }

            return VisionJson.BuildCaptureJson(
                "succeeded", path, source,
                capturedWidth, capturedHeight, info.Length,
                capturedWidth, capturedHeight,
                gameViewWidth, gameViewHeight,
                sourceWidth, sourceHeight,
                scaleX, scaleY,
                s2gX, s2gY,
                capturedAtUtc);
        }

        private static bool IsNoTextureError(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                   message.IndexOf("no texture", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildSyncGameUnsupportedJson()
        {
            return VisionJson.BuildCaptureJson("failed", null, null, 0, 0, 0,
                null, null, null, null, null, null,
                null, null, null, null, null,
                "Synchronous GameView capture is not supported because ScreenCapture can return null before end-of-frame. Use CaptureGameViewAsyncJson() or CaptureJsonAsync(\"game\", ...).", "not_supported");
        }

        private static string BuildCaptureFailureMessage(string mode, Exception ex)
        {
            string message = ex.Message ?? "Unknown capture failure.";
            if (message.IndexOf("no texture", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (mode == "game")
                    return "Failed to capture GameView: GameView is not ready yet. Enter PlayMode, focus GameView, or use auto/scene mode.";

                return "Failed to capture screenshot: GameView capture returned no texture. Enter PlayMode, focus GameView, or use scene mode.";
            }

            return "Failed to capture screenshot: " + message;
        }

        private static void RenderCamera(Camera camera, string path, int width, int height)
        {
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var renderTexture = new RenderTexture(width, height, 24);

            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static string ResolvePath(string requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                string p = requestedPath;
                if (!Path.IsPathRooted(p))
                    p = Path.Combine(ProjectRoot, p);
                return Path.GetFullPath(p);
            }

            string dir = Path.Combine(ProjectRoot, "Temp", "Harness", "vision");
            string file = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png";
            return Path.Combine(dir, file);
        }

        private static void ReadPngDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            byte[] header = new byte[24];
            using (var stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, header.Length) < header.Length)
                    return;
            }

            if (header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71)
                return;

            width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        }

        private static string ProjectRoot =>
            Directory.GetParent(Application.dataPath).FullName;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using Pi.UnityHarness.Runtime.Capabilities.Vision;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Vision
{
    /// <summary>
    /// playtest-loop-borrow-plan Phase 1：感知原语。
    /// 只新增能力，不改动 HarnessVision 现有 CaptureJson* 行为。
    ///
    /// 契约（对应源 game_test_agent._run）：
    /// - vision_observe：3 帧捕获（帧轮询 + 单帧超时）+ 文件 sha256 精确去重
    ///   + dHash 亮度指纹 + 0-1000 坐标网格叠图 + 画面变化时才合成时间序列图
    /// - vision_capture_after：动作后 short(8@120ms)/burst(24@80ms) 连拍 + 时间戳序列图
    ///
    /// 纯算法部分（NormalizedToPixel/DHashFingerprint/ComputeChanged/DrawNormalizedGrid/
    /// ComposeSheet/Build*Json）不依赖真实渲染，Editor 测试可直接覆盖。
    /// </summary>
    public static class PlaytestVision
    {
        public const string ObserveSchema = "harness.vision.observe.v1";
        public const string CaptureAfterSchema = "harness.vision.capture_after.v1";
        public const int DefaultFrames = 3;
        public const int DefaultIntervalMs = 160;
        public const int MaxFrames = 8;
        public const int ShortFrameCount = 8;
        public const int BurstFrameCount = 24;

        private const float DiffThreshold = 0.00005f;
        private const string DefaultPrefix = "Library/PiUnityHarness/playtest/observe";
        private const string DefaultAfterPrefix = "Library/PiUnityHarness/playtest/after";

        // ─── 0-1000 网格像素映射（源 _make_coordinate_grid：x = round(v * (w-1) / 1000)）───

        public static int NormalizedToPixel(int normalized, int pixelCount)
        {
            int value = Mathf.Clamp(normalized, 0, 1000);
            return Mathf.RoundToInt(value * (pixelCount - 1) / 1000f);
        }

        // ─── dHash 指纹（17x16 亮度、行内相邻比较、64 hex；缩放滤波固定为双线性）───

        public static string DHashFingerprint(Texture2D source)
        {
            const int width = 17;
            const int height = 16;

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(
                width, height, 0, RenderTextureFormat.ARGB32);
            var tiny = new Texture2D(width, height, TextureFormat.RGBA32, false);
            Color32[] pixels;
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                tiny.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tiny.Apply();
                pixels = tiny.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(tiny);
            }

            var bytes = new byte[32];
            for (int row = 0; row < height; row++)
            {
                int offset = row * width;
                for (int col = 0; col < width - 1; col++)
                {
                    // 亮度（源 PIL convert("L") 同式）：0.299R + 0.587G + 0.114B
                    float lumaLeft = 0.299f * pixels[offset + col].r
                                   + 0.587f * pixels[offset + col].g
                                   + 0.114f * pixels[offset + col].b;
                    float lumaRight = 0.299f * pixels[offset + col + 1].r
                                    + 0.587f * pixels[offset + col + 1].g
                                    + 0.114f * pixels[offset + col + 1].b;
                    if (lumaLeft > lumaRight)
                    {
                        int bitIndex = row * (width - 1) + col;
                        bytes[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
                    }
                }
            }
            var builder = new StringBuilder(64);
            foreach (byte b in bytes)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        // ─── 帧差异判定（同尺寸缩略绝对差和，归一化阈值；诊断用）───

        public static bool ComputeChanged(Texture2D a, Texture2D b)
        {
            if (a == null || b == null || a.width != b.width || a.height != b.height)
                return true;

            Color32[] pa = a.GetPixels32();
            Color32[] pb = b.GetPixels32();
            long total = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                total += Mathf.Abs(pa[i].r - pb[i].r);
                total += Mathf.Abs(pa[i].g - pb[i].g);
                total += Mathf.Abs(pa[i].b - pb[i].b);
            }
            return total / (float)(pa.Length * 3 * 255) > DiffThreshold;
        }

        // ─── 0-1000 坐标网格叠图（视觉尺；叠加在既有画面上，绝不清空原图）───

        public static void DrawNormalizedGrid(Texture2D texture)
        {
            Color lineColor = new Color(68f / 255f, 220f / 255f, 210f / 255f, 85f / 255f);
            Color labelColor = new Color(180f / 255f, 255f / 255f, 245f / 255f, 230f / 255f);

            int width = texture.width;
            int height = texture.height;
            for (int value = 100; value < 1000; value += 100)
            {
                int x = NormalizedToPixel(value, width);
                int y = NormalizedToPixel(value, height);
                DrawLine(texture, x, 0, x, height - 1, lineColor);
                DrawLine(texture, 0, y, width - 1, y, lineColor);
                string label = value.ToString();
                // 竖线标签画在图像顶部；横线标签画在左侧（纹理左下原点）
                DrawLabelBlock(texture, label, x + 2, height - 1 - 2, labelColor);
                DrawLabelBlock(texture, label, 2, y + 2, labelColor);
            }
            texture.Apply();
        }

        // ─── 时间序列 / 动作后序列图合成（源 _make_timeline / _make_sequence_sheet）───

        public static string ComposeSheet(
            IReadOnlyList<Texture2D> thumbs,
            IReadOnlyList<string> labels,
            int thumbWidth,
            string outputPath)
        {
            if (thumbs == null || thumbs.Count == 0)
                throw new ArgumentException("No frames to compose.");

            const int gap = 8;
            const int labelHeight = 24;
            int thumbHeight = thumbs[0].height > 0
                ? Mathf.Max(1, Mathf.RoundToInt(thumbWidth * thumbs[0].height / (float)thumbs[0].width))
                : thumbWidth;

            int canvasWidth = gap + thumbs.Count * (thumbWidth + gap);
            int canvasHeight = labelHeight + thumbHeight + gap;
            var canvas = new Texture2D(canvasWidth, canvasHeight, TextureFormat.RGB24, false);
            try
            {
                var background = new Color32(22, 25, 24, 255);
                var fill = new Color32[canvasWidth * canvasHeight];
                for (int i = 0; i < fill.Length; i++)
                    fill[i] = background;
                canvas.SetPixels32(fill);

                int x = gap;
                for (int i = 0; i < thumbs.Count; i++)
                {
                    Texture2D thumb = thumbs[i];
                    int y = labelHeight;
                    Texture2D resized = thumb;
                    Texture2D owned = null;
                    if (thumb.width != thumbWidth || thumb.height != thumbHeight)
                    {
                        owned = Resize(thumb, thumbWidth, thumbHeight);
                        resized = owned;
                    }
                    try
                    {
                        Color32[] pixels = resized.GetPixels32();
                        canvas.SetPixels32(x, y, thumbWidth, thumbHeight, pixels);
                    }
                    finally
                    {
                        if (owned != null)
                            UnityEngine.Object.DestroyImmediate(owned);
                    }
                    string label = i < labels.Count ? labels[i] : $"FRAME {i + 1} / {thumbs.Count}";
                    DrawText(canvas, label, x + 3, canvasHeight - 1 - 3, 2,
                        new Color(225f / 255f, 230f / 255f, 225f / 255f, 1f));
                    x += thumbWidth + gap;
                }

                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(outputPath, canvas.EncodeToJPG(88));
                return outputPath;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas);
            }
        }

        // ─── JSON schema ───

        public static string BuildObserveJson(
            string status, string error, string errorType,
            List<string> frames, string latest, string vision, string timeline,
            string fingerprint, bool changed, bool timedOut, int uniqueCount,
            int capturedCount, int deduplicatedCount,
            float gameViewWidth, float gameViewHeight,
            float screenshotToGameviewX, float screenshotToGameviewY,
            string capturedAtUtc, string annotationsJson = null,
            List<string> warnings = null)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"status\":\"").Append(status).Append('"');
            sb.Append(",\"schema\":\"").Append(ObserveSchema).Append('"');
            sb.Append(",\"warnings\":");
            if (warnings == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('[');
                for (int i = 0; i < warnings.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonString(warnings[i]));
                }
                sb.Append(']');
            }
            if (status == "failed")
            {
                sb.Append(",\"error\":").Append(JsonString(error));
                sb.Append(",\"error_type\":").Append(JsonString(errorType));
                sb.Append(",\"frames\":");
                if (frames == null)
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append('[');
                    for (int i = 0; i < frames.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(JsonString(frames[i]));
                    }
                    sb.Append(']');
                }
                sb.Append(",\"latest\":").Append(JsonString(latest));
                sb.Append(",\"vision\":").Append(JsonString(vision));
                sb.Append(",\"timeline\":").Append(JsonString(timeline));
                sb.Append(",\"fingerprint\":").Append(JsonString(fingerprint));
                sb.Append(",\"changed\":").Append(changed ? "true" : "false");
                sb.Append(",\"timed_out\":").Append(timedOut ? "true" : "false");
                sb.Append(",\"unique_count\":").Append(uniqueCount);
                sb.Append(",\"captured_count\":").Append(capturedCount);
                sb.Append(",\"deduplicated_count\":").Append(deduplicatedCount);
                sb.Append(",\"gameview_size\":null,\"screenshot_to_gameview\":null");
                sb.Append(",\"captured_at_utc\":").Append(JsonString(capturedAtUtc));
                sb.Append('}');
                return sb.ToString();
            }

            sb.Append(",\"frames\":[");
            if (frames != null)
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonString(frames[i]));
                }
            }
            sb.Append(']');
            sb.Append(",\"latest\":").Append(JsonString(latest));
            sb.Append(",\"vision\":").Append(JsonString(vision));
            sb.Append(",\"timeline\":").Append(JsonString(timeline));
            sb.Append(",\"fingerprint\":").Append(JsonString(fingerprint));
            sb.Append(",\"changed\":").Append(changed ? "true" : "false");
            sb.Append(",\"timed_out\":").Append(timedOut ? "true" : "false");
            sb.Append(",\"unique_count\":").Append(uniqueCount);
            sb.Append(",\"captured_count\":").Append(capturedCount);
            sb.Append(",\"deduplicated_count\":").Append(deduplicatedCount);
            sb.Append(",\"gameview_size\":{\"x\":").Append(F(gameViewWidth))
                .Append(",\"y\":").Append(F(gameViewHeight)).Append('}');
            sb.Append(",\"screenshot_to_gameview\":{\"x\":").Append(F(screenshotToGameviewX))
                .Append(",\"y\":").Append(F(screenshotToGameviewY)).Append('}');
            sb.Append(",\"captured_at_utc\":").Append(JsonString(capturedAtUtc));
            if (!string.IsNullOrEmpty(annotationsJson))
                sb.Append(",\"annotations\":").Append(annotationsJson);
            sb.Append('}');
            return sb.ToString();
        }

        public static string BuildCaptureAfterJson(
            string status, string error, string errorType,
            string mode, string sheet, List<string> frames, List<int> timingsMs,
            string fingerprint, bool timedOut, int uniqueCount, int capturedCount,
            string capturedAtUtc)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"status\":\"").Append(status).Append('"');
            sb.Append(",\"schema\":\"").Append(CaptureAfterSchema).Append('"');
            if (status == "failed")
            {
                sb.Append(",\"error\":").Append(JsonString(error));
                sb.Append(",\"error_type\":").Append(JsonString(errorType));
                sb.Append(",\"mode\":").Append(JsonString(mode));
                sb.Append(",\"sheet\":null,\"frames\":null,\"timings_ms\":null");
                sb.Append(",\"fingerprint\":null");
                sb.Append(",\"timed_out\":").Append(timedOut ? "true" : "false");
                sb.Append(",\"unique_count\":0,\"captured_count\":0,\"captured_at_utc\":null");
                sb.Append('}');
                return sb.ToString();
            }

            sb.Append(",\"mode\":").Append(JsonString(mode));
            sb.Append(",\"sheet\":").Append(JsonString(sheet));
            sb.Append(",\"frames\":[");
            if (frames != null)
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonString(frames[i]));
                }
            }
            sb.Append(']');
            sb.Append(",\"timings_ms\":[");
            if (timingsMs != null)
            {
                for (int i = 0; i < timingsMs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(timingsMs[i]);
                }
            }
            sb.Append(']');
            sb.Append(",\"fingerprint\":").Append(JsonString(fingerprint));
            sb.Append(",\"timed_out\":").Append(timedOut ? "true" : "false");
            sb.Append(",\"unique_count\":").Append(uniqueCount);
            sb.Append(",\"captured_count\":").Append(capturedCount);
            sb.Append(",\"captured_at_utc\":").Append(JsonString(capturedAtUtc));
            sb.Append('}');
            return sb.ToString();
        }

        // ─── 观察协程（Editor 侧，轮询 runtime request；总超时兜底并传播取消）───

        public static IEnumerator ObserveJsonAsync(
            string mode, int frames, int intervalMs, string overlay, string pathPrefix,
            int timeoutMs = 60000, float perFrameTimeoutMs = 500f)
        {
            string normalizedMode = string.IsNullOrWhiteSpace(mode) ? "game" : mode.ToLowerInvariant();
            // auto 与 game 等价：跑测必须看 PlayMode 画面，绝不静默落到 SceneView
            if (normalizedMode == "auto")
                normalizedMode = "game";
            if (normalizedMode != "game")
            {
                yield return BuildObserveJson(
                    "failed", "Only game mode is supported; PlayMode is required.",
                    "usage", null, null, null, null, null, false, false, 0, 0, 0,
                    0f, 0f, 0f, 0f, null, null);
                yield break;
            }

            int frameCount = Mathf.Clamp(frames > 0 ? frames : DefaultFrames, 1, MaxFrames);
            int gapMs = Mathf.Clamp(intervalMs, 0, 5000);
            string overlayMode = NormalizeOverlay(overlay);
            string resolvedPrefix = ResolvePathPrefix(pathPrefix, DefaultPrefix);
            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 5000;

            if (!HarnessVisionPlaytestRunner.TryStartObserve(
                    resolvedPrefix, frameCount, gapMs, perFrameTimeoutMs,
                    out var request, out string startError))
            {
                yield return BuildObserveJson(
                    "failed", startError, "not_supported",
                    null, null, null, null, null, false, false, 0, 0, 0,
                    0f, 0f, 0f, 0f, null);
                yield break;
            }

            float t0 = Time.realtimeSinceStartup;
            while (!request.IsDone)
            {
                if (Time.realtimeSinceStartup - t0 >= effectiveTimeoutMs / 1000f)
                    request.CancelRequested = true;
                yield return null;
            }

            HarnessVisionPlaytestResult result = request.Result;
            if (result == null || !result.Success)
            {
                string errorType = result != null && result.TimedOut ? "timeout" : "runtime";
                if (result != null)
                {
                    foreach (Texture2D thumb in result.Thumbs)
                    {
                        if (thumb != null)
                            UnityEngine.Object.DestroyImmediate(thumb);
                    }
                }
                // 中途超时/失败也带回已捕获的帧路径，便于 Agent 诊断
                yield return BuildObserveJson(
                    "failed", result != null ? result.Error : "observe failed", errorType,
                    result != null ? result.FramePaths : null,
                    result != null && result.FramePaths.Count > 0 ? result.FramePaths[result.FramePaths.Count - 1] : null,
                    null, null, null, false,
                    result != null && result.TimedOut,
                    result != null ? result.FramePaths.Count : 0,
                    result != null ? result.CapturedCount : 0,
                    result != null ? result.DeduplicatedCount : 0,
                    0f, 0f, 0f, 0f, null);
                yield break;
            }

            try
            {
                // 差异判定 + 最新帧 dHash（双线性 17x16 亮度）
                bool changed = result.FramePaths.Count > 1;
                string fingerprint = result.Thumbs.Count > 0
                    ? DHashFingerprint(result.Thumbs[result.Thumbs.Count - 1])
                    : null;

                // 网格叠图：读最新帧原图，叠加 0-1000 坐标尺，再按 overlay 追加 UI/物理标注
                string latest = result.FramePaths.Count > 0
                    ? result.FramePaths[result.FramePaths.Count - 1]
                    : null;
                var warnings = new List<string>();
                string visionPath = null;
                string annotationsJson = null;
                if (!string.IsNullOrEmpty(latest) && overlayMode != "none")
                {
                    visionPath = resolvedPrefix + "_vision.png";
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(latest);
                        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        try
                        {
                            if (source.LoadImage(bytes))
                            {
                                if (overlayMode == "grid" || overlayMode == "both")
                                    DrawNormalizedGrid(source);
                                File.WriteAllBytes(visionPath, source.EncodeToPNG());

                                if (overlayMode == "annotations" || overlayMode == "both")
                                {
                                    var set = PiVisionAnnotator.Collect();
                                    annotationsJson = PiVisionAnnotator.ToJsonObject(set);
                                    PiVisionAnnotator.DrawMarkersOnPng(visionPath, set);
                                }
                            }
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(source);
                        }
                    }
                    catch
                    {
                        visionPath = null;
                    }
                }

                // 时间序列图：仅画面有变化时合成（源 _make_timeline）；
                // 写盘失败不改变 changed 语义，进 warnings
                string timelinePath = null;
                if (changed && result.Thumbs.Count > 1)
                {
                    timelinePath = resolvedPrefix + "_timeline.jpg";
                    try
                    {
                        var labels = new List<string>(result.Thumbs.Count);
                        for (int i = 0; i < result.Thumbs.Count; i++)
                            labels.Add($"FRAME {i + 1} / {result.Thumbs.Count}");
                        ComposeSheet(result.Thumbs, labels, 480, timelinePath);
                    }
                    catch (Exception ex)
                    {
                        timelinePath = null;
                        warnings.Add($"timeline sheet failed: {ex.Message}");
                    }
                }

                Vector2 gameViewSize = PiGameViewCoordinates.GetGameViewSize();
                float s2gX = result.CapturedWidth > 0 ? gameViewSize.x / result.CapturedWidth : 0f;
                float s2gY = result.CapturedHeight > 0 ? gameViewSize.y / result.CapturedHeight : 0f;

                string json = BuildObserveJson(
                    "succeeded", null, null,
                    result.FramePaths, latest, visionPath, timelinePath,
                    fingerprint, changed, false, result.FramePaths.Count,
                    result.CapturedCount, result.DeduplicatedCount,
                    gameViewSize.x, gameViewSize.y, s2gX, s2gY,
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    annotationsJson, warnings);

                yield return json;
            }
            finally
            {
                foreach (Texture2D thumb in result.Thumbs)
                {
                    if (thumb != null)
                        UnityEngine.Object.DestroyImmediate(thumb);
                }
            }
        }

        // ─── 动作后连拍协程 ───

        public static IEnumerator CaptureAfterJsonAsync(
            string mode, string pathPrefix, int timeoutMs = 60000, float perFrameTimeoutMs = 500f)
        {
            string normalizedMode = string.IsNullOrWhiteSpace(mode) ? "short" : mode.ToLowerInvariant();
            if (normalizedMode != "short" && normalizedMode != "burst")
            {
                yield return BuildCaptureAfterJson(
                    "failed", "mode must be short or burst.", "usage",
                    normalizedMode, null, null, null, null, false, 0, 0, null);
                yield break;
            }

            string resolvedPrefix = ResolvePathPrefix(
                pathPrefix, DefaultAfterPrefix);
            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 15000;

            if (!HarnessVisionPlaytestRunner.TryStartCaptureAfter(
                    resolvedPrefix, normalizedMode, perFrameTimeoutMs,
                    out var request, out string startError))
            {
                yield return BuildCaptureAfterJson(
                    "failed", startError, "not_supported",
                    normalizedMode, null, null, null, null, false, 0, 0, null);
                yield break;
            }

            float t0 = Time.realtimeSinceStartup;
            while (!request.IsDone)
            {
                if (Time.realtimeSinceStartup - t0 >= effectiveTimeoutMs / 1000f)
                    request.CancelRequested = true;
                yield return null;
            }

            HarnessVisionPlaytestResult result = request.Result;
            if (result == null || !result.Success)
            {
                string errorType = result != null && result.TimedOut ? "timeout" : "runtime";
                if (result != null)
                {
                    foreach (Texture2D thumb in result.Thumbs)
                    {
                        if (thumb != null)
                            UnityEngine.Object.DestroyImmediate(thumb);
                    }
                }
                yield return BuildCaptureAfterJson(
                    "failed", result != null ? result.Error : "capture_after failed", errorType,
                    normalizedMode, null, null, null, null,
                    result != null && result.TimedOut, 0, 0, null);
                yield break;
            }

            try
            {
                string fingerprint = result.Thumbs.Count > 0
                    ? DHashFingerprint(result.Thumbs[result.Thumbs.Count - 1])
                    : null;

                string sheet = resolvedPrefix + $"_after_{normalizedMode}_sheet.jpg";
                var labels = new List<string>(result.Thumbs.Count);
                for (int i = 0; i < result.Thumbs.Count; i++)
                {
                    int elapsed = i < result.TimingsMs.Count ? result.TimingsMs[i] : 0;
                    labels.Add($"{i}  {elapsed}ms");
                }
                ComposeSheet(result.Thumbs, labels, 128, sheet);

                string json = BuildCaptureAfterJson(
                    "succeeded", null, null,
                    normalizedMode, sheet, result.FramePaths, result.TimingsMs,
                    fingerprint, false, result.FramePaths.Count, result.CapturedCount,
                    DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

                yield return json;
            }
            finally
            {
                foreach (Texture2D thumb in result.Thumbs)
                {
                    if (thumb != null)
                        UnityEngine.Object.DestroyImmediate(thumb);
                }
            }
        }

        // ─── helpers ───

        private static string NormalizeOverlay(string overlay)
        {
            string value = string.IsNullOrWhiteSpace(overlay) ? "both" : overlay.ToLowerInvariant();
            return value == "none" || value == "grid" || value == "annotations" ? value : "both";
        }

        private static string ResolvePathPrefix(string requested, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string path = requested;
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(ProjectRoot, path);
                return Path.GetFullPath(path);
            }
            return Path.Combine(ProjectRoot, fallback);
        }

        private static string ProjectRoot =>
            Directory.GetParent(Application.dataPath).FullName;

        private static void DrawLine(Texture2D texture, int x0, int y0, int x1, int y1, Color color)
        {
            int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
            for (int i = 0; i <= steps; i++)
            {
                int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, steps == 0 ? 0f : (float)i / steps));
                int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, steps == 0 ? 0f : (float)i / steps));
                if (x < 0 || y < 0 || x >= texture.width || y >= texture.height)
                    continue;
                Color existing = texture.GetPixel(x, y);
                texture.SetPixel(x, y, AlphaBlend(existing, color));
            }
            // Apply 由调用方统一执行（DrawNormalizedGrid 末尾），避免逐线刷 GPU
        }

        private static void DrawLabelBlock(Texture2D texture, string text, int x, int y, Color color)
        {
            // 半透明深色底块（纹理左下原点；5x7 字体放大 2 倍 = 10x14）
            int blockWidth = 4 + text.Length * 12;
            int blockHeight = 16;
            for (int by = 0; by < blockHeight; by++)
            {
                for (int bx = 0; bx < blockWidth; bx++)
                {
                    int px = x + bx;
                    int py = y - by;
                    if (px < 0 || py < 0 || px >= texture.width || py >= texture.height)
                        continue;
                    texture.SetPixel(px, py, AlphaBlend(
                        texture.GetPixel(px, py), new Color(10f / 255f, 20f / 255f, 20f / 255f, 150f / 255f)));
                }
            }
            DrawText(texture, text, x + 2, y - 1, 2, color);
        }

        /// <summary>5x7 点阵字体绘制（无第三方字体依赖，确定性，可单测）。</summary>
        public static void DrawText(Texture2D texture, string text, int x, int y, int scale, Color color)
        {
            string upper = text.ToUpperInvariant();
            int px = x;
            foreach (char ch in upper)
            {
                string glyph = Glyph(ch);
                if (glyph == null)
                {
                    px += 3 * scale;
                    continue;
                }
                for (int row = 0; row < 7; row++)
                {
                    int bits = Convert.ToInt32(glyph.Substring(row * 5, 5), 2);
                    for (int col = 0; col < 5; col++)
                    {
                        if ((bits & (1 << (4 - col))) == 0)
                            continue;
                        for (int dy = 0; dy < scale; dy++)
                        {
                            for (int dx = 0; dx < scale; dx++)
                            {
                                int pxp = px + col * scale + dx;
                                int pyp = y - row * scale - dy;
                                if (pxp < 0 || pyp < 0 || pxp >= texture.width || pyp >= texture.height)
                                    continue;
                                texture.SetPixel(pxp, pyp, AlphaBlend(texture.GetPixel(pxp, pyp), color));
                            }
                        }
                    }
                }
                px += 6 * scale;
            }
            texture.Apply();
        }

        private static string Glyph(char ch)
        {
            switch (ch)
            {
                case ' ': return "00000" + "00000" + "00000" + "00000" + "00000" + "00000" + "00000";
                case '/': return "00001" + "00010" + "00010" + "00100" + "01000" + "01000" + "10000";
                case ':': return "00000" + "00100" + "00100" + "00000" + "00100" + "00100" + "00000";
                case '-': return "00000" + "00000" + "00000" + "11111" + "00000" + "00000" + "00000";
                case '0': return "01110" + "10001" + "10011" + "10101" + "11001" + "10001" + "01110";
                case '1': return "00100" + "01100" + "00100" + "00100" + "00100" + "00100" + "01110";
                case '2': return "01110" + "10001" + "00001" + "00010" + "00100" + "01000" + "11111";
                case '3': return "11111" + "00010" + "00100" + "00010" + "00001" + "10001" + "01110";
                case '4': return "00010" + "00110" + "01010" + "10010" + "11111" + "00010" + "00010";
                case '5': return "11111" + "10000" + "11110" + "00001" + "00001" + "10001" + "01110";
                case '6': return "00110" + "01000" + "10000" + "11110" + "10001" + "10001" + "01110";
                case '7': return "11111" + "00001" + "00010" + "00100" + "01000" + "01000" + "01000";
                case '8': return "01110" + "10001" + "10001" + "01110" + "10001" + "10001" + "01110";
                case '9': return "01110" + "10001" + "10001" + "01111" + "00001" + "00010" + "01100";
                case 'A': return "01110" + "10001" + "10001" + "11111" + "10001" + "10001" + "10001";
                case 'B': return "11110" + "10001" + "10001" + "11110" + "10001" + "10001" + "11110";
                case 'C': return "01110" + "10001" + "10000" + "10000" + "10000" + "10001" + "01110";
                case 'D': return "11110" + "10001" + "10001" + "10001" + "10001" + "10001" + "11110";
                case 'E': return "11111" + "10000" + "10000" + "11110" + "10000" + "10000" + "11111";
                case 'F': return "11111" + "10000" + "10000" + "11110" + "10000" + "10000" + "10000";
                case 'G': return "01110" + "10001" + "10000" + "10111" + "10001" + "10001" + "01111";
                case 'H': return "10001" + "10001" + "10001" + "11111" + "10001" + "10001" + "10001";
                case 'I': return "01110" + "00100" + "00100" + "00100" + "00100" + "00100" + "01110";
                case 'J': return "00111" + "00010" + "00010" + "00010" + "00010" + "10010" + "01100";
                case 'K': return "10001" + "10010" + "10100" + "11000" + "10100" + "10010" + "10001";
                case 'L': return "10000" + "10000" + "10000" + "10000" + "10000" + "10000" + "11111";
                case 'M': return "10001" + "11011" + "10101" + "10101" + "10001" + "10001" + "10001";
                case 'N': return "10001" + "11001" + "10101" + "10011" + "10001" + "10001" + "10001";
                case 'O': return "01110" + "10001" + "10001" + "10001" + "10001" + "10001" + "01110";
                case 'P': return "11110" + "10001" + "10001" + "11110" + "10000" + "10000" + "10000";
                case 'Q': return "01110" + "10001" + "10001" + "10001" + "10101" + "10010" + "01101";
                case 'R': return "11110" + "10001" + "10001" + "11110" + "10100" + "10010" + "10001";
                case 'S': return "01111" + "10000" + "10000" + "01110" + "00001" + "00001" + "11110";
                case 'T': return "11111" + "00100" + "00100" + "00100" + "00100" + "00100" + "00100";
                case 'U': return "10001" + "10001" + "10001" + "10001" + "10001" + "10001" + "01110";
                case 'V': return "10001" + "10001" + "10001" + "10001" + "10001" + "01010" + "00100";
                case 'W': return "10001" + "10001" + "10001" + "10101" + "10101" + "11011" + "10001";
                case 'X': return "10001" + "10001" + "01010" + "00100" + "01010" + "10001" + "10001";
                case 'Y': return "10001" + "10001" + "01010" + "00100" + "00100" + "00100" + "00100";
                case 'Z': return "11111" + "00001" + "00010" + "00100" + "01000" + "10000" + "11111";
                default: return null;
            }
        }

        private static Texture2D Resize(Texture2D source, int width, int height)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var output = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                output.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                output.Apply();
                return output;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(output);
                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static Color AlphaBlend(Color existing, Color overlay)
        {
            float alpha = Mathf.Clamp01(overlay.a);
            float inv = 1f - alpha;
            return new Color(
                overlay.r * alpha + existing.r * inv,
                overlay.g * alpha + existing.g * inv,
                overlay.b * alpha + existing.b * inv,
                Mathf.Max(existing.a, overlay.a));
        }

        private static string JsonString(string value)
        {
            if (value == null)
                return "null";
            return "\"" + PiAbilityJson.Escape(value) + "\"";
        }

        private static string F(float value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

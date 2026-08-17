using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Pi.UnityHarness.Runtime.Capabilities.Vision
{
    /// <summary>
    /// 多帧观察 / 动作后连拍的运行时捕获结果（playtest-loop-borrow-plan Phase 1）。
    /// Thumbs 保留 480x270 缩略图供 Editor 侧做差异判定、指纹与时间序列合成；
    /// FramePaths 是写盘去重后的唯一路径列表。
    /// TimedOut/Cancelled 区分「EOF 未在单帧超时内触发」与「Editor 侧主动取消」。
    /// </summary>
    public sealed class HarnessVisionPlaytestResult
    {
        public bool Success;
        public string Error;
        public bool TimedOut;
        public bool Cancelled;
        public List<string> FramePaths = new List<string>();
        public List<Texture2D> Thumbs = new List<Texture2D>();
        public int CapturedCount;
        public int DeduplicatedCount;
        public int CapturedWidth;
        public int CapturedHeight;
        public List<int> TimingsMs = new List<int>();
    }

    public sealed class HarnessVisionPlaytestRequest
    {
        public bool IsDone;
        public bool CancelRequested;
        public HarnessVisionPlaytestResult Result;
    }

    /// <summary>
    /// Runtime MonoBehaviour：在 player loop 上逐帧捕获。呈现信号用
    /// Camera.onPostRender / RenderPipelineManager.endCameraRendering（GameView 相机
    /// 真正渲染过才置位），配合 yield null 轮询实现单帧硬超时——GameView 隐藏或
    /// 最小化时相机不渲染，信号不触发，超时生效，而不是永久挂起。
    /// 间隔等待用手写 realtime 循环（timeScale=0 的暂停态也必须能推进）。
    /// </summary>
    public sealed class HarnessVisionPlaytestRunner : MonoBehaviour
    {
        public const int ThumbWidth = 480;
        public const int ThumbHeight = 270;
        public const int AfterThumbWidth = 160;
        public const int AfterThumbHeight = 90;
        public const float DefaultPerFrameTimeoutMs = 500f;

        private static HarnessVisionPlaytestRunner s_instance;
        private static bool s_busy;

        private bool _framePresented;

        private void OnEnable()
        {
            Camera.onPostRender += OnAnyPostRender;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private void OnDisable()
        {
            Camera.onPostRender -= OnAnyPostRender;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        }

        private void OnDestroy()
        {
            // PlayMode 退出 / 域重载禁用时释放互斥，避免后续 observe 永久 busy
            if (s_instance == this)
                s_instance = null;
            s_busy = false;
        }

        private void OnAnyPostRender(Camera camera)
        {
            // 只认 GameView 主相机的呈现（SceneView/Preview/反射相机不算）
            if (camera != null && camera.cameraType == CameraType.Game && camera.targetTexture == null)
                _framePresented = true;
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera != null && camera.cameraType == CameraType.Game && camera.targetTexture == null)
                _framePresented = true;
        }

        public static bool TryStartObserve(
            string pathPrefix,
            int frames,
            int intervalMs,
            float perFrameTimeoutMs,
            out HarnessVisionPlaytestRequest request,
            out string error)
        {
            request = null;
            error = null;
            if (!Application.isPlaying)
            {
                error = "GameView end-of-frame observe requires PlayMode.";
                return false;
            }
            if (s_busy)
            {
                error = "Another playtest observe/capture_after is already in progress.";
                return false;
            }
            if (frames <= 0)
            {
                error = "frames must be >= 1.";
                return false;
            }
            if (!EnsureInstance(out error))
                return false;

            s_busy = true;
            request = new HarnessVisionPlaytestRequest();
            s_instance.StartCoroutine(s_instance.ObserveCoroutine(
                pathPrefix, frames, intervalMs, perFrameTimeoutMs, request));
            return true;
        }

        public static bool TryStartCaptureAfter(
            string pathPrefix,
            string mode,
            float perFrameTimeoutMs,
            out HarnessVisionPlaytestRequest request,
            out string error)
        {
            request = null;
            error = null;
            if (!Application.isPlaying)
            {
                error = "GameView end-of-frame capture_after requires PlayMode.";
                return false;
            }
            if (s_busy)
            {
                error = "Another playtest observe/capture_after is already in progress.";
                return false;
            }
            if (!EnsureInstance(out error))
                return false;

            int frameCount = mode == "burst" ? 24 : 8;
            float intervalMs = mode == "burst" ? 80f : 120f;
            s_busy = true;
            request = new HarnessVisionPlaytestRequest();
            s_instance.StartCoroutine(s_instance.CaptureAfterCoroutine(
                pathPrefix, frameCount, intervalMs, perFrameTimeoutMs, request));
            return true;
        }

        private static bool EnsureInstance(out string error)
        {
            error = null;
            if (s_instance != null)
                return true;

            var go = new GameObject("HarnessVisionPlaytestRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<HarnessVisionPlaytestRunner>();
            if (s_instance == null)
            {
                error = "Failed to create playtest runner.";
                return false;
            }
            return true;
        }

        private IEnumerator ObserveCoroutine(
            string pathPrefix, int frames, int intervalMs, float perFrameTimeoutMs,
            HarnessVisionPlaytestRequest request)
        {
            var result = new HarnessVisionPlaytestResult();
            IEnumerator inner = ObserveInner(
                pathPrefix, frames, intervalMs, perFrameTimeoutMs, request, result);
            bool moved = true;
            while (true)
            {
                Exception error = null;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (error != null)
                {
                    result.Success = false;
                    result.Error = error.Message;
                    break;
                }
                if (!moved)
                {
                    // 中途超时/取消不得算成功：CapturedCount > 0 但 TimedOut/Cancelled 时
                    // 仍走失败路径，由调用方读 error_type/timed_out
                    result.Success = result.CapturedCount > 0 && !result.TimedOut && !result.Cancelled;
                    break;
                }
                yield return inner.Current;
            }

            s_busy = false;
            request.Result = result;
            request.IsDone = true;
        }

        private IEnumerator ObserveInner(
            string pathPrefix, int frames, int intervalMs, float perFrameTimeoutMs,
            HarnessVisionPlaytestRequest request, HarnessVisionPlaytestResult result)
        {
            string dir = Path.GetDirectoryName(pathPrefix);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 内存像素去重（C2）：全分辨率 Color32 逐字节相等视为重复，
            // 重复帧不写盘、立刻 Destroy；只有唯一帧 EncodeToPNG。
            var previousPixels = new List<Color32[]>();
            var previousSizes = new List<(int W, int H)>();
            float started = Time.realtimeSinceStartup;

            for (int i = 0; i < frames; i++)
            {
                if (request.CancelRequested)
                {
                    result.Cancelled = true;
                    result.Error = "Observe cancelled by request timeout.";
                    yield break;
                }

                // 等待下一帧真正呈现（相机渲染回调置位），单帧超时可退出；
                // GameView 隐藏/最小化时相机不渲染 → 超时，而不是永久挂起。
                bool presented = false;
                var wait = WaitNextPresentedFrame(perFrameTimeoutMs, request);
                while (wait.MoveNext())
                {
                    if (wait.Current is bool sentinel)
                    {
                        presented = sentinel;
                        break;
                    }
                    yield return wait.Current;
                }
                if (!presented)
                {
                    result.TimedOut = true;
                    result.Error =
                        $"End-of-frame did not fire within {perFrameTimeoutMs:0}ms: un-minimize the Editor and keep the GameView visible.";
                    yield break;
                }

                Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
                if (frame == null)
                    throw new InvalidOperationException("ScreenCapture returned no texture. GameView is not ready yet.");

                Color32[] pixels = null;
                try
                {
                    result.CapturedWidth = frame.width;
                    result.CapturedHeight = frame.height;

                    // 内存像素比较：与之前所有帧逐字节相等 → 重复，不写盘
                    pixels = frame.GetPixels32();
                    bool duplicate = false;
                    for (int p = 0; p < previousPixels.Count; p++)
                    {
                        var prev = previousPixels[p];
                        var size = previousSizes[p];
                        if (size.W != frame.width || size.H != frame.height)
                            continue;
                        if (PixelsEqual(prev, pixels))
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (duplicate)
                    {
                        result.DeduplicatedCount++;
                    }
                    else
                    {
                        string path = $"{pathPrefix}_frame_{i:00}.png";
                        File.WriteAllBytes(path, frame.EncodeToPNG());
                        result.CapturedCount++;
                        result.FramePaths.Add(path);
                        previousPixels.Add(pixels);
                        previousSizes.Add((frame.width, frame.height));
                        pixels = null; // 所有权移交 previousPixels
                    }

                    // 缩略仍保留全部帧（fingerprint 用最新捕获；timeline 展示时间线）
                    result.Thumbs.Add(ResizeToThumb(frame, ThumbWidth, ThumbHeight));
                }
                finally
                {
                    Destroy(frame);
                }

                result.TimingsMs.Add(Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f));

                if (i < frames - 1 && intervalMs > 0)
                {
                    // due-time 周期语义：第 i 帧的呈现时刻 + interval，只补齐剩余
                    float due = Time.realtimeSinceStartup + Mathf.Max(0.01f, intervalMs / 1000f);
                    while (Time.realtimeSinceStartup < due)
                    {
                        if (request.CancelRequested)
                        {
                            result.Cancelled = true;
                            result.Error = "Observe cancelled by request timeout.";
                            yield break;
                        }
                        yield return null;
                    }
                }
            }
        }

        private IEnumerator CaptureAfterCoroutine(
            string pathPrefix, int frameCount, float intervalMs, float perFrameTimeoutMs,
            HarnessVisionPlaytestRequest request)
        {
            var result = new HarnessVisionPlaytestResult();
            IEnumerator inner = CaptureAfterInner(
                pathPrefix, frameCount, intervalMs, perFrameTimeoutMs, request, result);
            bool moved = true;
            while (true)
            {
                Exception error = null;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (error != null)
                {
                    result.Success = false;
                    result.Error = error.Message;
                    break;
                }
                if (!moved)
                {
                    result.Success = result.CapturedCount > 0 && !result.TimedOut && !result.Cancelled;
                    break;
                }
                yield return inner.Current;
            }

            s_busy = false;
            request.Result = result;
            request.IsDone = true;
        }

        private IEnumerator CaptureAfterInner(
            string pathPrefix, int frameCount, float intervalMs, float perFrameTimeoutMs,
            HarnessVisionPlaytestRequest request, HarnessVisionPlaytestResult result)
        {
            string dir = Path.GetDirectoryName(pathPrefix);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            float started = Time.realtimeSinceStartup;
            for (int i = 0; i < frameCount; i++)
            {
                if (request.CancelRequested)
                {
                    result.Cancelled = true;
                    result.Error = "CaptureAfter cancelled by request timeout.";
                    yield break;
                }

                bool presented = false;
                var wait = WaitNextPresentedFrame(perFrameTimeoutMs, request);
                while (wait.MoveNext())
                {
                    if (wait.Current is bool sentinel)
                    {
                        presented = sentinel;
                        break;
                    }
                    yield return wait.Current;
                }
                if (!presented)
                {
                    result.TimedOut = true;
                    result.Error =
                        $"End-of-frame did not fire within {perFrameTimeoutMs:0}ms: un-minimize the Editor and keep the GameView visible.";
                    yield break;
                }

                Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
                if (frame == null)
                    throw new InvalidOperationException("ScreenCapture returned no texture. GameView is not ready yet.");

                try
                {
                    result.CapturedWidth = frame.width;
                    result.CapturedHeight = frame.height;
                    string path = $"{pathPrefix}_after_{i:00}.png";
                    File.WriteAllBytes(path, frame.EncodeToPNG());
                    result.CapturedCount++;
                    result.FramePaths.Add(path);
                    // burst 内存只留 160x90 比较缓冲（文档：避免 24×全图常驻）
                    result.Thumbs.Add(ResizeToThumb(frame, AfterThumbWidth, AfterThumbHeight));
                }
                finally
                {
                    Destroy(frame);
                }

                result.TimingsMs.Add(Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f));

                if (i < frameCount - 1 && intervalMs > 0f)
                {
                    float due = Time.realtimeSinceStartup + Mathf.Max(0.01f, intervalMs / 1000f);
                    while (Time.realtimeSinceStartup < due)
                    {
                        if (request.CancelRequested)
                        {
                            result.Cancelled = true;
                            result.Error = "CaptureAfter cancelled by request timeout.";
                            yield break;
                        }
                        yield return null;
                    }
                }
            }
        }

        /// <summary>
        /// 等待下一帧真正呈现（Camera.onPostRender / endCameraRendering 回调置位）
        /// 或超时。yield 给调用方的哨兵 bool 表示是否拿到了新帧（哨兵本身不
        /// 交给 Unity 调度，避免多等一帧）。GameView 隐藏/最小化时相机不渲染，
        /// 信号不触发 → 单帧超时生效，与 WaitForEndOfFrame 卡死行为不同。
        /// </summary>
        private IEnumerator WaitNextPresentedFrame(float timeoutMs, HarnessVisionPlaytestRequest request)
        {
            float timeout = Mathf.Max(0.02f, timeoutMs / 1000f);
            float t0 = Time.realtimeSinceStartup;
            _framePresented = false;
            while (!_framePresented)
            {
                if (request != null && request.CancelRequested)
                {
                    yield return false;
                    yield break;
                }
                if (Time.realtimeSinceStartup - t0 >= timeout)
                {
                    yield return false;
                    yield break;
                }
                yield return null;
            }
            yield return true;
        }

        private static Texture2D ResizeToThumb(Texture2D source, int width, int height)
        {
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(
                width, height, 0, RenderTextureFormat.ARGB32);
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
                Destroy(output);
                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static bool PixelsEqual(Color32[] a, Color32[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
                    return false;
            }
            return true;
        }
    }
}

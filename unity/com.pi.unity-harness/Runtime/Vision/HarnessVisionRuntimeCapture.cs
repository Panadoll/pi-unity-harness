using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace Pi.UnityHarness.Runtime.Capabilities.Vision
{
    public sealed class HarnessVisionRuntimeCaptureResult
    {
        public bool Success;
        public string Error;
        public int CapturedWidth;
        public int CapturedHeight;
        public int SourceWidth;
        public int SourceHeight;
    }

    public sealed class HarnessVisionRuntimeCaptureRequest
    {
        public bool IsDone;
        public HarnessVisionRuntimeCaptureResult Result;
    }

    public sealed class HarnessVisionRuntimeCaptureRunner : MonoBehaviour
    {
        private static HarnessVisionRuntimeCaptureRunner s_instance;

        public static bool TryCaptureGameViewEndOfFrame(
            string path,
            int width,
            int height,
            out HarnessVisionRuntimeCaptureRequest request,
            out string error)
        {
            request = null;
            error = null;

            if (!Application.isPlaying)
            {
                error = "GameView end-of-frame capture requires PlayMode.";
                return false;
            }

            EnsureInstance();
            if (s_instance == null)
            {
                error = "Failed to create runtime capture runner.";
                return false;
            }

            request = new HarnessVisionRuntimeCaptureRequest();
            s_instance.StartCoroutine(s_instance.CaptureCoroutine(path, width, height, request));
            return true;
        }

        private static void EnsureInstance()
        {
            if (s_instance != null) return;

            var go = new GameObject("HarnessVisionRuntimeCaptureRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<HarnessVisionRuntimeCaptureRunner>();
        }

        private IEnumerator CaptureCoroutine(string path, int width, int height, HarnessVisionRuntimeCaptureRequest request)
        {
            var result = new HarnessVisionRuntimeCaptureResult();

            // Capture after camera, post-processing, and overlay UI have presented this frame.
            yield return new WaitForEndOfFrame();

            Texture2D screenshot = null;
            try
            {
                screenshot = ScreenCapture.CaptureScreenshotAsTexture();
                if (screenshot == null)
                    throw new InvalidOperationException("ScreenCapture returned no texture. GameView is not ready yet.");

                result.SourceWidth = screenshot.width;
                result.SourceHeight = screenshot.height;

                ResolveOutputSize(screenshot, width, height, out int outputWidth, out int outputHeight);

                Texture2D output = screenshot;
                if (screenshot.width != outputWidth || screenshot.height != outputHeight)
                    output = ResizeTexture(screenshot, outputWidth, outputHeight);

                try
                {
                    File.WriteAllBytes(path, output.EncodeToPNG());
                    result.CapturedWidth = output.width;
                    result.CapturedHeight = output.height;
                    result.Success = true;
                }
                finally
                {
                    if (output != screenshot)
                        Destroy(output);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            finally
            {
                if (screenshot != null)
                    Destroy(screenshot);

                request.Result = result;
                request.IsDone = true;
            }
        }

        private static void ResolveOutputSize(Texture2D source, int requestedWidth, int requestedHeight,
            out int width, out int height)
        {
            if (requestedWidth <= 0 && requestedHeight <= 0)
            {
                width = source.width;
                height = source.height;
                return;
            }

            if (requestedWidth > 0 && requestedHeight > 0)
            {
                width = Mathf.Clamp(requestedWidth, 64, 7680);
                height = Mathf.Clamp(requestedHeight, 64, 4320);
                return;
            }

            if (requestedWidth > 0)
            {
                width = Mathf.Clamp(requestedWidth, 64, 7680);
                height = Mathf.Clamp(Mathf.RoundToInt(width * (float)source.height / source.width), 64, 4320);
                return;
            }

            height = Mathf.Clamp(requestedHeight, 64, 4320);
            width = Mathf.Clamp(Mathf.RoundToInt(height * (float)source.width / source.height), 64, 7680);
        }

        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
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
                Destroy(output);
                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }
    }
}

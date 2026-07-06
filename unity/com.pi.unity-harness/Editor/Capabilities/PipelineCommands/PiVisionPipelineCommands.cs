#if PI_UNITY_PIPELINE
using System;
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using Pi.UnityHarness.Editor.Capabilities.Vision;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiVisionPipelineCommands
    {
        [CliCommand("vision_capture", "Capture a screenshot synchronously")]
        public static string Capture(
            [CliArg("mode", "Capture mode: auto, scene, or game")] string mode = "auto",
            [CliArg("path", "Output path")] string path = null,
            [CliArg("width", "Requested width")] int width = 0,
            [CliArg("height", "Requested height")] int height = 0)
        {
            return HarnessVision.CaptureJson(mode, path, width, height);
        }

        [CliCommand("vision_capture_async", "Capture a screenshot asynchronously")]
        public static Task<string> CaptureAsync(
            [CliArg("mode", "Capture mode: auto, scene, or game")] string mode = "auto",
            [CliArg("path", "Output path")] string path = null,
            [CliArg("width", "Requested width")] int width = 0,
            [CliArg("height", "Requested height")] int height = 0,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 60000)
        {
            return PiAbilityCoroutine.ToTask(HarnessVision.CaptureJsonAsync(mode, path, width, height), "vision_capture_async", timeoutMs);
        }

        [CliCommand("vision_capture_gameview", "Capture GameView asynchronously")]
        public static Task<string> CaptureGameView(
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 60000)
        {
            return PiAbilityCoroutine.ToTask(HarnessVision.CaptureGameViewAsyncJson(), "vision_capture_gameview", timeoutMs);
        }

        [CliCommand("vision_build_analysis_request", "Build a vision analysis request JSON")]
        public static string BuildAnalysisRequest(
            [CliArg("question", "Question for the analyzer")] string question,
            [CliArg("capture_json", "Capture result JSON")] string captureJson,
            [CliArg("context_json", "Optional context JSON")] string contextJson = null)
        {
            return HarnessVision.BuildAnalysisRequestJson(question, captureJson, contextJson);
        }

        [CliCommand("vision_analyze_image", "Analyze an existing image synchronously")]
        public static string AnalyzeImage(
            [CliArg("question", "Question for the analyzer")] string question,
            [CliArg("image_path", "Image path")] string imagePath,
            [CliArg("image_meta_json", "Optional image metadata JSON")] string imageMetaJson = null,
            [CliArg("context_json", "Optional context JSON")] string contextJson = null)
        {
            return HarnessVision.AnalyzeImageJson(question, imagePath, imageMetaJson, contextJson);
        }

        [CliCommand("vision_analyze_image_async", "Analyze an existing image asynchronously")]
        public static Task<string> AnalyzeImageAsync(
            [CliArg("question", "Question for the analyzer")] string question,
            [CliArg("image_path", "Image path")] string imagePath,
            [CliArg("image_meta_json", "Optional image metadata JSON")] string imageMetaJson = null,
            [CliArg("context_json", "Optional context JSON")] string contextJson = null,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 60000)
        {
            return PiAbilityCoroutine.ToTask(HarnessVision.AnalyzeImageAsyncJson(question, imagePath, imageMetaJson, contextJson), "vision_analyze_image_async", timeoutMs);
        }

        [CliCommand("vision_capture_and_analyze", "Capture and analyze synchronously")]
        public static string CaptureAndAnalyze(
            [CliArg("question", "Question for the analyzer")] string question,
            [CliArg("mode", "Capture mode: auto, scene, or game")] string mode = "auto",
            [CliArg("path", "Output path")] string path = null,
            [CliArg("width", "Requested width")] int width = 0,
            [CliArg("height", "Requested height")] int height = 0,
            [CliArg("context_json", "Optional context JSON")] string contextJson = null)
        {
            return HarnessVision.CaptureAndAnalyzeJson(question, mode, path, width, height, contextJson);
        }

        [CliCommand("vision_capture_and_analyze_async", "Capture and analyze asynchronously")]
        public static Task<string> CaptureAndAnalyzeAsync(
            [CliArg("question", "Question for the analyzer")] string question,
            [CliArg("mode", "Capture mode: auto, scene, or game")] string mode = "auto",
            [CliArg("path", "Output path")] string path = null,
            [CliArg("width", "Requested width")] int width = 0,
            [CliArg("height", "Requested height")] int height = 0,
            [CliArg("context_json", "Optional context JSON")] string contextJson = null,
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 60000)
        {
            return PiAbilityCoroutine.ToTask(HarnessVision.CaptureAndAnalyzeAsyncJson(question, mode, path, width, height, contextJson), "vision_capture_and_analyze_async", timeoutMs);
        }

        [CliCommand("vision_settings", "Get vision analyzer settings")]
        public static string Settings()
        {
            return HarnessVision.GetVisionSettingsJson();
        }

        [CliCommand("vision_test_provider", "Test configured vision provider")]
        public static Task<string> TestProvider(
            [CliArg("timeout_ms", "Timeout in milliseconds")] int timeoutMs = 60000)
        {
            return PiAbilityCoroutine.ToTask(HarnessVision.TestVisionProviderJson(), "vision_test_provider", timeoutMs);
        }

        [CliCommand("vision_capture_camera", "Capture screenshot from a specific Camera by path or name")]
        public static string CaptureCamera(
            [CliArg("camera_path", "Camera GameObject path or name")] string cameraPath = null,
            [CliArg("path", "Output image path")] string path = null,
            [CliArg("width", "Width")] int width = 0,
            [CliArg("height", "Height")] int height = 0)
        {
            var cameraObject = PiMcpPipelineSupport.ResolveGameObject(path: cameraPath, required: false);
            var camera = cameraObject != null ? cameraObject.GetComponent<Camera>() : Camera.main;
            if (camera == null)
                throw new ArgumentException("Camera not found.");
            path = PiMcpPipelineSupport.CaptureCamera(camera, path, width, height);
            return PiMcpPipelineSupport.Ok(new { path, camera = PiMcpPipelineSupport.ToGameObjectData(camera.gameObject) });
        }

        [CliCommand("vision_capture_isolated", "Select GameObject and capture SceneView screenshot")]
        public static string CaptureIsolated(
            [CliArg("instance_id", "GameObject instance id")] long instanceId = 0,
            [CliArg("path", "Output image path")] string path = null,
            [CliArg("width", "Width")] int width = 0,
            [CliArg("height", "Height")] int height = 0)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(instanceId, required: true);
            Selection.activeGameObject = go;
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
                sceneView.FrameSelected();
            if (sceneView == null || sceneView.camera == null)
                throw new InvalidOperationException("No active SceneView camera.");
            path = PiMcpPipelineSupport.CaptureCamera(sceneView.camera, path, width, height);
            return PiMcpPipelineSupport.Ok(new { path, mode = "sceneview", gameObject = PiMcpPipelineSupport.ToGameObjectData(go) });
        }
    }
}
#endif

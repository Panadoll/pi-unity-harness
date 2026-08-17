using System.Collections;
using System.IO;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Vision;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Pi.UnityHarness.PlayMode.Tests.Vision
{
    /// <summary>
    /// playtest-loop-borrow-plan Phase 1 PlayMode 契约：
    /// vision_observe 3 帧捕获（changed 在动画/静止场景上的真假）、
    /// vision_capture_after short/burst 连拍序列图、timeScale=0 下仍可观察。
    /// 对应源循环 _capture_frames / _capture_after_action。
    /// </summary>
    public sealed class PlaytestObservePlayModeTests
    {
        private const string OutputDir = "Temp/Harness/playtest-tests";

        private Scene _previousScene;
        private Scene _testScene;
        private GameObject _cameraObject;
        private GameObject _cubeObject;
        private bool _animateCube;

        [SetUp]
        public void SetUp()
        {
            _previousScene = SceneManager.GetActiveScene();
            _testScene = SceneManager.CreateScene("PlaytestObserveScene_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            SceneManager.SetActiveScene(_testScene);

            _cameraObject = new GameObject("PlaytestObserveCamera", typeof(Camera));
            _cameraObject.tag = "MainCamera";
            var camera = _cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.08f, 1f);
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.transform.LookAt(Vector3.zero);

            _cubeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cubeObject.name = "PlaytestObserveCube";
            _cubeObject.transform.position = Vector3.zero;
        }

        [TearDown]
        public void TearDown()
        {
            _animateCube = false;
            if (_cubeObject != null) Object.Destroy(_cubeObject);
            if (_cameraObject != null) Object.Destroy(_cameraObject);
            if (_testScene.IsValid())
                SceneManager.UnloadSceneAsync(_testScene);
            SceneManager.SetActiveScene(_previousScene);

            string root = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName, OutputDir);
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }

        [UnityTest]
        public IEnumerator Observe_AnimatedScene_ChangedTrueWithFingerprint()
        {
            _animateCube = true;
            yield return null;

            var observe = PlaytestVision.ObserveJsonAsync(
                mode: "game", frames: 3, intervalMs: 60, overlay: "none",
                pathPrefix: OutputDir + "/animated_step_001");
            string json = null;
            while (observe.MoveNext())
            {
                // 确定性旋转：每帧固定 6 度，不依赖 deltaTime（首帧可能为 0）
                if (_animateCube && _cubeObject != null)
                    _cubeObject.transform.Rotate(0f, 6f, 0f, Space.World);
                yield return observe.Current;
            }
            json = observe.Current as string;

            Assert.That(json, Is.Not.Null);
            StringAssert.Contains("\"status\":\"succeeded\"", json);
            StringAssert.Contains("\"schema\":\"harness.vision.observe.v1\"", json);
            StringAssert.Contains("\"changed\":true", json, "旋转 cube 的连续帧必须检测到变化");
            StringAssert.Contains("\"captured_count\":3", json);
            StringAssert.Contains("\"fingerprint\":\"", json);

            // 时间序列图只应在画面变化时产出
            Assert.That(json, Does.Contain("\"timeline\":\""),
                "动画场景应产出 timeline 序列图");
        }

        [UnityTest]
        public IEnumerator Observe_StaticScene_ChangedFalseWithDeduplication()
        {
            _animateCube = false;
            yield return null;

            var observe = PlaytestVision.ObserveJsonAsync(
                mode: "game", frames: 3, intervalMs: 60, overlay: "none",
                pathPrefix: OutputDir + "/static_step_001");
            string json = null;
            while (observe.MoveNext())
                yield return observe.Current;
            json = observe.Current as string;

            Assert.That(json, Is.Not.Null);
            StringAssert.Contains("\"status\":\"succeeded\"", json);
            StringAssert.Contains("\"changed\":false", json, "静止场景的连续帧不应判定为变化");
            StringAssert.Contains("\"timeline\":null", json);
            // 精确去重：静止纯色场景 3 帧字节相同，去重后只保留 1 个唯一文件
            StringAssert.Contains("\"unique_count\":1", json);
        }

        [UnityTest]
        public IEnumerator Observe_TimeScaleZero_StillWorks()
        {
            // timeScale=0（暂停态）时观察必须仍可用：间隔用 realtime，不依赖 WaitForSeconds
            _animateCube = false;
            yield return null;
            Time.timeScale = 0f;
            try
            {
                var observe = PlaytestVision.ObserveJsonAsync(
                    mode: "game", frames: 3, intervalMs: 40, overlay: "none",
                    pathPrefix: OutputDir + "/timescale_step_001");
                string json = null;
                while (observe.MoveNext())
                    yield return observe.Current;
                json = observe.Current as string;

                Assert.That(json, Is.Not.Null);
                StringAssert.Contains("\"status\":\"succeeded\"", json);
                StringAssert.Contains("\"changed\":false", json);
                StringAssert.Contains("\"timed_out\":false", json);
            }
            finally
            {
                Time.timeScale = 1f;
            }
        }

        [UnityTest]
        public IEnumerator CaptureAfter_Short_ReturnsSequenceSheet()
        {
            yield return null;

            var after = PlaytestVision.CaptureAfterJsonAsync("short", OutputDir + "/after_short");
            string json = null;
            while (after.MoveNext())
                yield return after.Current;
            json = after.Current as string;

            Assert.That(json, Is.Not.Null);
            StringAssert.Contains("\"status\":\"succeeded\"", json);
            StringAssert.Contains("\"schema\":\"harness.vision.capture_after.v1\"", json);
            StringAssert.Contains("\"mode\":\"short\"", json);
            StringAssert.Contains("\"sheet\":\"", json);
            StringAssert.Contains("\"timings_ms\":[", json);
        }

        [UnityTest]
        public IEnumerator CaptureAfter_Burst_ReturnsSequenceSheet()
        {
            yield return null;

            var after = PlaytestVision.CaptureAfterJsonAsync("burst", OutputDir + "/after_burst");
            string json = null;
            while (after.MoveNext())
                yield return after.Current;
            json = after.Current as string;

            Assert.That(json, Is.Not.Null);
            StringAssert.Contains("\"status\":\"succeeded\"", json);
            StringAssert.Contains("\"mode\":\"burst\"", json);
            StringAssert.Contains("\"captured_count\":24", json);
            StringAssert.Contains("\"sheet\":\"", json);
        }
    }
}

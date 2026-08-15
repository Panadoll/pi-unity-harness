using System.Collections;
using System.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Pi.UnityHarness.PlayMode.Tests.Vision
{
    /// <summary>
    /// 对照 unity-cli-loop RaycastGridAnnotator：截图标注中 3D collider 只标注
    /// 可被点击到达的目标——被 EventSystem 优先命中的 UI 覆盖的物理目标不标注。
    /// </summary>
    public sealed class OcclusionAnnotationTests
    {
        private GameObject _cameraObject;
        private GameObject _cubeObject;
        private GameObject _canvasObject;
        private GameObject _eventSystemObject;
        private GameObject _overlayImage;

        [TearDown]
        public void TearDown()
        {
            if (_overlayImage != null) Object.Destroy(_overlayImage);
            if (_canvasObject != null) Object.Destroy(_canvasObject);
            if (_eventSystemObject != null) Object.Destroy(_eventSystemObject);
            if (_cubeObject != null) Object.Destroy(_cubeObject);
            if (_cameraObject != null) Object.Destroy(_cameraObject);
        }

        [UnityTest]
        public IEnumerator Annotate_UiOverlayOccludesCube_PhysicsSetDoesNotContainOccludedCube()
        {
            SetupCameraAndCube();
            SetupCanvasAndEventSystem();
            // 覆盖在屏幕中心的大块 UI，完全遮住 cube 的投影
            _overlayImage = CreateFullscreenOverlay("OccludingPanel");

            yield return null;
            Physics.SyncTransforms();

            // 只收集物理标注（不收集 UI），网格细密保证命中 cube 投影区域
            var set = PiVisionAnnotator.Collect(
                gridColumns: 9,
                gridRows: 9,
                maxDistance: 1000f,
                includeUi: false,
                includePhysics: true);

            // 全屏 UI 覆盖时：所有命中 cube 的网格点都被 UI 优先命中而跳过，
            // 因此标注集中不得出现被遮挡的 collider（集合可以为空）
            Assert.That(
                set.Physics.Any(p => p.Name == "OccludedCube"),
                Is.False,
                "被 UI 完全覆盖的 collider 不应出现在标注集中。实际标注: " +
                string.Join(", ", set.Physics.Select(p => p.Name)));
        }

        [UnityTest]
        public IEnumerator Annotate_WithoutUi_CubeIsAnnotated()
        {
            SetupCameraAndCube();

            yield return null;
            Physics.SyncTransforms();

            var set = PiVisionAnnotator.Collect(
                gridColumns: 9,
                gridRows: 9,
                maxDistance: 1000f,
                includeUi: false,
                includePhysics: true);

            Assert.That(
                set.Physics.Any(p => p.Name == "OccludedCube"),
                Is.True,
                "无 UI 时物理物体应正常标注。实际标注: " +
                string.Join(", ", set.Physics.Select(p => p.Name)));
        }

        [UnityTest]
        public IEnumerator Annotate_PartialOverlay_UncoveredPartStillAnnotated()
        {
            SetupCameraAndCube();
            SetupCanvasAndEventSystem();
            // 只覆盖屏幕左侧 40%：cube 在中心（50%），左侧被遮、右侧露出
            _overlayImage = CreateLeftFortyOverlay("LeftHalfPanel");

            yield return null;
            Physics.SyncTransforms();

            var set = PiVisionAnnotator.Collect(
                gridColumns: 9,
                gridRows: 9,
                maxDistance: 1000f,
                includeUi: false,
                includePhysics: true);

            Assert.That(set.Physics, Is.Not.Empty, "未覆盖部分应仍有物理标注");
            // cube 中心在屏幕中心（右侧），仍应可点击
            Assert.That(
                set.Physics.Any(p => p.Name == "OccludedCube"),
                Is.True,
                "部分覆盖时未遮挡部分应仍可点击。实际标注: " +
                string.Join(", ", set.Physics.Select(p => p.Name)));
        }

        private void SetupCameraAndCube()
        {
            _cameraObject = new GameObject("OcclusionTestCamera", typeof(Camera));
            _cameraObject.tag = "MainCamera";
            _cameraObject.transform.position = new Vector3(0f, 0f, -10f);
            _cameraObject.transform.LookAt(Vector3.zero);

            _cubeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cubeObject.name = "OccludedCube";
            _cubeObject.transform.position = Vector3.zero;
            // 相机看向原点，cube 在正前方：屏幕中心投影
        }

        private void SetupCanvasAndEventSystem()
        {
            _canvasObject = new GameObject("OcclusionTestCanvas", typeof(Canvas), typeof(GraphicRaycaster));
            _canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            _eventSystemObject = new GameObject("OcclusionTestEventSystem", typeof(EventSystem));
        }

        private GameObject CreateFullscreenOverlay(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_canvasObject.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            go.GetComponent<Image>().raycastTarget = true;
            return go;
        }

        private GameObject CreateLeftFortyOverlay(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_canvasObject.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0.4f, 1f);
            rect.sizeDelta = Vector2.zero;
            go.GetComponent<Image>().raycastTarget = true;
            return go;
        }
    }
}

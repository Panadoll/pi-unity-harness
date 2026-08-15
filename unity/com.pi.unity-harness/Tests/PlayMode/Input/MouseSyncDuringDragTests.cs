using System.Collections;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Input;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Pi.UnityHarness.PlayMode.Tests.Input
{
    /// <summary>
    /// 对照 unity-cli-loop SimulateMouseUiInputSystemTests：UI 拖拽过程中，
    /// EventSystem 输入模块读到的鼠标坐标必须与模拟坐标一致，跨多帧不漂移。
    /// 上游修复点：拖拽期间同步 Mouse.current.position（QueueDeltaStateEvent +
    /// 显式 update）；本测试断言 InputSystem 设备读取路径的坐标。
    /// </summary>
    public sealed class MouseSyncDuringDragTests
    {
        private GameObject _canvasObject;
        private GameObject _eventSystemObject;
        private GameObject _cameraObject;
        private GameObject _dragObject;

        [TearDown]
        public void TearDown()
        {
            if (_canvasObject != null) Object.Destroy(_canvasObject);
            if (_eventSystemObject != null) Object.Destroy(_eventSystemObject);
            if (_cameraObject != null) Object.Destroy(_cameraObject);
            if (_dragObject != null) Object.Destroy(_dragObject);
            HarnessInput.ClearAllInputJson();
        }

        [UnityTest]
        public IEnumerator DragMove_EventSystemMousePosition_StaysInSyncAcrossFrames()
        {
            var dragTarget = CreateDragTarget();
            var handler = dragTarget.GetComponent<DragProbe>();

            yield return null;

            Vector2 center = ScreenCenter();
            // 起点在拖拽目标中心偏左，终点偏右，多步移动
            float startX = center.x - 60f;
            float startY = center.y;
            float endX = center.x + 60f;

            // 启动拖拽（beginDrag 在首次 move 时触发，此处只按下）
            string start = null;
            yield return RunToLastString(HarnessInput.DragStartJson(startX, startY, "left"), v => start = v);
            Assert.That(start, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(handler.BeginDragCount, Is.EqualTo(0), "按下后不应立即 beginDrag");

            // 分 4 步移动到终点，每步断言 Mouse.current 位置与模拟坐标一致
            int steps = 4;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float expectedX = startX + (endX - startX) * t;
                float expectedInputY = startY;

                string move = null;
                yield return RunToLastString(HarnessInput.DragMoveJson(expectedX, expectedInputY, 1), v => move = v);
                Assert.That(move, Does.Contain("\"action\":\"drag_move\""));
                Assert.That(handler.DragCount, Is.GreaterThan(0), "第 " + i + " 步应派发 drag");

                // 跨帧后断言 InputSystem 鼠标位置（屏幕坐标，y 为 bottom-left）
                yield return null;
                var mouse = Mouse.current;
                Assert.That(mouse, Is.Not.Null, "InputSystem 鼠标设备应存在");
                Vector2 current = mouse.position.ReadValue();
                Assert.That(current.x, Is.EqualTo(expectedX).Within(1.0f),
                    "第 " + i + " 步鼠标 X 应与模拟坐标一致（不漂移）");
                float expectedScreenY = Screen.height - expectedInputY;
                Assert.That(current.y, Is.EqualTo(expectedScreenY).Within(1.0f),
                    "第 " + i + " 步鼠标 Y 应与模拟坐标一致（不漂移）");
            }

            // 结束拖拽
            string end = null;
            yield return RunToLastString(HarnessInput.DragEndJson(endX, startY, 1), v => end = v);
            Assert.That(end, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(handler.EndDragCount, Is.GreaterThan(0), "应已结束拖拽");
        }

        [UnityTest]
        public IEnumerator DragMove_MousePosition_MatchesEachStep_NotOnlyStartAndEnd()
        {
            var dragTarget = CreateDragTarget();
            yield return null;

            Vector2 center = ScreenCenter();
            float startX = center.x - 80f;
            float endX = center.x + 80f;

            yield return RunToLastString(HarnessInput.DragStartJson(startX, center.y, "left"), v => { });

            // 用 8 步验证中间点不漂移（只查 X，Y 恒定）
            int steps = 8;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float expectedX = startX + (endX - startX) * t;
                yield return RunToLastString(HarnessInput.DragMoveJson(expectedX, center.y, 1), v => { });
                yield return null;

                var mouse = Mouse.current;
                Vector2 current = mouse.position.ReadValue();
                Assert.That(current.x, Is.EqualTo(expectedX).Within(1.0f),
                    "中间步 " + i + " 鼠标 X 不应漂移");
            }

            yield return RunToLastString(HarnessInput.DragEndJson(endX, center.y, 1), v => { });
        }

        private GameObject CreateDragTarget()
        {
            SetupCanvasAndEventSystem();
            _dragObject = new GameObject("MouseSyncDragTarget", typeof(RectTransform), typeof(Image));
            _dragObject.transform.SetParent(_canvasObject.transform, false);

            var rect = _dragObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(240f, 120f);
            _dragObject.GetComponent<Image>().raycastTarget = true;
            _dragObject.AddComponent<DragProbe>();
            return _dragObject;
        }

        private void SetupCanvasAndEventSystem()
        {
            if (_canvasObject == null)
            {
                _canvasObject = new GameObject("MouseSyncCanvas", typeof(Canvas), typeof(GraphicRaycaster));
                _canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            }

            if (_eventSystemObject == null)
                _eventSystemObject = new GameObject("MouseSyncEventSystem", typeof(EventSystem));
        }

        private static Vector2 ScreenCenter()
        {
            float width = Screen.width > 0 ? Screen.width : 800f;
            float height = Screen.height > 0 ? Screen.height : 600f;
            return new Vector2(width / 2f, height / 2f);
        }

        private static IEnumerator RunToLastString(IEnumerator enumerator, System.Action<string> assign)
        {
            object lastResult = null;
            while (enumerator.MoveNext())
            {
                lastResult = enumerator.Current;
                yield return enumerator.Current;
            }
            assign(lastResult as string);
        }

        private sealed class DragProbe : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            public int BeginDragCount;
            public int DragCount;
            public int EndDragCount;

            public void OnBeginDrag(PointerEventData eventData) { BeginDragCount++; }
            public void OnDrag(PointerEventData eventData) { DragCount++; }
            public void OnEndDrag(PointerEventData eventData) { EndDragCount++; }
        }
    }
}

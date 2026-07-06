using System.Collections;
using Pi.UnityHarness.Editor.Capabilities.Input;
using Pi.UnityHarness.Editor.Capabilities.UiTree;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Pi.UnityHarness.PlayMode.Tests.Input
{
    public sealed class InputPlayModeClickTests
    {
        private GameObject _canvasObject;
        private GameObject _eventSystemObject;
        private GameObject _cameraObject;

        [TearDown]
        public void TearDown()
        {
            if (_canvasObject != null)
                Object.Destroy(_canvasObject);
            if (_eventSystemObject != null)
                Object.Destroy(_eventSystemObject);
            if (_cameraObject != null)
                Object.Destroy(_cameraObject);
        }

        [UnityTest]
        public IEnumerator ClickJson_ClicksUguiButton()
        {
            var buttonObject = CreateButton("HarnessInputButton", Vector2.zero, new Vector2(160f, 80f));
            bool clicked = false;
            buttonObject.GetComponent<Button>().onClick.AddListener(() => clicked = true);

            yield return null;

            string result = null;
            yield return RunToLastString(HarnessInput.ClickJson(ScreenCenter().x, ScreenCenter().y, "left"), value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"dispatch\":{\"attempted\":true,\"dispatched\":true"));
            Assert.That(clicked, Is.True);
        }

        [UnityTest]
        public IEnumerator ClickJson_UsesCanvasSpaceFallbackForClippedOverlayButton()
        {
            Vector2 center = ScreenCenter();
            var buttonObject = CreateButton("HarnessInputClippedButton", new Vector2(-center.x - 40f, 0f), new Vector2(160f, 80f));
            bool clicked = false;
            buttonObject.GetComponent<Button>().onClick.AddListener(() => clicked = true);

            yield return null;

            string result = null;
            yield return RunToLastString(HarnessInput.ClickJson(-40f, center.y, "left"), value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"dispatch\":{\"attempted\":true,\"dispatched\":true"));
            Assert.That(result, Does.Contain("HarnessInputClippedButton"));
            Assert.That(clicked, Is.True);
        }

        [UnityTest]
        public IEnumerator DescribeJson_UsesCanvasSpaceFallbackForClippedOverlayButton()
        {
            Vector2 center = ScreenCenter();
            CreateButton("HarnessInputClippedButton", new Vector2(-center.x - 40f, 0f), new Vector2(160f, 80f));

            yield return null;

            string snapshot = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "HarnessInputCanvas",
                selector: "#HarnessInputClippedButton",
                includeInvisible: true);
            string refId = ExtractTargetRef(snapshot);
            string json = HarnessUiTree.DescribeJson(refId);

            Assert.That(json, Does.Contain("\"eventsystem_top_hit_matches_target\":true"));
            Assert.That(json, Does.Contain("\"likely_clickable\":true"));
            Assert.That(json, Does.Not.Contain("\"blocked_reason\":\"no_eventsystem_hit\""));
        }

        [UnityTest]
        public IEnumerator ClickJson_UsesCanvasSpaceFallbackForClippedScreenSpaceCameraButton()
        {
            SetupScreenSpaceCameraCanvas();
            Vector2 center = ScreenCenter();
            var buttonObject = CreateButton("HarnessInputCameraClippedButton", new Vector2(-center.x - 40f, 0f), new Vector2(160f, 80f));
            bool clicked = false;
            buttonObject.GetComponent<Button>().onClick.AddListener(() => clicked = true);

            yield return null;

            string snapshot = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "HarnessInputCanvas",
                selector: "#HarnessInputCameraClippedButton",
                includeInvisible: true);
            string refId = ExtractTargetRef(snapshot);
            string diagnostics = HarnessUiTree.DescribeJson(refId);
            string result = null;
            yield return RunToLastString(HarnessInput.ClickJson(-40f, center.y, "left"), value => result = value);

            Assert.That(diagnostics, Does.Contain("\"eventsystem_top_hit_matches_target\":true"));
            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"dispatch\":{\"attempted\":true,\"dispatched\":true"));
            Assert.That(clicked, Is.True);
        }

        [UnityTest]
        public IEnumerator DoubleClickJson_ReturnsDispatchResult()
        {
            var buttonObject = CreateButton("HarnessInputDoubleButton", Vector2.zero, new Vector2(160f, 80f));
            int clicked = 0;
            buttonObject.GetComponent<Button>().onClick.AddListener(() => clicked++);

            yield return null;

            string result = null;
            yield return RunToLastString(HarnessInput.DoubleClickJson(ScreenCenter().x, ScreenCenter().y, "left"), value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"action\":\"double_click\""));
            Assert.That(result, Does.Contain("\"dispatch\":{\"attempted\":true,\"dispatched\":true"));
            Assert.That(clicked, Is.GreaterThanOrEqualTo(1));
        }

        [UnityTest]
        public IEnumerator DoubleClickJson_FailsWhenDispatchMissesHandler()
        {
            SetupCanvasAndEventSystem();
            yield return null;

            string result = null;
            yield return RunToLastString(HarnessInput.DoubleClickJson(-1000f, -1000f, "left"), value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"failed\""));
            Assert.That(result, Does.Contain("\"action\":\"double_click\""));
            Assert.That(result, Does.Contain("\"error_type\":\"runtime\""));
            Assert.That(result, Does.Contain("\"dispatch\":{\"attempted\":true,\"dispatched\":false"));
        }

        [UnityTest]
        public IEnumerator DragJson_DragsUguiHandler()
        {
            var dragObject = CreateImage("HarnessInputDragTarget", Vector2.zero, new Vector2(180f, 100f));
            var handler = dragObject.AddComponent<DragProbe>();

            yield return null;

            Vector2 center = ScreenCenter();
            string result = null;
            yield return RunToLastString(
                HarnessInput.DragJson(center.x - 40f, center.y, center.x + 40f, center.y, "left", 4),
                value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"action\":\"drag\""));
            Assert.That(result, Does.Contain("\"dispatch\":"));
            Assert.That(handler.BeginDragCount, Is.GreaterThan(0));
            Assert.That(handler.DragCount, Is.GreaterThan(0));
            Assert.That(handler.EndDragCount, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator ScrollJson_ScrollsUguiHandler()
        {
            var scrollObject = CreateImage("HarnessInputScrollTarget", Vector2.zero, new Vector2(180f, 100f));
            var handler = scrollObject.AddComponent<ScrollProbe>();

            yield return null;

            string result = null;
            yield return RunToLastString(
                HarnessInput.ScrollJson(ScreenCenter().x, ScreenCenter().y, 0f, 120f),
                value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"dispatch\":{\"attempted\":true,\"dispatched\":true"));
            Assert.That(handler.ScrollCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SplitDragJson_HoldsMovesAndEndsUguiHandler()
        {
            var dragObject = CreateImage("HarnessInputSplitDragTarget", Vector2.zero, new Vector2(220f, 140f));
            var handler = dragObject.AddComponent<DragProbe>();

            yield return null;

            Vector2 center = ScreenCenter();
            string start = null;
            yield return RunToLastString(
                HarnessInput.DragStartJson(center.x - 40f, center.y, "left"),
                value => start = value);
            Assert.That(start, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(handler.BeginDragCount, Is.EqualTo(0));

            string move = null;
            yield return RunToLastString(
                HarnessInput.DragMoveJson(center.x + 40f, center.y, 4),
                value => move = value);
            Assert.That(move, Does.Contain("\"action\":\"drag_move\""));
            Assert.That(handler.BeginDragCount, Is.GreaterThan(0));
            Assert.That(handler.DragCount, Is.GreaterThan(0));
            Assert.That(handler.EndDragCount, Is.EqualTo(0));

            string end = null;
            yield return RunToLastString(
                HarnessInput.DragEndJson(center.x + 40f, center.y, 1),
                value => end = value);
            Assert.That(end, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(end, Does.Contain("\"action\":\"drag_end\""));
            Assert.That(handler.EndDragCount, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator TypeTextJson_AppendsToSelectedInputField()
        {
            var inputObject = CreateInputField("HarnessInputTextField", Vector2.zero, new Vector2(220f, 60f));
            var input = inputObject.GetComponent<InputField>();
            yield return null;
            Assert.That(EventSystem.current, Is.Not.Null);
            EventSystem.current.SetSelectedGameObject(inputObject);
            input.ActivateInputField();

            string result = null;
            yield return RunToLastString(HarnessInput.TypeTextJson("Hi"), value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(input.text, Does.EndWith("Hi"));
        }

        [UnityTest]
        public IEnumerator KeyChordJson_SucceedsAndReleasesState()
        {
            SetupCanvasAndEventSystem();

            string result = null;
            yield return RunToLastString(HarnessInput.KeyChordJson("[\"LeftCtrl\",\"S\"]"), value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"keys\":[\"LeftCtrl\",\"S\"]"));

            string clear = HarnessInput.ClearAllInputJson();
            Assert.That(clear, Does.Contain("\"status\":\"succeeded\""));
        }

        [UnityTest]
        public IEnumerator RunSequenceJson_ClickAndType_HappyPath()
        {
            var buttonObject = CreateButton("HarnessInputSequenceButton", new Vector2(-120f, 0f), new Vector2(140f, 70f));
            bool clicked = false;
            buttonObject.GetComponent<Button>().onClick.AddListener(() => clicked = true);

            var inputObject = CreateInputField("HarnessInputSequenceField", new Vector2(120f, 0f), new Vector2(180f, 60f));
            var input = inputObject.GetComponent<InputField>();

            yield return null;

            Vector2 center = ScreenCenter();
            float clickX = center.x - 120f;
            float clickY = center.y;
            float inputX = center.x + 120f;
            string sequence = "{\"actions\":[" +
                "{\"type\":\"click\",\"x\":" + clickX.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"y\":" + clickY.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}," +
                "{\"type\":\"click\",\"x\":" + inputX.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"y\":" + clickY.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}," +
                "{\"type\":\"type_text\",\"text\":\"ok\"}" +
                "]}";

            string result = null;
            yield return RunToLastString(HarnessInput.RunSequenceJson(sequence), value => result = value);

            Assert.That(result, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(result, Does.Contain("\"executed_count\":3"));
            Assert.That(clicked, Is.True);
            Assert.That(input.text, Does.EndWith("ok"));
        }

        [UnityTest]
        public IEnumerator MultipleClicks_DoNotLeakPressedMouseState()
        {
            var buttonObject = CreateButton("HarnessInputRepeatedButton", Vector2.zero, new Vector2(160f, 80f));
            int clicked = 0;
            buttonObject.GetComponent<Button>().onClick.AddListener(() => clicked++);

            yield return null;

            Vector2 center = ScreenCenter();
            string first = null;
            yield return RunToLastString(HarnessInput.ClickJson(center.x, center.y, "left"), value => first = value);
            string second = null;
            yield return RunToLastString(HarnessInput.ClickJson(center.x, center.y, "left"), value => second = value);

            Assert.That(first, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(second, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(clicked, Is.EqualTo(2));
        }

        private GameObject CreateButton(string name, Vector2 position, Vector2 size)
        {
            var buttonObject = CreateImage(name, position, size);
            buttonObject.AddComponent<Button>();
            return buttonObject;
        }

        private GameObject CreateInputField(string name, Vector2 position, Vector2 size)
        {
            var inputObject = CreateImage(name, position, size);
            var input = inputObject.AddComponent<InputField>();

            var textObject = new GameObject(name + "Text", typeof(RectTransform), typeof(Text));
            textObject.layer = inputObject.layer;
            textObject.transform.SetParent(inputObject.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            var text = textObject.GetComponent<Text>();
            text.text = string.Empty;
            text.raycastTarget = false;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            input.textComponent = text;
            input.text = string.Empty;
            return inputObject;
        }

        private GameObject CreateImage(string name, Vector2 position, Vector2 size)
        {
            SetupCanvasAndEventSystem();
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_canvasObject.transform, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            go.GetComponent<Image>().raycastTarget = true;
            return go;
        }

        private void SetupCanvasAndEventSystem()
        {
            if (_canvasObject == null)
            {
                _canvasObject = new GameObject("HarnessInputCanvas", typeof(Canvas), typeof(GraphicRaycaster));
                _canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            }

            if (_eventSystemObject == null)
                _eventSystemObject = new GameObject("HarnessInputEventSystem", typeof(EventSystem));
        }

        private void SetupScreenSpaceCameraCanvas()
        {
            SetupCanvasAndEventSystem();
            if (_cameraObject == null)
            {
                _cameraObject = new GameObject("HarnessInputCamera", typeof(Camera));
                _cameraObject.GetComponent<Camera>().orthographic = true;
            }

            var canvas = _canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _cameraObject.GetComponent<Camera>();
            canvas.planeDistance = 10f;
        }

        private static Vector2 ScreenCenter()
        {
            float width = Screen.width > 0 ? Screen.width : 800f;
            float height = Screen.height > 0 ? Screen.height : 600f;
            return new Vector2(width / 2f, height / 2f);
        }

        private static string ExtractTargetRef(string json)
        {
            string marker = "\"target_ref\":\"";
            int start = json.IndexOf(marker, System.StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            start += marker.Length;
            int end = json.IndexOf("\"", start, System.StringComparison.Ordinal);
            return json.Substring(start, end - start);
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

        private sealed class ScrollProbe : MonoBehaviour, IScrollHandler
        {
            public int ScrollCount;

            public void OnScroll(PointerEventData eventData) { ScrollCount++; }
        }
    }
}

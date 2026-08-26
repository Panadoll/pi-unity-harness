using Pi.UnityHarness.Editor.Capabilities.UiTree;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Pi.UnityHarness.Editor.Tests.UiTree
{
    /// <summary>
    /// UGUI fixture tests. Creates temporary Canvas/Button/Text GameObjects,
    /// verifies ListRootsJson, SnapshotJson, FindJson, TextJson.
    /// </summary>
    public sealed class UiTreeUguiTests
    {
        private GameObject _canvasGo;
        private GameObject _buttonGo;
        private GameObject _inputGo;
        private GameObject _sliderGo;
        private GameObject _scrollGo;
        private GameObject _dragGo;
        private GameObject _textGo;

        [SetUp]
        public void SetUp()
        {
            _canvasGo = new GameObject("TestCanvas_UiTree");
            _canvasGo.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _canvasGo.AddComponent<CanvasScaler>();
            _canvasGo.AddComponent<GraphicRaycaster>();

            _buttonGo = new GameObject("TestButton");
            _buttonGo.transform.SetParent(_canvasGo.transform);
            var buttonRt = _buttonGo.AddComponent<RectTransform>();
            buttonRt.sizeDelta = new Vector2(200, 50);
            _buttonGo.AddComponent<Image>();
            _buttonGo.AddComponent<Button>();

            _inputGo = new GameObject("TestInputField");
            _inputGo.transform.SetParent(_canvasGo.transform);
            var inputRt = _inputGo.AddComponent<RectTransform>();
            inputRt.sizeDelta = new Vector2(240, 40);
            _inputGo.AddComponent<Image>();
            var input = _inputGo.AddComponent<InputField>();
            input.text = "initial";

            _sliderGo = new GameObject("TestSlider");
            _sliderGo.transform.SetParent(_canvasGo.transform);
            var sliderRt = _sliderGo.AddComponent<RectTransform>();
            sliderRt.sizeDelta = new Vector2(240, 30);
            var slider = _sliderGo.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 10f;
            slider.value = 4f;

            _scrollGo = new GameObject("TestScrollRect");
            _scrollGo.transform.SetParent(_canvasGo.transform);
            var scrollRt = _scrollGo.AddComponent<RectTransform>();
            scrollRt.sizeDelta = new Vector2(240, 120);
            _scrollGo.AddComponent<ScrollRect>();

            _dragGo = new GameObject("DragArea");
            _dragGo.transform.SetParent(_canvasGo.transform);
            var dragRt = _dragGo.AddComponent<RectTransform>();
            dragRt.sizeDelta = new Vector2(180, 180);
            _dragGo.AddComponent<Image>();
            _dragGo.AddComponent<TestDragHandler>();

            _textGo = new GameObject("TestText");
            _textGo.transform.SetParent(_buttonGo.transform);
            var textRt = _textGo.AddComponent<RectTransform>();
            textRt.sizeDelta = new Vector2(200, 50);
            var text = _textGo.AddComponent<Text>();
            text.text = "Click Me";
        }

        [TearDown]
        public void TearDown()
        {
            if (_canvasGo != null)
                Object.DestroyImmediate(_canvasGo);
        }

        [Test]
        public void ListRootsJson_ContainsTestCanvas()
        {
            string json = HarnessUiTree.ListRootsJson();
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("TestCanvas_UiTree"));
            Assert.That(json, Does.Contain("\"source\":\"ugui\""));
        }

        [Test]
        public void SnapshotJson_ContainsInteractiveButton()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree");
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"type\":\"Button\""));
            Assert.That(json, Does.Contain("\"interactive\":true"));
            Assert.That(json, Does.Contain("\"interaction\":\"click\""));
            Assert.That(json, Does.Contain("\"center\":{"));
            Assert.That(json, Does.Contain("\"total_count\":"));
            Assert.That(json, Does.Contain("\"matched_count\":"));
            Assert.That(json, Does.Contain("\"returned_count\":"));
            Assert.That(json, Does.Contain("\"truncated\":false"));
        }

        [Test]
        public void SnapshotJson_TruncationMetaCountsAllMatchingRoots()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree",
                limit: 1);

            Assert.That(json, Does.Contain("\"returned_count\":1"));
            Assert.That(json, Does.Contain("\"matched_count\":5"));
            Assert.That(json, Does.Contain("\"omitted_count\":4"));
            Assert.That(json, Does.Contain("\"truncated\":true"));
        }

        [Test]
        public void FindJson_TruncationMetaCountsAllMatches()
        {
            string json = HarnessUiTree.FindJson("Test", limit: 1, root: "TestCanvas_UiTree");

            Assert.That(json, Does.Contain("\"returned_count\":1"));
            Assert.That(json, Does.Contain("\"matched_count\":7"));
            Assert.That(json, Does.Contain("\"omitted_count\":6"));
            Assert.That(json, Does.Contain("\"truncated\":true"));
        }

        [Test]
        public void SnapshotJson_ButtonHasInputHint()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree");
            Assert.That(json, Does.Contain("\"input_hint\":{\"action\":\"click\""));
            Assert.That(json, Does.Contain("\"button\":\"left\""));
            Assert.That(json, Does.Contain("\"coordinate_space\":\"gameview_top_left\""));
            Assert.That(json, Does.Contain("\"source\":\"ugui\""));
            Assert.That(json, Does.Contain("\"requires_focus\":false"));
            Assert.That(json, Does.Contain("\"target_name\":\"TestButton\""));
            Assert.That(json, Does.Contain("\"target_type\":\"Button\""));
            Assert.That(json, Does.Contain("\"target_ref\":\"ref_"));
            Assert.That(json, Does.Contain("\"gameview_size\":{"));
        }

        [Test]
        public void SnapshotJson_InputFieldHintRequiresFocus()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree",
                selector: "#TestInputField");

            Assert.That(json, Does.Contain("\"type\":\"InputField\""));
            Assert.That(json, Does.Contain("\"input_hint\":{\"action\":\"type_text\""));
            Assert.That(json, Does.Contain("\"requires_focus\":true"));
            Assert.That(json, Does.Contain("\"coordinate_space\":\"gameview_top_left\""));
            Assert.That(json, Does.Contain("\"input_template\":{\"executable\":false"));
            Assert.That(json, Does.Contain("\"kind\":\"fill_text\""));
            Assert.That(json, Does.Contain("\"parameters\":[\"text\"]"));
            Assert.That(json, Does.Contain("\"text\":\"__USER_INPUT__\""));
        }

        [Test]
        public void SnapshotJson_SliderHasDragTemplate()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree",
                selector: "#TestSlider");

            Assert.That(json, Does.Contain("\"type\":\"Slider\""));
            Assert.That(json, Does.Contain("\"input_hint\":{\"action\":\"drag\""));
            Assert.That(json, Does.Contain("\"input_template\":{\"executable\":false"));
            Assert.That(json, Does.Contain("\"kind\":\"set_value\""));
            Assert.That(json, Does.Contain("\"parameters\":[\"value\"]"));
            Assert.That(json, Does.Contain("\"value_range\":{\"current\":4"));
            Assert.That(json, Does.Contain("\"min\":0"));
            Assert.That(json, Does.Contain("\"max\":10"));
            Assert.That(json, Does.Contain("\"to_x\":\"__VALUE_TO_X__\""));
        }

        [Test]
        public void SnapshotJson_CustomDragHandlerHasGameViewDragHints()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree",
                selector: "#DragArea");

            Assert.That(json, Does.Contain("\"type\":\"Image\""));
            Assert.That(json, Does.Contain("\"interaction\":\"drag\""));
            Assert.That(json, Does.Contain("\"input_hint\":{\"action\":\"drag\""));
            Assert.That(json, Does.Contain("\"coordinate_space\":\"gameview_top_left\""));
            Assert.That(json, Does.Contain("\"gameview_size\":{"));
            Assert.That(json, Does.Contain("\"handlers\":[\"IDragHandler\"]"));
            Assert.That(json, Does.Contain("\"safe_drag_up\":{"));
            Assert.That(json, Does.Contain("\"from_x\":"));
            Assert.That(json, Does.Contain("\"to_y\":"));
        }

        [Test]
        public void SnapshotJson_ScrollRectHasScrollTemplate()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree",
                selector: "#TestScrollRect");

            Assert.That(json, Does.Contain("\"type\":\"ScrollRect\""));
            Assert.That(json, Does.Contain("\"input_hint\":{\"action\":\"scroll\""));
            Assert.That(json, Does.Contain("\"input_template\":{\"executable\":false"));
            Assert.That(json, Does.Contain("\"kind\":\"scroll_delta\""));
            Assert.That(json, Does.Contain("\"parameters\":[\"delta_x\",\"delta_y\"]"));
            Assert.That(json, Does.Contain("\"delta_y\":\"__DELTA_Y__\""));
        }

        [Test]
        public void DescribeJson_UguiNodeIncludesInteractionDiagnostics()
        {
            string snapshot = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                root: "TestCanvas_UiTree",
                selector: "#TestButton");
            string refId = ExtractTargetRef(snapshot);

            string json = HarnessUiTree.DescribeJson(refId);

            Assert.That(json, Does.Contain("\"interaction_diagnostics\":{"));
            Assert.That(json, Does.Contain("\"likely_clickable\":"));
            Assert.That(json, Does.Contain("\"blocked_reason\":"));
            Assert.That(json, Does.Contain("\"event_system_present\":"));
            Assert.That(json, Does.Contain("\"probe_point\":{"));
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

        private sealed class TestDragHandler : MonoBehaviour, IDragHandler
        {
            public void OnDrag(PointerEventData eventData)
            {
            }
        }

        [Test]
        public void FindJson_FindsByName()
        {
            string json = HarnessUiTree.FindJson("TestButton");
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"name\":\"TestButton\""));
        }

        [Test]
        public void FindJson_UguiSupportsExactNameSelector()
        {
            string json = HarnessUiTree.FindJson("#TestButton", root: "TestCanvas_UiTree");
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"name\":\"TestButton\""));
        }

        [Test]
        public void FindJson_UguiDoesNotTreatClassSelectorAsName()
        {
            string json = HarnessUiTree.FindJson(".TestButton", root: "TestCanvas_UiTree");
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Not.Contain("\"name\":\"TestButton\""));
            Assert.That(json, Does.Contain("\"returned_count\":0"));
        }

        [Test]
        public void FindJson_FindsByType()
        {
            string json = HarnessUiTree.FindJson("Button");
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"type\":\"Button\""));
        }

        [Test]
        public void FindJson_FindsByText()
        {
            string json = HarnessUiTree.FindJson("Click Me");
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("Click Me"));
        }
    }
}

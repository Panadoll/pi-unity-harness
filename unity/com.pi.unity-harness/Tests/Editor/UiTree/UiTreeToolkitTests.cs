using Pi.UnityHarness.Editor.Capabilities.UiTree;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Pi.UnityHarness.Editor.Tests.UiTree
{
    /// <summary>
    /// UI Toolkit tests using a temporary EditorWindow with VisualElements.
    /// Covers classes, TextElement, DescribeJson, and element discovery.
    /// </summary>
    public sealed class UiTreeToolkitTests
    {
        private TestEditorWindow _window;

        private class TestEditorWindow : EditorWindow
        {
            public static TestEditorWindow Create()
            {
                var window = CreateInstance<TestEditorWindow>();
                window.titleContent = new GUIContent("UiTree Test Window");
                window.Show();
                return window;
            }

            public void BuildTestUI()
            {
                var root = rootVisualElement;
                root.Clear();

                var container = new VisualElement();
                container.name = "test-container";
                container.AddToClassList("main-panel");
                root.Add(container);

                var label = new Label("Hello UiTree");
                label.name = "test-label";
                label.AddToClassList("header-text");
                container.Add(label);

                var button = new Button(() => { }) { text = "Test Action" };
                button.name = "test-action-btn";
                button.AddToClassList("action-button");
                container.Add(button);

                var textField = new TextField("Input");
                textField.name = "test-input";
                textField.value = "sample text";
                container.Add(textField);

                var toggle = new Toggle("Enabled");
                toggle.name = "test-toggle";
                container.Add(toggle);
            }
        }

        [SetUp]
        public void SetUp()
        {
            _window = TestEditorWindow.Create();
            _window.BuildTestUI();
        }

        [TearDown]
        public void TearDown()
        {
            if (_window != null)
                _window.Close();
        }

        [Test]
        public void ListRootsJson_ContainsEditorPanels()
        {
            string json = HarnessUiTree.ListRootsJson();
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            // Should contain at least one UI Toolkit root (editor panels)
            Assert.That(json, Does.Contain("\"source\":\"uitoolkit\""));
        }

        [Test]
        public void SnapshotJson_ContainsUIToolkitNodes()
        {
            string json = HarnessUiTree.SnapshotJson(interactiveOnly: false, maxDepth: 3, limit: 200);
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"source\":\"uitoolkit\""));
        }

        [Test]
        public void SnapshotJson_InteractiveOnly_ContainsButtons()
        {
            string json = HarnessUiTree.SnapshotJson(interactiveOnly: true, limit: 200);
            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            // Editor always has interactive elements (buttons in toolbar, etc.)
            Assert.That(json, Does.Contain("\"interactive\":true"));
        }

        [Test]
        public void SnapshotJson_EditorPanelHintIsNotInjectable()
        {
            string json = HarnessUiTree.SnapshotJson(
                interactiveOnly: true,
                limit: 200,
                selector: "#test-action-btn");

            Assert.That(json, Does.Contain("\"status\":\"succeeded\""));
            Assert.That(json, Does.Contain("\"input_hint\":{\"action\":\"click\""));
            Assert.That(json, Does.Contain("\"input_hint\":{\"action\":\"click\",\"button\":\"left\",\"coordinate_space\":\"editor_panel_not_injectable\""));
            Assert.That(json, Does.Contain("\"coordinate_space\":\"editor_panel_not_injectable\""));
            Assert.That(json, Does.Contain("\"source\":\"uitoolkit\""));
            Assert.That(json, Does.Contain("\"target_name\":\"test-action-btn\""));
        }
    }
}

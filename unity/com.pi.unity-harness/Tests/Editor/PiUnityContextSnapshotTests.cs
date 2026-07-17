using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class PiUnityContextSnapshotTests
    {
        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PiUnityConsoleLogBuffer.Clear();
            root = new GameObject("SnapshotRoot");
            GameObject child = new GameObject("SnapshotChild");
            child.transform.SetParent(root.transform);
            GameObject grandchild = new GameObject("SnapshotGrandchild");
            grandchild.transform.SetParent(child.transform);
        }

        [TearDown]
        public void TearDown()
        {
            PiUnityConsoleLogBuffer.Clear();
            if (root != null)
                Object.DestroyImmediate(root);
        }

        [Test]
        public void BuildJson_ReturnsEditorSceneHierarchyAndErrorLogs()
        {
            Debug.Log("SnapshotInfoShouldBeFiltered");
            LogAssert.Expect(LogType.Error, "SnapshotError");
            Debug.LogError("SnapshotError");

            JObject snapshot = JObject.Parse(PiUnityContextSnapshot.BuildJson(
                maxDepth: 3,
                maxNodes: 20,
                logLimit: 10,
                logLevel: "error",
                includeComponents: true));

            Assert.AreEqual(1, snapshot.Value<int>("schemaVersion"));
            Assert.IsNotEmpty(snapshot["capturedAtUtc"].Value<string>());
            Assert.AreEqual(Application.unityVersion, snapshot["project"].Value<string>("unityVersion"));
            Assert.AreEqual(1, snapshot["scene"].Value<int>("rootCount"));
            Assert.AreEqual("SnapshotRoot", snapshot["hierarchy"]["roots"][0].Value<string>("name"));
            Assert.AreEqual("SnapshotChild", snapshot["hierarchy"]["roots"][0]["children"][0].Value<string>("name"));
            Assert.AreEqual("SnapshotGrandchild", snapshot["hierarchy"]["roots"][0]["children"][0]["children"][0].Value<string>("name"));
            CollectionAssert.Contains(snapshot["hierarchy"]["roots"][0]["components"].Values<string>(), "UnityEngine.Transform");
            Assert.AreEqual(1, snapshot["logs"].Value<int>("count"));
            Assert.AreEqual("SnapshotError", snapshot["logs"]["entries"][0].Value<string>("message"));
        }

        [Test]
        public void BuildJson_RespectsDepthAndNodeBounds()
        {
            JObject snapshot = JObject.Parse(PiUnityContextSnapshot.BuildJson(
                maxDepth: 0,
                maxNodes: 1,
                logLimit: 0,
                logLevel: "all",
                includeComponents: false));

            Assert.AreEqual(1, snapshot["hierarchy"].Value<int>("nodeCount"));
            Assert.IsTrue(snapshot["hierarchy"].Value<bool>("truncated"));
            Assert.IsNull(snapshot["hierarchy"]["roots"][0]["children"]);
            Assert.AreEqual(0, snapshot["logs"].Value<int>("count"));
        }
    }
}

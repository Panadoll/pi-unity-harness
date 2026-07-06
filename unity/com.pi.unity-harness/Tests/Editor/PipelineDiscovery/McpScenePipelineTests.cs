#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpScenePipelineTests
    {
        private const string TestFolder = "Assets/PiMcpSceneTests";
        private const string ScenePath = TestFolder + "/SceneA.unity";
        private const string AdditiveScenePath = TestFolder + "/SceneB.unity";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TestFolder))
                AssetDatabase.CreateFolder("Assets", "PiMcpSceneTests");
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void SceneCreateOpenListSetActiveAndUnload_WorkWithPaths()
        {
            var created = JObject.Parse(PiMcpPipelineCommands.SceneCreate(ScenePath, "Empty"));
            Assert.AreEqual(ScenePath, created.Value<string>("path"));
            Assert.IsTrue(created.Value<bool>("isLoaded"));

            PiMcpPipelineCommands.SceneCreate(AdditiveScenePath, "Empty");

            var opened = JObject.Parse(PiMcpPipelineCommands.SceneOpen(ScenePath));
            Assert.AreEqual(ScenePath, opened["opened"].Value<string>("path"));

            var additive = JObject.Parse(PiMcpPipelineCommands.SceneOpen(AdditiveScenePath, "Additive"));
            Assert.GreaterOrEqual(((JArray)additive["scenes"]).Count, 2);

            var active = JObject.Parse(PiMcpPipelineCommands.SceneSetActive(AdditiveScenePath));
            Assert.IsTrue(active.Value<bool>("active"));

            var listed = JArray.Parse(PiMcpPipelineCommands.SceneListOpened());
            Assert.GreaterOrEqual(listed.Count, 2);

            var unloaded = JObject.Parse(PiMcpPipelineCommands.SceneUnload(AdditiveScenePath));
            Assert.IsTrue(unloaded.Value<bool>("Unloaded"));
        }

        [Test]
        public void SceneGetDataAndSave_ReturnStructuredSceneData()
        {
            PiMcpPipelineCommands.SceneCreate(ScenePath, "Empty");

            var data = JObject.Parse(PiMcpPipelineCommands.SceneGetData(ScenePath));
            Assert.AreEqual(ScenePath, data.Value<string>("path"));

            var save = JObject.Parse(PiMcpPipelineCommands.SceneSave(ScenePath));
            Assert.IsTrue(save.Value<bool>("saved"));
            Assert.AreEqual(ScenePath, save["scene"].Value<string>("path"));
        }
    }
}
#endif

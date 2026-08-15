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
            // 重置为干净空场景，避免前序测试残留的脏场景触发 dirtyAction=abort 拒绝
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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

        [Test]
        public void SceneOpen_DirtySceneWithAbort_ReturnsErrorWithoutOpening()
        {
            // 准备待打开的场景资产
            PiMcpPipelineCommands.SceneCreate(ScenePath, "Empty");
            PiMcpPipelineCommands.SceneCreate(AdditiveScenePath, "Empty");

            // 打开 AdditiveScene 并标记为脏
            PiMcpPipelineCommands.SceneOpen(AdditiveScenePath, "Additive");
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            // 默认 abort：脏场景时 Single 打开应报错，且不替换当前场景
            var result = JObject.Parse(PiMcpPipelineCommands.SceneOpen(ScenePath));
            Assert.IsTrue(result.Value<bool>("ok") == false, "脏场景 + 默认 abort 应返回错误");
            Assert.That(result.Value<string>("error"), Does.Contain("dirtyAction"), "错误信息应提示 dirtyAction 选项");
        }

        [Test]
        public void SceneOpen_DirtySceneWithDiscard_OpensSuccessfully()
        {
            PiMcpPipelineCommands.SceneCreate(ScenePath, "Empty");
            PiMcpPipelineCommands.SceneCreate(AdditiveScenePath, "Empty");
            PiMcpPipelineCommands.SceneOpen(AdditiveScenePath, "Additive");
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            var result = JObject.Parse(PiMcpPipelineCommands.SceneOpen(ScenePath, "Single", "discard"));
            Assert.AreEqual(ScenePath, result["opened"].Value<string>("path"), "discard 策略应放行并打开目标场景");
        }

        [Test]
        public void SceneOpen_DirtyUntitledSceneWithSave_ReturnsError()
        {
            // 当前 untitled 场景标记为脏（save 无保存路径 → 应按 abort 处理报错）
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Assert.IsTrue(string.IsNullOrEmpty(UnityEngine.SceneManagement.SceneManager.GetActiveScene().path), "前置条件：场景未保存");

            PiMcpPipelineCommands.SceneCreate(ScenePath, "Empty");

            var result = JObject.Parse(PiMcpPipelineCommands.SceneOpen(ScenePath, "Single", "save"));
            Assert.IsTrue(result.Value<bool>("ok") == false, "untitled 脏场景 + save 应报错");
            Assert.That(result.Value<string>("error"), Does.Contain("从未保存").Or.Contains("untitled"), "错误应说明未保存原因");
        }

        [Test]
        public void SceneUnload_DirtySceneWithAbort_ReturnsErrorWithoutUnloading()
        {
            PiMcpPipelineCommands.SceneCreate(ScenePath, "Empty");
            PiMcpPipelineCommands.SceneCreate(AdditiveScenePath, "Empty");
            PiMcpPipelineCommands.SceneOpen(AdditiveScenePath, "Additive");
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            var result = JObject.Parse(PiMcpPipelineCommands.SceneUnload(AdditiveScenePath));
            Assert.IsTrue(result.Value<bool>("ok") == false, "脏场景 + 默认 abort 卸载应报错");
            Assert.That(result.Value<string>("error"), Does.Contain("dirtyAction"), "错误信息应提示 dirtyAction 选项");
        }
    }
}
#endif

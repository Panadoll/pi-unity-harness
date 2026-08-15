#if PI_UNITY_PIPELINE
using System.Collections.Generic;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    /// <summary>
    /// DirtyScenePolicy 策略测试：3 种 dirtyAction × 脏/干净场景 × untitled 场景（save 时）。
    /// 直接操作当前 active 场景（每个测试 SetUp 重建干净场景），避免 Additive 场景在
    /// untitled 场景下被 Unity 拒绝的问题。
    /// </summary>
    public sealed class DirtyScenePolicyTests
    {
        private const string TempScenePath = "Assets/PiDirtyScenePolicyTests_Tmp.unity";

        [SetUp]
        public void SetUp()
        {
            // 重建干净的空场景；脚本化 Single 模式会静默丢弃上一个场景的修改
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TempScenePath) != null)
                AssetDatabase.DeleteAsset(TempScenePath);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void Parse_NullOrEmpty_DefaultsToAbort()
        {
            Assert.AreEqual(DirtySceneAction.Abort, DirtyScenePolicy.Parse(null, "scene_open", out _));
            Assert.AreEqual(DirtySceneAction.Abort, DirtyScenePolicy.Parse("", "scene_open", out _));
        }

        [Test]
        public void Parse_KnownValues_AreCaseInsensitive()
        {
            Assert.AreEqual(DirtySceneAction.Save, DirtyScenePolicy.Parse("save", "scene_open", out _));
            Assert.AreEqual(DirtySceneAction.Save, DirtyScenePolicy.Parse("SAVE", "scene_open", out _));
            Assert.AreEqual(DirtySceneAction.Discard, DirtyScenePolicy.Parse("discard", "scene_open", out _));
            Assert.AreEqual(DirtySceneAction.Abort, DirtyScenePolicy.Parse("abort", "scene_open", out _));
        }

        [Test]
        public void Parse_InvalidValue_ReturnsError()
        {
            string error = null;
            var action = DirtyScenePolicy.Parse("explode", "scene_open", out error);
            Assert.AreEqual(DirtySceneAction.Abort, action, "非法值应回退到 abort");
            Assert.That(error, Does.Contain("explode"), "错误信息应包含非法值");
        }

        [Test]
        public void Apply_Abort_CleanScene_ReturnsNull()
        {
            var scene = EditorSceneManager.GetActiveScene();
            string error = DirtyScenePolicy.Apply(DirtySceneAction.Abort, new List<Scene> { scene }, "scene_open");
            Assert.IsNull(error, "干净场景不应被 abort");
        }

        [Test]
        public void Apply_Abort_DirtyScene_ReturnsError()
        {
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            string error = DirtyScenePolicy.Apply(DirtySceneAction.Abort, new List<Scene> { scene }, "scene_open");
            Assert.That(error, Is.Not.Null, "脏场景 + abort 应报错");
            Assert.That(error, Does.Contain("scene_open"), "错误信息应包含命令名");
        }

        [Test]
        public void Apply_Save_DirtyScene_SavesAndReturnsNull()
        {
            var scene = EditorSceneManager.GetActiveScene();
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, TempScenePath), "前置条件：保存场景到临时路径");
            EditorSceneManager.MarkSceneDirty(scene);
            string error = DirtyScenePolicy.Apply(DirtySceneAction.Save, new List<Scene> { scene }, "scene_open");
            Assert.IsNull(error, "save 策略应成功");
            Assert.IsFalse(scene.isDirty, "保存后场景不应再是脏的");
        }

        [Test]
        public void Apply_Save_UntitledDirtyScene_ReturnsErrorWithoutDialog()
        {
            var scene = EditorSceneManager.GetActiveScene();
            Assert.IsTrue(string.IsNullOrEmpty(scene.path), "前置条件：场景应未保存（untitled）");
            EditorSceneManager.MarkSceneDirty(scene);
            string error = DirtyScenePolicy.Apply(DirtySceneAction.Save, new List<Scene> { scene }, "scene_open");
            Assert.That(error, Is.Not.Null, "untitled 脏场景 + save 应按 abort 处理并报错");
            Assert.That(error, Does.Contain("从未保存").Or.Contains("path"), "错误应说明未保存原因");
        }

        [Test]
        public void Apply_Discard_DirtyScene_ReturnsNullAndKeepsDirty()
        {
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            string error = DirtyScenePolicy.Apply(DirtySceneAction.Discard, new List<Scene> { scene }, "scene_open");
            Assert.IsNull(error, "discard 策略应放行");
        }

        [Test]
        public void Apply_EmptySceneList_AnyStrategy_ReturnsNull()
        {
            Assert.IsNull(DirtyScenePolicy.Apply(DirtySceneAction.Abort, new List<Scene>(), "scene_open"));
            Assert.IsNull(DirtyScenePolicy.Apply(DirtySceneAction.Save, new List<Scene>(), "scene_open"));
            Assert.IsNull(DirtyScenePolicy.Apply(DirtySceneAction.Discard, new List<Scene>(), "scene_open"));
        }
    }
}
#endif

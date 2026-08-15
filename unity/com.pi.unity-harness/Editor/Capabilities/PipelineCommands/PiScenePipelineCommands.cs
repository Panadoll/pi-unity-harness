#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands.Data;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static partial class PiMcpPipelineCommands
    {
        [CliCommand("scene_create", "Create a new scene")]
        public static string SceneCreate([CliArg("path", "Optional scene path")] string path = null, [CliArg("setup", "Scene setup")] string setup = "DefaultGameObjects", [CliArg("dirty_action", "Dirty scene policy: save / discard / abort (default abort)")] string dirtyAction = "abort")
        {
            // 新建场景会替换当前场景：先应用脏场景策略（save 保存 / discard 丢弃 / abort 报错）
            string policyError = ApplyDirtyScenePolicy(dirtyAction, "scene_create");
            if (policyError != null)
                return PiMcpPipelineSupport.Ok(new { ok = false, error = policyError });

            var sceneSetup = string.Equals(setup, "Empty", StringComparison.OrdinalIgnoreCase)
                ? NewSceneSetup.EmptyScene
                : NewSceneSetup.DefaultGameObjects;
            var scene = EditorSceneManager.NewScene(sceneSetup, NewSceneMode.Single);
            if (!string.IsNullOrWhiteSpace(path))
                EditorSceneManager.SaveScene(scene, path);
            return PiMcpPipelineSupport.Ok(ToSceneData(scene));
        }

        [CliCommand("scene_open", "Open a scene")]
        public static string SceneOpen([CliArg("path", "Scene asset path")] string path, [CliArg("mode", "Single or Additive")] string mode = "Single", [CliArg("dirty_action", "Dirty scene policy: save / discard / abort (default abort)")] string dirtyAction = "abort")
        {
            var openMode = string.Equals(mode, "Additive", StringComparison.OrdinalIgnoreCase)
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;

            // Single 模式会替换当前场景：先应用脏场景策略；Additive 不替换当前场景，无需处理
            if (openMode == OpenSceneMode.Single)
            {
                string policyError = ApplyDirtyScenePolicy(dirtyAction, "scene_open");
                if (policyError != null)
                    return PiMcpPipelineSupport.Ok(new { ok = false, error = policyError });
            }

            var scene = EditorSceneManager.OpenScene(path, openMode);
            return PiMcpPipelineSupport.Ok(new { opened = ToSceneData(scene), scenes = OpenedScenes() });
        }

        [CliCommand("scene_save", "Save a scene")]
        public static string SceneSave([CliArg("path", "Optional scene path")] string path = null, [CliArg("save_as", "Save as new path")] string saveAs = null)
        {
            var scene = string.IsNullOrWhiteSpace(path) ? SceneManager.GetActiveScene() : FindOpenScene(path);
            bool saved = string.IsNullOrWhiteSpace(saveAs) ? EditorSceneManager.SaveScene(scene) : EditorSceneManager.SaveScene(scene, saveAs);
            return PiMcpPipelineSupport.Ok(new { saved, scene = ToSceneData(scene) });
        }

        [CliCommand("scene_get_data", "Get scene data")]
        public static string SceneGetData([CliArg("path", "Scene path or empty for active scene")] string path = null)
        {
            var scene = string.IsNullOrWhiteSpace(path) ? SceneManager.GetActiveScene() : FindOpenScene(path);
            return PiMcpPipelineSupport.Ok(ToSceneData(scene));
        }

        [CliCommand("scene_list_opened", "List opened scenes")]
        public static string SceneListOpened()
        {
            return PiMcpPipelineSupport.Ok(OpenedScenes());
        }

        [CliCommand("scene_set_active", "Set active scene")]
        public static string SceneSetActive([CliArg("path", "Scene path")] string path)
        {
            var scene = FindOpenScene(path);
            bool active = SceneManager.SetActiveScene(scene);
            return PiMcpPipelineSupport.Ok(new { active, scene = ToSceneData(scene) });
        }

        [CliCommand("scene_unload", "Unload scene")]
        public static string SceneUnload([CliArg("path", "Scene path")] string path, [CliArg("dirty_action", "Dirty scene policy: save / discard / abort (default abort)")] string dirtyAction = "abort")
        {
            var scene = FindOpenScene(path);
            if (!scene.IsValid())
                return PiMcpPipelineSupport.Ok(new { ok = false, error = $"Scene '{path}' is not open." });

            // 关闭脏场景前应用策略：save 先保存 / discard 直接关 / abort 报错
            var action = DirtyScenePolicy.Parse(dirtyAction, "scene_unload", out string parseError);
            if (parseError != null)
                return PiMcpPipelineSupport.Ok(new { ok = false, error = parseError });

            string policyError = DirtyScenePolicy.Apply(
                action,
                new List<UnityEngine.SceneManagement.Scene> { scene },
                "scene_unload");
            if (policyError != null)
                return PiMcpPipelineSupport.Ok(new { ok = false, error = policyError });

            bool unloaded = EditorSceneManager.CloseScene(scene, true);
            return PiMcpPipelineSupport.Ok(new PiMcpUnloadSceneResult { Unloaded = unloaded, Path = path });
        }

        /// <summary>
        /// 对当前所有已打开场景应用 dirtyAction 策略（scene_create / scene_open 共用）。
        /// 返回 null 表示可继续；返回非 null 为错误信息（命令应中止）。
        /// </summary>
        private static string ApplyDirtyScenePolicy(string dirtyAction, string commandName)
        {
            var action = DirtyScenePolicy.Parse(dirtyAction, commandName, out string parseError);
            if (parseError != null)
                return parseError;

            var openScenes = new List<UnityEngine.SceneManagement.Scene>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                openScenes.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i));

            return DirtyScenePolicy.Apply(action, openScenes, commandName);
        }
    }
}
#endif

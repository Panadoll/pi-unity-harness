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
        public static string SceneCreate([CliArg("path", "Optional scene path")] string path = null, [CliArg("setup", "Scene setup")] string setup = "DefaultGameObjects")
        {
            var sceneSetup = string.Equals(setup, "Empty", StringComparison.OrdinalIgnoreCase)
                ? NewSceneSetup.EmptyScene
                : NewSceneSetup.DefaultGameObjects;
            var scene = EditorSceneManager.NewScene(sceneSetup, NewSceneMode.Single);
            if (!string.IsNullOrWhiteSpace(path))
                EditorSceneManager.SaveScene(scene, path);
            return PiMcpPipelineSupport.Ok(ToSceneData(scene));
        }

        [CliCommand("scene_open", "Open a scene")]
        public static string SceneOpen([CliArg("path", "Scene asset path")] string path, [CliArg("mode", "Single or Additive")] string mode = "Single")
        {
            var openMode = string.Equals(mode, "Additive", StringComparison.OrdinalIgnoreCase)
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;
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
        public static string SceneUnload([CliArg("path", "Scene path")] string path)
        {
            var scene = FindOpenScene(path);
            bool unloaded = EditorSceneManager.CloseScene(scene, true);
            return PiMcpPipelineSupport.Ok(new PiMcpUnloadSceneResult { Unloaded = unloaded, Path = path });
        }
    }
}
#endif

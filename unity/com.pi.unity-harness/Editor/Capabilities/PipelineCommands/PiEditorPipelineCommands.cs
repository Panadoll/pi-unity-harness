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
        [CliCommand("editor_application_get_state", "Get Editor application state")]
        public static string EditorApplicationGetState()
        {
            return PiMcpPipelineSupport.Ok(new PiMcpEditorStatsData
            {
                IsPlaying = EditorApplication.isPlaying,
                IsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                IsPaused = EditorApplication.isPaused,
                IsCompiling = EditorApplication.isCompiling,
                IsUpdating = EditorApplication.isUpdating,
                ApplicationPath = EditorApplication.applicationPath,
                UnityVersion = Application.unityVersion,
                TimeSinceStartup = EditorApplication.timeSinceStartup,
            });
        }

        [CliCommand("editor_application_set_state", "Set Editor application state")]
        public static string EditorApplicationSetState([CliArg("set_playing", "Whether to apply play mode")] bool setPlaying = false, [CliArg("is_playing", "Set play mode")] bool isPlaying = false, [CliArg("set_paused", "Whether to apply pause state")] bool setPaused = false, [CliArg("is_paused", "Set pause state")] bool isPaused = false)
        {
            if (setPaused)
                EditorApplication.isPaused = isPaused;
            if (setPlaying)
                EditorApplication.isPlaying = isPlaying;
            return EditorApplicationGetState();
        }

        [CliCommand("editor_selection_get", "Get Editor selection")]
        public static string EditorSelectionGet([CliArg("include_gameobjects", "Include GameObjects")] bool includeGameObjects = true, [CliArg("include_assets", "Include selected asset GUIDs")] bool includeAssets = true)
        {
            var gameObjects = includeGameObjects
                ? Selection.gameObjects.Select(go => PiMcpPipelineSupport.ToGameObjectData(go)).ToList()
                : null;
            var selection = new PiMcpSelectionData
            {
                activeObject = ToObjectRef(Selection.activeObject),
                activeGameObject = PiMcpPipelineSupport.ToGameObjectData(Selection.activeGameObject),
                instanceIds = Selection.entityIds.Select(id => (long)EntityId.ToULong(id)).ToArray(),
                assetGuids = includeAssets ? Selection.assetGUIDs : null,
                gameObjects = gameObjects,
            };
            return PiMcpPipelineSupport.Ok(new
            {
                activeObject = selection.activeObject,
                activeGameObject = selection.activeGameObject,
                instanceIds = selection.instanceIds,
                assetGuids = selection.assetGuids,
                gameObjects = selection.gameObjects,
                selection,
            });
        }

        [CliCommand("editor_selection_set", "Set Editor selection")]
        public static string EditorSelectionSet([CliArg("instance_ids_json", "JSON array of instance ids")] string instanceIdsJson = null, [CliArg("asset_paths_json", "JSON array of asset paths")] string assetPathsJson = null)
        {
            var objects = new List<UnityEngine.Object>();
            if (!string.IsNullOrWhiteSpace(instanceIdsJson))
            {
                foreach (var id in Newtonsoft.Json.JsonConvert.DeserializeObject<long[]>(instanceIdsJson))
                {
                    var obj = PiMcpPipelineSupport.ObjectFromId(id);
                    if (obj != null)
                        objects.Add(obj);
                }
            }
            if (!string.IsNullOrWhiteSpace(assetPathsJson))
            {
                foreach (var path in Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(assetPathsJson))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (obj != null)
                        objects.Add(obj);
                }
            }
            Selection.objects = objects.ToArray();
            return EditorSelectionGet();
        }
    }
}
#endif

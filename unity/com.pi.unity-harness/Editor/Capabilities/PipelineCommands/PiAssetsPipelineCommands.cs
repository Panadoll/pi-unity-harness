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
        [CliCommand("assets_find", "Find assets by filter")]
        public static string AssetsFind([CliArg("filter", "AssetDatabase filter")] string filter = null, [CliArg("folder", "Search folder")] string folder = null, [CliArg("limit", "Maximum results")] int limit = 100)
        {
            string[] folders = string.IsNullOrWhiteSpace(folder) ? null : new[] { folder };
            var guids = AssetDatabase.FindAssets(filter ?? string.Empty, folders);
            var assets = guids.Take(limit <= 0 ? 100 : limit).Select(guid => ToAssetData(AssetDatabase.GUIDToAssetPath(guid))).ToList();
            return PiMcpPipelineSupport.Ok(new { count = assets.Count, assets });
        }

        [CliCommand("assets_find_builtin", "Find built-in assets")]
        public static string AssetsFindBuiltIn([CliArg("filter", "Name/type filter")] string filter = null, [CliArg("limit", "Maximum results")] int limit = 100)
        {
            var all = Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                .Where(obj => obj != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(obj)))
                .Where(obj => string.IsNullOrWhiteSpace(filter) || obj.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || obj.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(limit <= 0 ? 100 : limit)
                .Select(ToObjectRef)
                .ToList();
            return PiMcpPipelineSupport.Ok(new { count = all.Count, assets = all });
        }

        [CliCommand("assets_get_data", "Get asset data")]
        public static string AssetsGetData([CliArg("asset_path", "Asset path")] string assetPath = null, [CliArg("asset_guid", "Asset GUID")] string assetGuid = null, [CliArg("instance_id", "Unity instance id")] long instanceId = 0)
        {
            if (string.IsNullOrWhiteSpace(assetPath) && !string.IsNullOrWhiteSpace(assetGuid))
                assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
            if (string.IsNullOrWhiteSpace(assetPath) && instanceId != 0)
                assetPath = AssetDatabase.GetAssetPath(PiMcpPipelineSupport.ObjectFromId(instanceId));
            return PiMcpPipelineSupport.Ok(ToAssetData(assetPath));
        }

        [CliCommand("assets_create_folders", "Create asset folders")]
        public static string AssetsCreateFolders([CliArg("folders_json", "JSON array of folder paths")] string foldersJson)
        {
            var created = new List<string>();
            var errors = new List<string>();
            foreach (var folder in Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(foldersJson))
            {
                try
                {
                    EnsureAssetFolder(folder);
                    created.Add(folder);
                }
                catch (Exception ex)
                {
                    errors.Add(folder + ": " + ex.Message);
                }
            }
            AssetDatabase.Refresh();
            return PiMcpPipelineSupport.Ok(new PiMcpCreateFolderResponse { CreatedFolders = created, Errors = errors });
        }

        [CliCommand("assets_copy", "Copy assets")]
        public static string AssetsCopy([CliArg("source_path", "Source asset path")] string sourcePath, [CliArg("destination_path", "Destination asset path")] string destinationPath)
        {
            bool copied = AssetDatabase.CopyAsset(sourcePath, destinationPath);
            AssetDatabase.Refresh();
            return PiMcpPipelineSupport.Ok(new
            {
                copied,
                sourcePath,
                destinationPath,
                asset = copied ? ToAssetData(destinationPath) : null,
                response = new PiMcpCopyAssetsResponse
                {
                    CopiedAssets = copied ? new List<PiMcpAssetData> { ToAssetData(destinationPath) } : new List<PiMcpAssetData>(),
                    Errors = copied ? new List<string>() : new List<string> { "Copy failed" },
                },
            });
        }

        [CliCommand("assets_move", "Move assets")]
        public static string AssetsMove([CliArg("source_path", "Source asset path")] string sourcePath, [CliArg("destination_path", "Destination asset path")] string destinationPath)
        {
            string error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            bool moved = string.IsNullOrEmpty(error);
            AssetDatabase.Refresh();
            return PiMcpPipelineSupport.Ok(new
            {
                moved,
                error,
                sourcePath,
                destinationPath,
                response = new PiMcpMoveAssetsResponse
                {
                    MovedPaths = moved ? new List<string> { destinationPath } : new List<string>(),
                    Errors = moved ? new List<string>() : new List<string> { error },
                },
            });
        }

        [CliCommand("assets_delete", "Delete assets")]
        public static string AssetsDelete([CliArg("paths_json", "JSON array of asset paths")] string pathsJson)
        {
            var results = new List<object>();
            var deletedPaths = new List<string>();
            var errors = new List<string>();
            foreach (var path in Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(pathsJson))
            {
                bool deleted = AssetDatabase.DeleteAsset(path);
                results.Add(new { path, deleted });
                if (deleted)
                    deletedPaths.Add(path);
                else
                    errors.Add("Delete failed: " + path);
            }
            AssetDatabase.Refresh();
            return PiMcpPipelineSupport.Ok(new { results, response = new PiMcpDeleteAssetsResponse { DeletedPaths = deletedPaths, Errors = errors } });
        }

        [CliCommand("assets_modify", "Modify asset serialized properties")]
        public static string AssetsModify([CliArg("asset_path", "Asset path")] string assetPath, [CliArg("properties_json", "JSON object with serialized property values")] string propertiesJson)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                throw new ArgumentException("Asset not found: " + assetPath);
            PiMcpPipelineSupport.ApplySerializedProperties(asset, propertiesJson);
            AssetDatabase.SaveAssets();
            return PiMcpPipelineSupport.Ok(new { asset = ToAssetData(assetPath), properties = PiMcpPipelineSupport.ReadSerializedProperties(asset) });
        }

        [CliCommand("assets_refresh", "Refresh AssetDatabase")]
        public static string AssetsRefresh([CliArg("force", "Force update")] bool force = false)
        {
            AssetDatabase.Refresh(force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
            return PiMcpPipelineSupport.Ok(new { refreshed = true, force });
        }

        [CliCommand("assets_material_create", "Create material asset")]
        public static string AssetsMaterialCreate([CliArg("asset_path", "Material asset path")] string assetPath, [CliArg("shader_name", "Shader name")] string shaderName = "Standard")
        {
            EnsureAssetFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
            var shader = Shader.Find(shaderName) ?? Shader.Find("Standard");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();
            return PiMcpPipelineSupport.Ok(ToAssetData(assetPath));
        }

        [CliCommand("assets_prefab_create", "Create prefab asset from GameObject")]
        public static string AssetsPrefabCreate([CliArg("gameobject_instance_id", "GameObject instance id")] long gameObjectInstanceId, [CliArg("asset_path", "Prefab asset path")] string assetPath)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(gameObjectInstanceId, required: true);
            EnsureAssetFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, assetPath);
            return PiMcpPipelineSupport.Ok(new { prefab = ToAssetData(assetPath), gameObject = PiMcpPipelineSupport.ToGameObjectData(prefab) });
        }

        [CliCommand("assets_prefab_open", "Open prefab stage")]
        public static string AssetsPrefabOpen([CliArg("asset_path", "Prefab asset path")] string assetPath)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(assetPath);
            return PiMcpPipelineSupport.Ok(new { opened = stage != null, assetPath, scenePath = stage != null ? stage.scene.path : null });
        }

        [CliCommand("assets_prefab_close", "Close prefab stage")]
        public static string AssetsPrefabClose()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                StageUtility.GoBackToPreviousStage();
            return PiMcpPipelineSupport.Ok(new { closed = stage != null });
        }

        [CliCommand("assets_prefab_save", "Save current prefab stage")]
        public static string AssetsPrefabSave()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return PiMcpPipelineSupport.Ok(new { saved = false, reason = "No prefab stage is open" });
            EditorSceneManager.SaveScene(stage.scene);
            return PiMcpPipelineSupport.Ok(new { saved = true, assetPath = stage.assetPath });
        }

        [CliCommand("assets_prefab_instantiate", "Instantiate prefab into scene")]
        public static string AssetsPrefabInstantiate([CliArg("asset_path", "Prefab asset path")] string assetPath, [CliArg("parent_path", "Optional parent hierarchy path")] string parentPath = null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                throw new ArgumentException("Prefab not found: " + assetPath);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                var parent = PiMcpPipelineSupport.ResolveGameObject(path: parentPath, required: true);
                instance.transform.SetParent(parent.transform, false);
            }
            Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");
            return PiMcpPipelineSupport.Ok(PiMcpPipelineSupport.ToGameObjectData(instance, includeComponents: true));
        }

        [CliCommand("assets_shader_get_data", "Get shader data")]
        public static string AssetsShaderGetData([CliArg("shader_name", "Shader name")] string shaderName = null, [CliArg("asset_path", "Shader asset path")] string assetPath = null)
        {
            var shader = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.LoadAssetAtPath<Shader>(assetPath) : Shader.Find(shaderName);
            if (shader == null)
                throw new ArgumentException("Shader not found.");
            return PiMcpPipelineSupport.Ok(ToShaderData(shader));
        }

        [CliCommand("assets_shader_list_all", "List shaders")]
        public static string AssetsShaderListAll([CliArg("limit", "Maximum results")] int limit = 500)
        {
            var shaders = Resources.FindObjectsOfTypeAll<Shader>()
                .Where(shader => shader != null)
                .Take(limit <= 0 ? 500 : limit)
                .Select(ToShaderData)
                .ToList();
            return PiMcpPipelineSupport.Ok(new { count = shaders.Count, shaders });
        }
    }
}
#endif

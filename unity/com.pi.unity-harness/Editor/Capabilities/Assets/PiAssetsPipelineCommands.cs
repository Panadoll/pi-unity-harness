#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.Models;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters
{
    internal static class PiAssetsPipelineCommands
    {
        [CliCommand("assets_find", "Find assets by filter")]
        public static string AssetsFind([CliArg("filter", "AssetDatabase filter")] string filter = null, [CliArg("folder", "Search folder")] string folder = null, [CliArg("limit", "Maximum results")] int limit = 100)
        {
            string[] folders = string.IsNullOrWhiteSpace(folder) ? null : new[] { folder };
            var guids = AssetDatabase.FindAssets(filter ?? string.Empty, folders);
            var assets = guids.Take(limit <= 0 ? 100 : limit).Select(guid => ToAssetData(AssetDatabase.GUIDToAssetPath(guid))).ToList();
            return PiPipelineSupport.Ok(new { count = assets.Count, assets });
        }

        [CliCommand("assets_find_builtin", "Find built-in assets")]
        public static string AssetsFindBuiltIn([CliArg("filter", "Name/type filter")] string filter = null, [CliArg("limit", "Maximum results")] int limit = 100)
        {
            var all = Resources.FindObjectsOfTypeAll<UnityEngine.Object>()
                .Where(obj => obj != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(obj)))
                .Where(obj => string.IsNullOrWhiteSpace(filter) || obj.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || obj.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(limit <= 0 ? 100 : limit)
                .Select(PiPipelineCommandData.ToObjectRef)
                .ToList();
            return PiPipelineSupport.Ok(new { count = all.Count, assets = all });
        }

        [CliCommand("assets_get_data", "Get asset data")]
        public static string AssetsGetData([CliArg("asset_path", "Asset path")] string assetPath = null, [CliArg("asset_guid", "Asset GUID")] string assetGuid = null, [CliArg("instance_id", "Unity instance id")] long instanceId = 0)
        {
            if (string.IsNullOrWhiteSpace(assetPath) && !string.IsNullOrWhiteSpace(assetGuid))
                assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
            if (string.IsNullOrWhiteSpace(assetPath) && instanceId != 0)
                assetPath = AssetDatabase.GetAssetPath(PiPipelineSupport.ObjectFromId(instanceId));
            return PiPipelineSupport.Ok(ToAssetData(assetPath));
        }

        [CliCommand("assets_create_folders", "Create asset folders")]
        public static string AssetsCreateFolders([CliArg("folders_json", "JSON array of folder paths", Required = true)] string foldersJson)
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
            return PiPipelineSupport.Ok(new PiPipelineCreateFolderResponse { CreatedFolders = created, Errors = errors });
        }

        [CliCommand("assets_copy", "Copy assets")]
        public static string AssetsCopy([CliArg("source_path", "Source asset path", Required = true)] string sourcePath, [CliArg("destination_path", "Destination asset path", Required = true)] string destinationPath)
        {
            bool copied = AssetDatabase.CopyAsset(sourcePath, destinationPath);
            AssetDatabase.Refresh();
            return PiPipelineSupport.Ok(new
            {
                copied,
                sourcePath,
                destinationPath,
                asset = copied ? ToAssetData(destinationPath) : null,
                response = new PiPipelineCopyAssetsResponse
                {
                    CopiedAssets = copied ? new List<PiPipelineAssetData> { ToAssetData(destinationPath) } : new List<PiPipelineAssetData>(),
                    Errors = copied ? new List<string>() : new List<string> { "Copy failed" },
                },
            });
        }

        [CliCommand("assets_move", "Move assets")]
        public static string AssetsMove([CliArg("source_path", "Source asset path", Required = true)] string sourcePath, [CliArg("destination_path", "Destination asset path", Required = true)] string destinationPath)
        {
            string error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            bool moved = string.IsNullOrEmpty(error);
            AssetDatabase.Refresh();
            return PiPipelineSupport.Ok(new
            {
                moved,
                error,
                sourcePath,
                destinationPath,
                response = new PiPipelineMoveAssetsResponse
                {
                    MovedPaths = moved ? new List<string> { destinationPath } : new List<string>(),
                    Errors = moved ? new List<string>() : new List<string> { error },
                },
            });
        }

        [CliCommand("assets_delete", "Delete assets")]
        public static string AssetsDelete([CliArg("paths_json", "JSON array of asset paths", Required = true)] string pathsJson)
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
            return PiPipelineSupport.Ok(new { results, response = new PiPipelineDeleteAssetsResponse { DeletedPaths = deletedPaths, Errors = errors } });
        }

        [CliCommand("assets_modify", "Modify asset serialized properties")]
        public static string AssetsModify([CliArg("asset_path", "Asset path", Required = true)] string assetPath, [CliArg("properties_json", "JSON object with serialized property values", Required = true)] string propertiesJson)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                throw new ArgumentException("Asset not found: " + assetPath);
            PiPipelineSupport.ApplySerializedProperties(asset, propertiesJson);
            AssetDatabase.SaveAssets();
            return PiPipelineSupport.Ok(new { asset = ToAssetData(assetPath), properties = PiPipelineSupport.ReadSerializedProperties(asset) });
        }

        [CliCommand("assets_refresh", "Refresh AssetDatabase")]
        public static string AssetsRefresh([CliArg("force", "Force update")] bool force = false)
        {
            AssetDatabase.Refresh(force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
            return PiPipelineSupport.Ok(new { refreshed = true, force });
        }

        [CliCommand("assets_material_create", "Create material asset")]
        public static string AssetsMaterialCreate([CliArg("asset_path", "Material asset path", Required = true)] string assetPath, [CliArg("shader_name", "Shader name")] string shaderName = "Standard")
        {
            EnsureAssetFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
            var shader = Shader.Find(shaderName) ?? Shader.Find("Standard");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();
            return PiPipelineSupport.Ok(ToAssetData(assetPath));
        }

        [CliCommand("assets_prefab_create", "Create prefab asset from GameObject")]
        public static string AssetsPrefabCreate([CliArg("gameobject_instance_id", "GameObject instance id", Required = true)] long gameObjectInstanceId, [CliArg("asset_path", "Prefab asset path", Required = true)] string assetPath)
        {
            var go = PiPipelineSupport.ResolveGameObject(gameObjectInstanceId, required: true);
            EnsureAssetFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, assetPath);
            return PiPipelineSupport.Ok(new { prefab = ToAssetData(assetPath), gameObject = PiPipelineSupport.ToGameObjectData(prefab) });
        }

        [CliCommand("assets_prefab_open", "Open prefab stage")]
        public static string AssetsPrefabOpen([CliArg("asset_path", "Prefab asset path", Required = true)] string assetPath)
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(assetPath);
            return PiPipelineSupport.Ok(new { opened = stage != null, assetPath, scenePath = stage != null ? stage.scene.path : null });
        }

        [CliCommand("assets_prefab_close", "Close prefab stage")]
        public static string AssetsPrefabClose()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                StageUtility.GoBackToPreviousStage();
            return PiPipelineSupport.Ok(new { closed = stage != null });
        }

        [CliCommand("assets_prefab_save", "Save current prefab stage")]
        public static string AssetsPrefabSave()
        {
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return PiPipelineSupport.Ok(new { saved = false, reason = "No prefab stage is open" });
            EditorSceneManager.SaveScene(stage.scene);
            return PiPipelineSupport.Ok(new { saved = true, assetPath = stage.assetPath });
        }

        [CliCommand("assets_prefab_instantiate", "Instantiate prefab into scene")]
        public static string AssetsPrefabInstantiate([CliArg("asset_path", "Prefab asset path", Required = true)] string assetPath, [CliArg("parent_path", "Optional parent hierarchy path")] string parentPath = null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                throw new ArgumentException("Prefab not found: " + assetPath);
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                var parent = PiPipelineSupport.ResolveGameObject(path: parentPath, required: true);
                instance.transform.SetParent(parent.transform, false);
            }
            Undo.RegisterCreatedObjectUndo(instance, "Instantiate Prefab");
            return PiPipelineSupport.Ok(PiPipelineSupport.ToGameObjectData(instance, includeComponents: true));
        }

        [CliCommand("assets_shader_get_data", "Get shader data")]
        public static string AssetsShaderGetData([CliArg("shader_name", "Shader name")] string shaderName = null, [CliArg("asset_path", "Shader asset path")] string assetPath = null)
        {
            var shader = !string.IsNullOrWhiteSpace(assetPath) ? AssetDatabase.LoadAssetAtPath<Shader>(assetPath) : Shader.Find(shaderName);
            if (shader == null)
                throw new ArgumentException("Shader not found.");
            return PiPipelineSupport.Ok(ToShaderData(shader));
        }

        [CliCommand("assets_shader_list_all", "List shaders")]
        public static string AssetsShaderListAll([CliArg("limit", "Maximum results")] int limit = 500)
        {
            var shaders = Resources.FindObjectsOfTypeAll<Shader>()
                .Where(shader => shader != null)
                .Take(limit <= 0 ? 500 : limit)
                .Select(ToShaderData)
                .ToList();
            return PiPipelineSupport.Ok(new { count = shaders.Count, shaders });
        }

        private static PiPipelineShaderData ToShaderData(Shader shader)
        {
            if (shader == null)
                return null;

            var properties = new List<PiPipelineShaderPropertyData>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                properties.Add(new PiPipelineShaderPropertyData
                {
                    Name = shader.GetPropertyName(i),
                    Description = shader.GetPropertyDescription(i),
                    Type = shader.GetPropertyType(i).ToString(),
                });
            }

            return new PiPipelineShaderData
            {
                Name = shader.name,
                InstanceId = PiPipelineSupport.ObjectId(shader),
                AssetPath = AssetDatabase.GetAssetPath(shader),
                PropertyCount = shader.GetPropertyCount(),
                Properties = properties,
            };
        }

        private static PiPipelineAssetData ToAssetData(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                return new PiPipelineAssetData { assetPath = assetPath, assetGuid = AssetDatabase.AssetPathToGUID(assetPath) };

            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            return new PiPipelineAssetData
            {
                instanceId = PiPipelineSupport.ObjectId(asset),
                name = asset.name,
                type = asset.GetType().FullName,
                assetPath = assetPath,
                assetGuid = AssetDatabase.AssetPathToGUID(assetPath),
                mainAssetType = type != null ? type.FullName : null,
            };
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || AssetDatabase.IsValidFolder(folder))
                return;

            string[] parts = folder.Replace('\\', '/').Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif

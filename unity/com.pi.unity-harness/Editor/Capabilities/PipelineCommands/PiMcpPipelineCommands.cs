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
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static partial class PiMcpPipelineCommands
    {
        private static async Task WaitForPackageRequest(Request request)
        {
            while (!request.IsCompleted)
                await Task.Delay(50);
            if (request.Status == StatusCode.Failure)
                throw new InvalidOperationException(request.Error.message);
        }

        private static async Task<string> AwaitPackageRequest(Request request, string command, Func<object> getResult)
        {
            await WaitForPackageRequest(request);
            return PiMcpPipelineSupport.Ok(new { command, result = getResult() });
        }

        private static PiMcpPackageData ToPackageData(UnityEditor.PackageManager.PackageInfo package)
        {
            return new PiMcpPackageData
            {
                Name = package.name,
                DisplayName = package.displayName ?? package.name,
                Version = package.version,
                Description = package.description ?? string.Empty,
                Source = package.source.ToString(),
                Category = package.category ?? string.Empty,
                ResolvedPath = package.resolvedPath,
            };
        }

        private static PiMcpShaderData ToShaderData(Shader shader)
        {
            if (shader == null)
                return null;

            var properties = new List<PiMcpShaderPropertyData>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                properties.Add(new PiMcpShaderPropertyData
                {
                    Name = shader.GetPropertyName(i),
                    Description = shader.GetPropertyDescription(i),
                    Type = shader.GetPropertyType(i).ToString(),
                });
            }

            return new PiMcpShaderData
            {
                Name = shader.name,
                InstanceId = PiMcpPipelineSupport.ObjectId(shader),
                AssetPath = AssetDatabase.GetAssetPath(shader),
                PropertyCount = shader.GetPropertyCount(),
                Properties = properties,
            };
        }

        private static PiMcpAssetData ToAssetData(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                return new PiMcpAssetData { assetPath = assetPath, assetGuid = AssetDatabase.AssetPathToGUID(assetPath) };

            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            return new PiMcpAssetData
            {
                instanceId = PiMcpPipelineSupport.ObjectId(asset),
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

        private static PiMcpSceneData ToSceneData(Scene scene)
        {
            return new PiMcpSceneData
            {
                name = scene.name,
                path = scene.path,
                buildIndex = scene.buildIndex,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty,
                isValid = scene.IsValid(),
                rootCount = scene.IsValid() ? scene.rootCount : 0,
            };
        }

        private static List<PiMcpSceneData> OpenedScenes()
        {
            var scenes = new List<PiMcpSceneData>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                scenes.Add(ToSceneData(SceneManager.GetSceneAt(i)));
            return scenes;
        }

        private static Scene FindOpenScene(string path)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(scene.path, path, StringComparison.OrdinalIgnoreCase) || string.Equals(scene.name, path, StringComparison.OrdinalIgnoreCase))
                    return scene;
            }
            throw new ArgumentException("Open scene not found: " + path);
        }

        private static PiMcpObjectRefData ToObjectRef(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

            string assetPath = AssetDatabase.GetAssetPath(obj);
            return new PiMcpObjectRefData
            {
                instanceId = PiMcpPipelineSupport.ObjectId(obj),
                name = obj.name,
                type = obj.GetType().FullName,
                assetPath = assetPath,
                assetGuid = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath),
            };
        }
    }

    internal static class PiMcpConsoleLogBuffer
    {
        public static List<PiUnityConsoleLogBuffer.Entry> Get(int limit, string level) =>
            PiUnityConsoleLogBuffer.Get(limit, level);

        public static void Clear() => PiUnityConsoleLogBuffer.Clear();
    }
}
#endif

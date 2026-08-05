using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Builds a bounded editor context snapshot suitable for agent consumption.
    /// </summary>
    internal static class PiUnityContextSnapshot
    {
        internal const int DefaultMaxDepth = 3;
        internal const int DefaultMaxNodes = 500;
        internal const int DefaultLogLimit = 50;
        internal const int MaxAllowedDepth = 20;
        internal const int MaxAllowedNodes = 5000;
        internal const int MaxAllowedLogs = 500;

        public static string BuildJson(int maxDepth, int maxNodes, int logLimit, string logLevel, bool includeComponents)
        {
            maxDepth = Mathf.Clamp(maxDepth, 0, MaxAllowedDepth);
            maxNodes = Mathf.Clamp(maxNodes, 1, MaxAllowedNodes);
            logLimit = Mathf.Clamp(logLimit, 0, MaxAllowedLogs);
            logLevel = string.IsNullOrWhiteSpace(logLevel) ? "error" : logLevel.Trim();

            Scene scene = SceneManager.GetActiveScene();
            HierarchyBuildState hierarchyState = new HierarchyBuildState(maxDepth, maxNodes, includeComponents);
            List<HierarchyNode> roots = BuildRoots(scene, hierarchyState);
            List<PiUnityConsoleLogBuffer.Entry> logs = PiUnityConsoleLogBuffer.Get(logLimit, logLevel);
            Camera mainCamera = Camera.main;

            Snapshot snapshot = new Snapshot
            {
                schemaVersion = 1,
                capturedAtUtc = DateTime.UtcNow.ToString("o"),
                project = new ProjectSnapshot
                {
                    path = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/'),
                    assetsPath = Application.dataPath.Replace('\\', '/'),
                    productName = Application.productName,
                    unityVersion = Application.unityVersion,
                    platform = Application.platform.ToString(),
                },
                editor = new EditorSnapshot
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                    isPaused = EditorApplication.isPaused,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating,
                    isFocused = Application.isFocused,
                    timeSinceStartup = EditorApplication.timeSinceStartup,
                },
                scene = BuildSceneSnapshot(scene, mainCamera),
                selection = new SelectionSnapshot
                {
                    count = Selection.objects != null ? Selection.objects.Length : 0,
                    activeObject = BuildObjectReference(Selection.activeObject),
                    activeGameObject = BuildObjectReference(Selection.activeGameObject),
                },
                hierarchy = new HierarchySnapshot
                {
                    maxDepth = maxDepth,
                    maxNodes = maxNodes,
                    nodeCount = hierarchyState.NodeCount,
                    truncated = hierarchyState.Truncated,
                    roots = roots,
                },
                logs = new LogSnapshot
                {
                    level = logLevel,
                    limit = logLimit,
                    count = logs.Count,
                    entries = logs,
                },
            };

            return JsonConvert.SerializeObject(snapshot, Formatting.None, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            });
        }

        private static List<HierarchyNode> BuildRoots(Scene scene, HierarchyBuildState state)
        {
            List<HierarchyNode> roots = new List<HierarchyNode>();
            if (!scene.IsValid() || !scene.isLoaded)
                return roots;

            GameObject[] rootObjects = scene.GetRootGameObjects();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                HierarchyNode node = BuildNode(rootObjects[i], 0, state);
                if (node != null)
                    roots.Add(node);

                if (state.NodeCount < state.MaxNodes)
                    continue;

                if (i + 1 < rootObjects.Length)
                    state.Truncated = true;
                break;
            }

            return roots;
        }

        private static SceneSnapshot BuildSceneSnapshot(Scene scene, Camera mainCamera)
        {
            bool valid = scene.IsValid();
            return new SceneSnapshot
            {
                name = valid ? scene.name : null,
                path = valid ? scene.path : null,
                buildIndex = valid ? scene.buildIndex : -1,
                isLoaded = valid && scene.isLoaded,
                isDirty = valid && scene.isDirty,
                isValid = valid,
                rootCount = valid ? scene.rootCount : 0,
                openSceneCount = SceneManager.sceneCount,
                hasMainCamera = mainCamera != null,
                mainCameraPath = mainCamera != null ? HierarchyPath(mainCamera.transform) : null,
            };
        }

        private static HierarchyNode BuildNode(GameObject gameObject, int depth, HierarchyBuildState state)
        {
            if (gameObject == null || state.NodeCount >= state.MaxNodes)
            {
                state.Truncated = true;
                return null;
            }

            state.NodeCount++;
            Transform transform = gameObject.transform;
            HierarchyNode node = new HierarchyNode
            {
                instanceId = ObjectId(gameObject),
                name = gameObject.name,
                path = HierarchyPath(transform),
                tag = gameObject.tag,
                layer = gameObject.layer,
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                childCount = transform.childCount,
            };

            if (state.IncludeComponents)
            {
                Component[] components = gameObject.GetComponents<Component>();
                node.components = new List<string>(components.Length);
                for (int i = 0; i < components.Length; i++)
                    node.components.Add(components[i] != null ? components[i].GetType().FullName : "MissingScript");
            }

            if (transform.childCount == 0)
                return node;

            if (depth >= state.MaxDepth)
            {
                state.Truncated = true;
                return node;
            }

            node.children = new List<HierarchyNode>();
            for (int i = 0; i < transform.childCount; i++)
            {
                if (state.NodeCount >= state.MaxNodes)
                {
                    state.Truncated = true;
                    break;
                }

                HierarchyNode child = BuildNode(transform.GetChild(i).gameObject, depth + 1, state);
                if (child != null)
                    node.children.Add(child);
            }

            return node;
        }

        private static ObjectReference BuildObjectReference(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

            GameObject gameObject = obj as GameObject;
            if (gameObject == null && obj is Component component)
                gameObject = component.gameObject;

            string assetPath = AssetDatabase.GetAssetPath(obj);
            return new ObjectReference
            {
                instanceId = ObjectId(obj),
                name = obj.name,
                type = obj.GetType().FullName,
                hierarchyPath = gameObject != null ? HierarchyPath(gameObject.transform) : null,
                assetPath = string.IsNullOrEmpty(assetPath) ? null : assetPath,
            };
        }

        private static long ObjectId(UnityEngine.Object obj)
        {
            if (obj == null)
                return 0;
#if UNITY_6000_0_OR_NEWER
            return (long)EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

        private static string HierarchyPath(Transform transform)
        {
            if (transform == null)
                return null;

            Stack<string> names = new Stack<string>();
            for (Transform current = transform; current != null; current = current.parent)
                names.Push(current.name);
            return string.Join("/", names.ToArray());
        }

        private sealed class HierarchyBuildState
        {
            public readonly int MaxDepth;
            public readonly int MaxNodes;
            public readonly bool IncludeComponents;
            public int NodeCount;
            public bool Truncated;

            public HierarchyBuildState(int maxDepth, int maxNodes, bool includeComponents)
            {
                MaxDepth = maxDepth;
                MaxNodes = maxNodes;
                IncludeComponents = includeComponents;
            }
        }

        [Serializable]
        private sealed class Snapshot
        {
            public int schemaVersion;
            public string capturedAtUtc;
            public ProjectSnapshot project;
            public EditorSnapshot editor;
            public SceneSnapshot scene;
            public SelectionSnapshot selection;
            public HierarchySnapshot hierarchy;
            public LogSnapshot logs;
        }

        [Serializable]
        private sealed class ProjectSnapshot
        {
            public string path;
            public string assetsPath;
            public string productName;
            public string unityVersion;
            public string platform;
        }

        [Serializable]
        private sealed class EditorSnapshot
        {
            public bool isPlaying;
            public bool isPlayingOrWillChangePlaymode;
            public bool isPaused;
            public bool isCompiling;
            public bool isUpdating;
            public bool isFocused;
            public double timeSinceStartup;
        }

        [Serializable]
        private sealed class SceneSnapshot
        {
            public string name;
            public string path;
            public int buildIndex;
            public bool isLoaded;
            public bool isDirty;
            public bool isValid;
            public int rootCount;
            public int openSceneCount;
            public bool hasMainCamera;
            public string mainCameraPath;
        }

        [Serializable]
        private sealed class SelectionSnapshot
        {
            public int count;
            public ObjectReference activeObject;
            public ObjectReference activeGameObject;
        }

        [Serializable]
        private sealed class ObjectReference
        {
            public long instanceId;
            public string name;
            public string type;
            public string hierarchyPath;
            public string assetPath;
        }

        [Serializable]
        private sealed class HierarchySnapshot
        {
            public int maxDepth;
            public int maxNodes;
            public int nodeCount;
            public bool truncated;
            public List<HierarchyNode> roots;
        }

        [Serializable]
        private sealed class HierarchyNode
        {
            public long instanceId;
            public string name;
            public string path;
            public string tag;
            public int layer;
            public bool activeSelf;
            public bool activeInHierarchy;
            public int childCount;
            public List<string> components;
            public List<HierarchyNode> children;
        }

        [Serializable]
        private sealed class LogSnapshot
        {
            public string level;
            public int limit;
            public int count;
            public List<PiUnityConsoleLogBuffer.Entry> entries;
        }
    }
}

#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands.Data;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiMcpPipelineSupport
    {
        public static string Json(object value)
        {
            return JsonConvert.SerializeObject(value, Formatting.None, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            });
        }

        public static string Ok(object value)
        {
            return Json(value);
        }

        public static string OkMessage(string command, string message)
        {
            return Json(new { ok = true, command, message });
        }

        public static long ObjectId(UnityEngine.Object obj)
        {
            if (obj == null)
                return 0;
#if UNITY_6000_3_OR_NEWER
            return (long)EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

        public static UnityEngine.Object ObjectFromId(long id)
        {
            if (id == 0)
                return null;
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(EntityId.FromULong((ulong)id));
#else
            return EditorUtility.InstanceIDToObject((int)id);
#endif
        }

        public static GameObject ResolveGameObject(long instanceId = 0, string path = null, string name = null, bool required = true)
        {
            if (instanceId != 0)
            {
                var obj = ObjectFromId(instanceId) as GameObject;
                if (obj != null)
                    return obj;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                var go = GameObject.Find(path);
                if (go != null)
                    return go;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                var go = GameObject.Find(name);
                if (go != null)
                    return go;

                foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (candidate.name == name && !EditorUtility.IsPersistent(candidate))
                        return candidate;
                }
            }

            if (required)
                throw new ArgumentException("GameObject not found. Provide instance_id, path, or name.");
            return null;
        }

        public static Component ResolveComponent(long componentInstanceId = 0, long gameObjectInstanceId = 0, string path = null, string typeName = null, bool required = true)
        {
            if (componentInstanceId != 0)
            {
                var component = ObjectFromId(componentInstanceId) as Component;
                if (component != null)
                    return component;
            }

            GameObject go = ResolveGameObject(gameObjectInstanceId, path, null, required: false);
            if (go != null)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                    return go.transform;

                foreach (var component in go.GetComponents<Component>())
                {
                    if (component == null)
                        continue;
                    var type = component.GetType();
                    if (string.Equals(type.FullName, typeName, StringComparison.Ordinal) || string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        return component;
                }
            }

            if (required)
                throw new ArgumentException("Component not found. Provide component_instance_id or gameobject reference with type_name.");
            return null;
        }

        public static Type FindType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var type = Type.GetType(typeName);
            if (type != null)
                return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(typeName);
                    if (type != null)
                        return type;

                    foreach (var candidate in assembly.GetTypes())
                    {
                        if (candidate.Name == typeName || candidate.FullName == typeName)
                            return candidate;
                    }
                }
                catch { /* Some assemblies cannot be enumerated; ignore */ }
            }
            return null;
        }

        public static PiMcpGameObjectData ToGameObjectData(GameObject go, bool includeComponents = false, bool includeChildren = false)
        {
            if (go == null)
                return null;

            var data = new PiMcpGameObjectData
            {
                instanceId = ObjectId(go),
                name = go.name,
                path = GetHierarchyPath(go.transform),
                tag = go.tag,
                layer = go.layer,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                scenePath = go.scene.path,
            };

            if (includeComponents)
            {
                data.components = new List<PiMcpComponentData>();
                foreach (var component in go.GetComponents<Component>())
                    data.components.Add(ToComponentData(component, includeProperties: false));
            }

            if (includeChildren)
            {
                data.children = new List<PiMcpGameObjectData>();
                foreach (Transform child in go.transform)
                    data.children.Add(ToGameObjectData(child.gameObject, includeComponents: false, includeChildren: false));
            }

            return data;
        }

        public static PiMcpComponentData ToComponentData(Component component, bool includeProperties)
        {
            if (component == null)
                return null;

            var data = new PiMcpComponentData
            {
                instanceId = ObjectId(component),
                type = component.GetType().FullName,
                name = component.GetType().Name,
                enabled = true,
            };

            if (component is Behaviour behaviour)
                data.enabled = behaviour.enabled;
            else if (component is Renderer renderer)
                data.enabled = renderer.enabled;

            if (includeProperties)
                data.properties = ReadSerializedProperties(component);

            return data;
        }

        public static Dictionary<string, object> ReadSerializedProperties(UnityEngine.Object obj)
        {
            var result = new Dictionary<string, object>();
            if (obj == null)
                return result;

            var serialized = new SerializedObject(obj);
            var iterator = serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                result[iterator.propertyPath] = ReadSerializedProperty(iterator);
            }
            return result;
        }

        public static void ApplySerializedProperties(UnityEngine.Object obj, string propertiesJson)
        {
            if (obj == null || string.IsNullOrWhiteSpace(propertiesJson))
                return;

            var properties = JObject.Parse(propertiesJson);
            var serialized = new SerializedObject(obj);
            foreach (var property in properties.Properties())
            {
                var target = serialized.FindProperty(property.Name);
                if (target == null)
                    continue;
                ApplySerializedProperty(target, property.Value);
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(obj);
        }

        public static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return null;

            var names = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static object ReadSerializedProperty(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue;
                case SerializedPropertyType.Boolean: return property.boolValue;
                case SerializedPropertyType.Float: return property.floatValue;
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Color: return property.colorValue.ToString();
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue != null ? ObjectId(property.objectReferenceValue) : 0;
                case SerializedPropertyType.Enum: return property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString();
                case SerializedPropertyType.Rect: return property.rectValue.ToString();
                case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return property.quaternionValue.eulerAngles.ToString();
                default: return property.ToString();
            }
        }

        public static string CaptureCamera(Camera camera, string path, int width, int height)
        {
            path = NormalizeScreenshotPath(path);
            width = width > 0 ? width : Math.Max(1, camera.pixelWidth > 0 ? camera.pixelWidth : 1280);
            height = height > 0 ? height : Math.Max(1, camera.pixelHeight > 0 ? camera.pixelHeight : 720);

            var target = new RenderTexture(width, height, 24);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                RenderTexture.active = target;
                camera.Render();
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                return path;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        public static string NormalizeScreenshotPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Application.dataPath, "..", "Temp", "PiUnityHarness", "Screenshots", "capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }

        private static void ApplySerializedProperty(SerializedProperty property, JToken token)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    property.intValue = token.ToObject<int>();
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = token.ToObject<bool>();
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = token.ToObject<float>();
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = token.ToObject<string>();
                    break;
                case SerializedPropertyType.Enum:
                    if (token.Type == JTokenType.Integer)
                        property.enumValueIndex = token.ToObject<int>();
                    else
                    {
                        string value = token.ToObject<string>();
                        int index = Array.IndexOf(property.enumDisplayNames, value);
                        if (index >= 0)
                            property.enumValueIndex = index;
                    }
                    break;
            }
        }
    }
}
#endif

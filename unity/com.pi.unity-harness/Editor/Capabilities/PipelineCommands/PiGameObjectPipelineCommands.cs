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
        [CliCommand("gameobject_find", "Find GameObjects by instance id, hierarchy path, or name")]
        public static string GameObjectFind([CliArg("instance_id", "Unity instance id")] long instanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("name", "GameObject name")] string name = null, [CliArg("include_components", "Include components")] bool includeComponents = false, [CliArg("include_children", "Include child summary")] bool includeChildren = false)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(instanceId, path, name, required: true);
            return PiMcpPipelineSupport.Ok(PiMcpPipelineSupport.ToGameObjectData(go, includeComponents, includeChildren));
        }

        [CliCommand("gameobject_create", "Create a GameObject")]
        public static string GameObjectCreate([CliArg("name", "GameObject name")] string name = "GameObject", [CliArg("parent_path", "Optional parent hierarchy path")] string parentPath = null)
        {
            var go = new GameObject(string.IsNullOrWhiteSpace(name) ? "GameObject" : name);
            if (!string.IsNullOrWhiteSpace(parentPath))
            {
                var parent = PiMcpPipelineSupport.ResolveGameObject(path: parentPath, required: true);
                go.transform.SetParent(parent.transform, false);
            }
            Undo.RegisterCreatedObjectUndo(go, "Create GameObject");
            Selection.activeGameObject = go;
            return PiMcpPipelineSupport.Ok(PiMcpPipelineSupport.ToGameObjectData(go));
        }

        [CliCommand("gameobject_destroy", "Destroy a GameObject")]
        public static string GameObjectDestroy([CliArg("instance_id", "Unity instance id")] long instanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("name", "GameObject name")] string name = null)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(instanceId, path, name, required: true);
            var data = PiMcpPipelineSupport.ToGameObjectData(go);
            Undo.DestroyObjectImmediate(go);
            return PiMcpPipelineSupport.Ok(new PiMcpDestroyGameObjectResult { Success = true, GameObject = data });
        }

        [CliCommand("gameobject_duplicate", "Duplicate a GameObject")]
        public static string GameObjectDuplicate([CliArg("instance_id", "Unity instance id")] long instanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("name", "GameObject name")] string name = null, [CliArg("new_name", "Duplicated GameObject name")] string newName = null)
        {
            var source = PiMcpPipelineSupport.ResolveGameObject(instanceId, path, name, required: true);
            var clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
            clone.name = string.IsNullOrWhiteSpace(newName) ? source.name : newName;
            Undo.RegisterCreatedObjectUndo(clone, "Duplicate GameObject");
            Selection.activeGameObject = clone;
            return PiMcpPipelineSupport.Ok(PiMcpPipelineSupport.ToGameObjectData(clone, includeComponents: true));
        }

        [CliCommand("gameobject_modify", "Modify GameObject properties")]
        public static string GameObjectModify([CliArg("instance_id", "Unity instance id")] long instanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("name", "GameObject name")] string name = null, [CliArg("new_name", "New GameObject name")] string newName = null, [CliArg("set_active", "Whether to apply active value")] bool setActive = false, [CliArg("active", "Active state")] bool active = false, [CliArg("tag", "Tag")] string tag = null, [CliArg("layer", "Layer, -1 skips")] int layer = -1)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(instanceId, path, name, required: true);
            Undo.RecordObject(go, "Modify GameObject");
            if (!string.IsNullOrWhiteSpace(newName))
                go.name = newName;
            if (setActive)
                go.SetActive(active);
            if (!string.IsNullOrWhiteSpace(tag))
                go.tag = tag;
            if (layer >= 0)
                go.layer = layer;
            EditorUtility.SetDirty(go);
            return PiMcpPipelineSupport.Ok(PiMcpPipelineSupport.ToGameObjectData(go, includeComponents: true));
        }

        [CliCommand("gameobject_set_parent", "Set GameObject parent")]
        public static string GameObjectSetParent([CliArg("instance_id", "Unity instance id")] long instanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("parent_instance_id", "Parent instance id")] long parentInstanceId = 0, [CliArg("parent_path", "Parent hierarchy path")] string parentPath = null, [CliArg("world_position_stays", "Keep world position")] bool worldPositionStays = true)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(instanceId, path, null, required: true);
            var parent = PiMcpPipelineSupport.ResolveGameObject(parentInstanceId, parentPath, null, required: false);
            Undo.SetTransformParent(go.transform, parent != null ? parent.transform : null, "Set GameObject Parent");
            go.transform.SetParent(parent != null ? parent.transform : null, worldPositionStays);
            return PiMcpPipelineSupport.Ok(PiMcpPipelineSupport.ToGameObjectData(go));
        }

        [CliCommand("gameobject_component_add", "Add a component to a GameObject")]
        public static string GameObjectComponentAdd([CliArg("instance_id", "Unity instance id")] long instanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("type_name", "Component type name")] string typeName = null)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(instanceId, path, null, required: true);
            var type = PiMcpPipelineSupport.FindType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                throw new ArgumentException("Component type not found or invalid: " + typeName);
            var component = Undo.AddComponent(go, type);
            var response = new PiMcpAddComponentResponse
            {
                AddedComponents = new List<PiMcpComponentData> { PiMcpPipelineSupport.ToComponentData(component, includeProperties: false) },
                Messages = new List<string> { "Added component " + type.FullName + " to " + go.name },
            };
            return PiMcpPipelineSupport.Ok(response);
        }

        [CliCommand("gameobject_component_destroy", "Destroy a component on a GameObject")]
        public static string GameObjectComponentDestroy([CliArg("component_instance_id", "Component instance id")] long componentInstanceId = 0, [CliArg("gameobject_instance_id", "GameObject instance id")] long gameObjectInstanceId = 0, [CliArg("type_name", "Component type name")] string typeName = null)
        {
            var component = PiMcpPipelineSupport.ResolveComponent(componentInstanceId, gameObjectInstanceId, null, typeName, required: true);
            if (component is Transform)
                throw new InvalidOperationException("Transform component cannot be destroyed.");
            var data = PiMcpPipelineSupport.ToComponentData(component, includeProperties: false);
            Undo.DestroyObjectImmediate(component);
            return PiMcpPipelineSupport.Ok(new PiMcpDestroyComponentsResponse
            {
                DestroyedComponents = new List<PiMcpComponentData> { data },
                Errors = new List<string>(),
            });
        }

        [CliCommand("gameobject_component_get", "Get component data")]
        public static string GameObjectComponentGet([CliArg("component_instance_id", "Component instance id")] long componentInstanceId = 0, [CliArg("gameobject_instance_id", "GameObject instance id")] long gameObjectInstanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("type_name", "Component type name")] string typeName = null)
        {
            var component = PiMcpPipelineSupport.ResolveComponent(componentInstanceId, gameObjectInstanceId, path, typeName, required: true);
            var components = component.gameObject.GetComponents<Component>();
            int index = Array.IndexOf(components, component);
            return PiMcpPipelineSupport.Ok(new PiMcpGetComponentResponse
            {
                Reference = ToObjectRef(component),
                Index = index,
                Component = PiMcpPipelineSupport.ToComponentData(component, includeProperties: false),
                Properties = PiMcpPipelineSupport.ReadSerializedProperties(component),
            });
        }

        [CliCommand("gameobject_component_list_all", "List GameObject components")]
        public static string GameObjectComponentListAll([CliArg("instance_id", "Unity instance id")] long instanceId = 0, [CliArg("path", "Hierarchy path")] string path = null, [CliArg("name", "GameObject name")] string name = null)
        {
            var go = PiMcpPipelineSupport.ResolveGameObject(instanceId, path, name, required: true);
            string[] items = go.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().FullName)
                .ToArray();
            return PiMcpPipelineSupport.Ok(new
            {
                gameObject = PiMcpPipelineSupport.ToGameObjectData(go),
                components = items.Select(type => new { type }).ToArray(),
                list = new PiMcpComponentListResult
                {
                    Items = items,
                    Page = 0,
                    PageSize = items.Length,
                    TotalCount = items.Length,
                    TotalPages = items.Length == 0 ? 0 : 1,
                },
            });
        }

        [CliCommand("gameobject_component_modify", "Modify component serialized properties")]
        public static string GameObjectComponentModify([CliArg("component_instance_id", "Component instance id")] long componentInstanceId = 0, [CliArg("properties_json", "JSON object with serialized property values")] string propertiesJson = null)
        {
            var component = PiMcpPipelineSupport.ResolveComponent(componentInstanceId, required: true);
            Undo.RecordObject(component, "Modify Component");
            PiMcpPipelineSupport.ApplySerializedProperties(component, propertiesJson);
            var components = component.gameObject.GetComponents<Component>();
            return PiMcpPipelineSupport.Ok(new PiMcpModifyComponentResponse
            {
                Success = true,
                Reference = ToObjectRef(component),
                Index = Array.IndexOf(components, component),
                Component = PiMcpPipelineSupport.ToComponentData(component, includeProperties: false),
                Logs = new[] { "Applied serialized properties" },
            });
        }
    }
}
#endif

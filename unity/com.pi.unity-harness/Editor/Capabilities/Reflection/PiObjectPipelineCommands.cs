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
    internal static class PiObjectPipelineCommands
    {
        [CliCommand("object_get_data", "Get Unity object data")]
        public static string ObjectGetData([CliArg("instance_id", "Unity instance id", Required = true)] long instanceId, [CliArg("include_serialized", "Include serialized properties")] bool includeSerialized = true)
        {
            var obj = PiPipelineSupport.ObjectFromId(instanceId);
            if (obj == null)
                throw new ArgumentException("Object not found: " + instanceId);
            return PiPipelineSupport.Ok(new { reference = PiPipelineCommandData.ToObjectRef(obj), properties = includeSerialized ? PiPipelineSupport.ReadSerializedProperties(obj) : null });
        }

        [CliCommand("object_modify", "Modify Unity object serialized properties")]
        public static string ObjectModify([CliArg("instance_id", "Unity instance id", Required = true)] long instanceId, [CliArg("properties_json", "JSON object with serialized property values", Required = true)] string propertiesJson)
        {
            var obj = PiPipelineSupport.ObjectFromId(instanceId);
            if (obj == null)
                throw new ArgumentException("Object not found: " + instanceId);
            Undo.RecordObject(obj, "Modify Object");
            PiPipelineSupport.ApplySerializedProperties(obj, propertiesJson);
            return PiPipelineSupport.Ok(new PiPipelineModifyObjectResponse
            {
                Success = true,
                Reference = PiPipelineCommandData.ToObjectRef(obj),
                Properties = PiPipelineSupport.ReadSerializedProperties(obj),
                Logs = new[] { "Applied serialized properties" },
            });
        }
    }
}
#endif

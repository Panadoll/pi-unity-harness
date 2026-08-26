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
        [CliCommand("object_get_data", "Get Unity object data")]
        public static string ObjectGetData([CliArg("instance_id", "Unity instance id")] long instanceId, [CliArg("include_serialized", "Include serialized properties")] bool includeSerialized = true)
        {
            var obj = PiMcpPipelineSupport.ObjectFromId(instanceId);
            if (obj == null)
                throw new ArgumentException("Object not found: " + instanceId);
            return PiMcpPipelineSupport.Ok(new { reference = ToObjectRef(obj), properties = includeSerialized ? PiMcpPipelineSupport.ReadSerializedProperties(obj) : null });
        }

        [CliCommand("object_modify", "Modify Unity object serialized properties")]
        public static string ObjectModify([CliArg("instance_id", "Unity instance id")] long instanceId, [CliArg("properties_json", "JSON object with serialized property values")] string propertiesJson)
        {
            var obj = PiMcpPipelineSupport.ObjectFromId(instanceId);
            if (obj == null)
                throw new ArgumentException("Object not found: " + instanceId);
            Undo.RecordObject(obj, "Modify Object");
            PiMcpPipelineSupport.ApplySerializedProperties(obj, propertiesJson);
            return PiMcpPipelineSupport.Ok(new PiMcpModifyObjectResponse
            {
                Success = true,
                Reference = ToObjectRef(obj),
                Properties = PiMcpPipelineSupport.ReadSerializedProperties(obj),
                Logs = new[] { "Applied serialized properties" },
            });
        }
    }
}
#endif

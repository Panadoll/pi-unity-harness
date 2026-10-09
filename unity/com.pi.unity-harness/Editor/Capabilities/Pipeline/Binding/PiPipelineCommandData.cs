#if PI_UNITY_PIPELINE
using Pi.UnityHarness.Editor.Capabilities.Pipeline.Models;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters
{
    /// <summary>
    /// 跨域命令共用的对象引用投影。领域私有 helper 留在各自命令类。
    /// </summary>
    internal static class PiPipelineCommandData
    {
        public static PiPipelineObjectRefData ToObjectRef(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

            string assetPath = AssetDatabase.GetAssetPath(obj);
            return new PiPipelineObjectRefData
            {
                instanceId = PiPipelineSupport.ObjectId(obj),
                name = obj.name,
                type = obj.GetType().FullName,
                assetPath = assetPath,
                assetGuid = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.AssetPathToGUID(assetPath),
            };
        }
    }
}
#endif

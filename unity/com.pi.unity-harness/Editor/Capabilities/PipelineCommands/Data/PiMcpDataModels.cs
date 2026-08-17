#if PI_UNITY_PIPELINE
using System.Collections.Generic;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands.Data
{
    internal class PiMcpObjectRefData
    {
        public long instanceId;
        public string name;
        public string type;
        public string assetPath;
        public string assetGuid;
    }

    internal class PiMcpComponentData
    {
        public long instanceId;
        public string type;
        public string name;
        public bool enabled;
        public Dictionary<string, object> properties;
    }

    internal class PiMcpGameObjectData
    {
        public long instanceId;
        public string name;
        public string path;
        public string tag;
        public int layer;
        public bool activeSelf;
        public bool activeInHierarchy;
        public string scenePath;
        public List<PiMcpComponentData> components;
        public List<PiMcpGameObjectData> children;
    }

    internal class PiMcpAssetData
    {
        public long instanceId;
        public string name;
        public string type;
        public string assetPath;
        public string assetGuid;
        public string mainAssetType;
    }

    internal class PiMcpSceneData
    {
        public string name;
        public string path;
        public int buildIndex;
        public bool isLoaded;
        public bool isDirty;
        public bool isValid;
        public int rootCount;
    }

    internal class PiMcpSelectionData
    {
        public PiMcpObjectRefData activeObject;
        public PiMcpGameObjectData activeGameObject;
        public long[] instanceIds;
        public string[] assetGuids;
        public List<PiMcpGameObjectData> gameObjects;
    }
}
#endif

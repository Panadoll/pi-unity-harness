#if PI_UNITY_PIPELINE
using System.Collections.Generic;

namespace Pi.UnityHarness.Editor.Capabilities.Pipeline.Models
{
    internal class PiPipelineObjectRefData
    {
        public long instanceId;
        public string name;
        public string type;
        public string assetPath;
        public string assetGuid;
    }

    internal class PiPipelineComponentData
    {
        public long instanceId;
        public string type;
        public string name;
        public bool enabled;
        public Dictionary<string, object> properties;
    }

    internal class PiPipelineGameObjectData
    {
        public long instanceId;
        public string name;
        public string path;
        public string tag;
        public int layer;
        public bool activeSelf;
        public bool activeInHierarchy;
        public string scenePath;
        public List<PiPipelineComponentData> components;
        public List<PiPipelineGameObjectData> children;
    }

    internal class PiPipelineAssetData
    {
        public long instanceId;
        public string name;
        public string type;
        public string assetPath;
        public string assetGuid;
        public string mainAssetType;
    }

    internal class PiPipelineSceneData
    {
        public string name;
        public string path;
        public int buildIndex;
        public bool isLoaded;
        public bool isDirty;
        public bool isValid;
        public int rootCount;
    }

    internal class PiPipelineSelectionData
    {
        public PiPipelineObjectRefData activeObject;
        public PiPipelineGameObjectData activeGameObject;
        public long[] instanceIds;
        public string[] assetGuids;
        public List<PiPipelineGameObjectData> gameObjects;
    }
}
#endif

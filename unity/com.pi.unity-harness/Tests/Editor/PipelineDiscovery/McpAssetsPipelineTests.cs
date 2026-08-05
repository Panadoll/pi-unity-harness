#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpAssetsPipelineTests
    {
        private const string TestFolder = "Assets/PiMcpPipelineTests";

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void AssetsCreateFindGetAndDelete_WorkWithFlatParameters()
        {
            PiMcpPipelineCommands.AssetsCreateFolders("[\"" + TestFolder + "\"]");
            string materialPath = TestFolder + "/Mat.mat";

            string createJson = PiMcpPipelineCommands.AssetsMaterialCreate(materialPath, "Standard");
            var created = JObject.Parse(createJson);
            Assert.AreEqual(materialPath, created.Value<string>("assetPath"));

            string findJson = PiMcpPipelineCommands.AssetsFind("Mat", TestFolder, 10);
            var found = JObject.Parse(findJson);
            Assert.GreaterOrEqual(found.Value<int>("count"), 1);

            string getJson = PiMcpPipelineCommands.AssetsGetData(assetPath: materialPath);
            var data = JObject.Parse(getJson);
            Assert.AreEqual(materialPath, data.Value<string>("assetPath"));

            string deleteJson = PiMcpPipelineCommands.AssetsDelete("[\"" + materialPath + "\"]");
            var deleted = JObject.Parse(deleteJson);
            Assert.IsTrue(deleted["results"][0].Value<bool>("deleted"));
        }
    }
}
#endif

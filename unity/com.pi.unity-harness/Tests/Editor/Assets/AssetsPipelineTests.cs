#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class AssetsPipelineTests
    {
        private const string TestFolder = "Assets/PiPipelineTests";

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void AssetsCreateFindGetAndDelete_WorkWithFlatParameters()
        {
            PiAssetsPipelineCommands.AssetsCreateFolders("[\"" + TestFolder + "\"]");
            string materialPath = TestFolder + "/Mat.mat";

            string createJson = PiAssetsPipelineCommands.AssetsMaterialCreate(materialPath, "Standard");
            var created = JObject.Parse(createJson);
            Assert.AreEqual(materialPath, created.Value<string>("assetPath"));

            string findJson = PiAssetsPipelineCommands.AssetsFind("Mat", TestFolder, 10);
            var found = JObject.Parse(findJson);
            Assert.GreaterOrEqual(found.Value<int>("count"), 1);

            string getJson = PiAssetsPipelineCommands.AssetsGetData(assetPath: materialPath);
            var data = JObject.Parse(getJson);
            Assert.AreEqual(materialPath, data.Value<string>("assetPath"));

            string deleteJson = PiAssetsPipelineCommands.AssetsDelete("[\"" + materialPath + "\"]");
            var deleted = JObject.Parse(deleteJson);
            Assert.IsTrue(deleted["results"][0].Value<bool>("deleted"));
        }
    }
}
#endif

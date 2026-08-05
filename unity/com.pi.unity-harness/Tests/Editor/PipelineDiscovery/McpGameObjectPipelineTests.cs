#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpGameObjectPipelineTests
    {
        private readonly System.Collections.Generic.List<GameObject> created = new System.Collections.Generic.List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
            {
                if (go != null)
                    UnityEngine.Object.DestroyImmediate(go);
            }
            created.Clear();
        }

        [Test]
        public void GameObjectCreateAndFind_ReturnStructuredData()
        {
            string createJson = PiMcpPipelineCommands.GameObjectCreate("PiMcpCreated");
            var createdObject = JObject.Parse(createJson);
            long instanceId = createdObject.Value<long>("instanceId");
            created.Add(Pi.UnityHarness.Editor.Capabilities.PipelineCommands.PiMcpPipelineSupport.ObjectFromId(instanceId) as GameObject);

            Assert.AreEqual("PiMcpCreated", createdObject.Value<string>("name"));
            Assert.AreEqual("PiMcpCreated", createdObject.Value<string>("path"));

            string findJson = PiMcpPipelineCommands.GameObjectFind(instanceId: instanceId, includeComponents: true);
            var found = JObject.Parse(findJson);
            Assert.AreEqual(instanceId, found.Value<long>("instanceId"));
            Assert.IsNotNull(found["components"]);
        }

        [Test]
        public void GameObjectModifyAndComponentList_WorkWithFlatParameters()
        {
            var go = new GameObject("PiMcpModifySource");
            created.Add(go);

            string modifyJson = PiMcpPipelineCommands.GameObjectModify(
                instanceId: Pi.UnityHarness.Editor.Capabilities.PipelineCommands.PiMcpPipelineSupport.ObjectId(go),
                newName: "PiMcpModified",
                setActive: true,
                active: false,
                layer: 0);
            var modified = JObject.Parse(modifyJson);

            Assert.AreEqual("PiMcpModified", modified.Value<string>("name"));
            Assert.IsFalse(modified.Value<bool>("activeSelf"));

            string listJson = PiMcpPipelineCommands.GameObjectComponentListAll(instanceId: Pi.UnityHarness.Editor.Capabilities.PipelineCommands.PiMcpPipelineSupport.ObjectId(go));
            var listed = JObject.Parse(listJson);
            Assert.IsTrue(listed["components"].HasValues);
        }
    }
}
#endif

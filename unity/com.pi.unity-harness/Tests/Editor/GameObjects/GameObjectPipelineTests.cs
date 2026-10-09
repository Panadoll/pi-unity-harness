#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class GameObjectPipelineTests
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
            string createJson = PiGameObjectsPipelineCommands.GameObjectCreate("PiPipelineCreated");
            var createdObject = JObject.Parse(createJson);
            long instanceId = createdObject.Value<long>("instanceId");
            created.Add(Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters.PiPipelineSupport.ObjectFromId(instanceId) as GameObject);

            Assert.AreEqual("PiPipelineCreated", createdObject.Value<string>("name"));
            Assert.AreEqual("PiPipelineCreated", createdObject.Value<string>("path"));

            string findJson = PiGameObjectsPipelineCommands.GameObjectFind(instanceId: instanceId, includeComponents: true);
            var found = JObject.Parse(findJson);
            Assert.AreEqual(instanceId, found.Value<long>("instanceId"));
            Assert.IsNotNull(found["components"]);
        }

        [Test]
        public void GameObjectModifyAndComponentList_WorkWithFlatParameters()
        {
            var go = new GameObject("PiPipelineModifySource");
            created.Add(go);

            string modifyJson = PiGameObjectsPipelineCommands.GameObjectModify(
                instanceId: Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters.PiPipelineSupport.ObjectId(go),
                newName: "PiPipelineModified",
                setActive: true,
                active: false,
                layer: 0);
            var modified = JObject.Parse(modifyJson);

            Assert.AreEqual("PiPipelineModified", modified.Value<string>("name"));
            Assert.IsFalse(modified.Value<bool>("activeSelf"));

            string listJson = PiGameObjectsPipelineCommands.GameObjectComponentListAll(instanceId: Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters.PiPipelineSupport.ObjectId(go));
            var listed = JObject.Parse(listJson);
            Assert.IsTrue(listed["components"].HasValues);
        }
    }
}
#endif

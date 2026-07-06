#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpReflectionObjectTypePipelineTests
    {
        private GameObject created;

        [TearDown]
        public void TearDown()
        {
            if (created != null)
                Object.DestroyImmediate(created);
        }

        [Test]
        public void ReflectionFindAndCallStaticMethod_ReturnExpectedResult()
        {
            var found = JObject.Parse(PiMcpPipelineCommands.ReflectionMethodFind("UnityEngine.Mathf", "Max", 10));
            Assert.Greater(found.Value<int>("count"), 0);

            var called = JObject.Parse(PiMcpPipelineCommands.ReflectionMethodCall("UnityEngine.Mathf", "Max", "[2,5]"));
            Assert.AreEqual(5, called.Value<int>("result"));
        }

        [Test]
        public void ObjectGetAndModify_WorkWithSerializedProperties()
        {
            created = new GameObject("PiMcpObjectSource");
            long id = PiMcpPipelineSupport.ObjectId(created);

            var data = JObject.Parse(PiMcpPipelineCommands.ObjectGetData(id, true));
            Assert.AreEqual("PiMcpObjectSource", data["reference"].Value<string>("name"));
            Assert.IsNotNull(data["properties"]);

            var modified = JObject.Parse(PiMcpPipelineCommands.ObjectModify(id, "{\"m_Name\":\"PiMcpObjectModified\"}"));
            Assert.IsTrue(modified.Value<bool>("Success"));
            Assert.AreEqual("PiMcpObjectModified", created.name);
        }

        [Test]
        public void TypeGetJsonSchema_ReturnsMembersForKnownType()
        {
            var schema = JObject.Parse(PiMcpPipelineCommands.TypeGetJsonSchema("UnityEngine.Transform"));
            Assert.AreEqual("UnityEngine.Transform", schema.Value<string>("type"));
            Assert.AreEqual("Transform", schema["schema"].Value<string>("title"));
            Assert.IsTrue(schema["schema"]["properties"].HasValues);
        }
    }
}
#endif

#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class ReflectionObjectTypePipelineTests
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
            var found = JObject.Parse(PiReflectionPipelineCommands.ReflectionMethodFind("UnityEngine.Mathf", "Max", 10));
            Assert.Greater(found.Value<int>("count"), 0);

            var called = JObject.Parse(PiReflectionPipelineCommands.ReflectionMethodCall("UnityEngine.Mathf", "Max", "[2,5]"));
            Assert.AreEqual(5, called.Value<int>("result"));
        }

        [Test]
        public void ObjectGetAndModify_WorkWithSerializedProperties()
        {
            created = new GameObject("PiPipelineObjectSource");
            long id = PiPipelineSupport.ObjectId(created);

            var data = JObject.Parse(PiObjectPipelineCommands.ObjectGetData(id, true));
            Assert.AreEqual("PiPipelineObjectSource", data["reference"].Value<string>("name"));
            Assert.IsNotNull(data["properties"]);

            var modified = JObject.Parse(PiObjectPipelineCommands.ObjectModify(id, "{\"m_Name\":\"PiPipelineObjectModified\"}"));
            Assert.IsTrue(modified.Value<bool>("Success"));
            Assert.AreEqual("PiPipelineObjectModified", created.name);
        }

        [Test]
        public void TypeGetJsonSchema_ReturnsMembersForKnownType()
        {
            var schema = JObject.Parse(PiTypePipelineCommands.TypeGetJsonSchema("UnityEngine.Transform"));
            Assert.AreEqual("UnityEngine.Transform", schema.Value<string>("type"));
            Assert.AreEqual("Transform", schema["schema"].Value<string>("title"));
            Assert.IsTrue(schema["schema"]["properties"].HasValues);
        }
    }
}
#endif

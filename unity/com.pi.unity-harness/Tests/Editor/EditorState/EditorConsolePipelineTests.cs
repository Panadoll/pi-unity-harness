#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class EditorConsolePipelineTests
    {
        private GameObject created;

        [TearDown]
        public void TearDown()
        {
            Selection.objects = new UnityEngine.Object[0];
            if (created != null)
                UnityEngine.Object.DestroyImmediate(created);
        }

        [Test]
        public void EditorSelectionSetAndGet_ReturnSelectionData()
        {
            created = new GameObject("PiPipelineSelection");
            string idsJson = "[" + Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters.PiPipelineSupport.ObjectId(created) + "]";

            string setJson = PiEditorStatePipelineCommands.EditorSelectionSet(instanceIdsJson: idsJson);
            var set = JObject.Parse(setJson);

            Assert.AreEqual(Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters.PiPipelineSupport.ObjectId(created), set["instanceIds"][0].Value<long>());
            Assert.AreEqual("PiPipelineSelection", set["activeGameObject"].Value<string>("name"));
        }

        [Test]
        public void ConsoleGetAndClear_ReturnStructuredLogs()
        {
            PiConsolePipelineCommands.ConsoleClearLogs();
            Debug.Log("PiPipelineConsoleMessage");

            string logsJson = PiConsolePipelineCommands.ConsoleGetLogs(limit: 10);
            var logs = JObject.Parse(logsJson);
            Assert.GreaterOrEqual(logs.Value<int>("count"), 1);

            string clearJson = PiConsolePipelineCommands.ConsoleClearLogs();
            Assert.IsTrue(JObject.Parse(clearJson).Value<bool>("cleared"));
        }
    }
}
#endif

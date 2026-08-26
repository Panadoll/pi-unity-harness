#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpEditorConsolePipelineTests
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
            created = new GameObject("PiMcpSelection");
            string idsJson = "[" + Pi.UnityHarness.Editor.Capabilities.PipelineCommands.PiMcpPipelineSupport.ObjectId(created) + "]";

            string setJson = PiMcpPipelineCommands.EditorSelectionSet(instanceIdsJson: idsJson);
            var set = JObject.Parse(setJson);

            Assert.AreEqual(Pi.UnityHarness.Editor.Capabilities.PipelineCommands.PiMcpPipelineSupport.ObjectId(created), set["instanceIds"][0].Value<long>());
            Assert.AreEqual("PiMcpSelection", set["activeGameObject"].Value<string>("name"));
        }

        [Test]
        public void ConsoleGetAndClear_ReturnStructuredLogs()
        {
            PiMcpPipelineCommands.ConsoleClearLogs();
            Debug.Log("PiMcpConsoleMessage");

            string logsJson = PiMcpPipelineCommands.ConsoleGetLogs(limit: 10);
            var logs = JObject.Parse(logsJson);
            Assert.GreaterOrEqual(logs.Value<int>("count"), 1);

            string clearJson = PiMcpPipelineCommands.ConsoleClearLogs();
            Assert.IsTrue(JObject.Parse(clearJson).Value<bool>("cleared"));
        }
    }
}
#endif

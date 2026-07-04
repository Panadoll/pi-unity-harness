#if PI_UNITY_PIPELINE
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class PiUnityPipelineExecutorTests
    {
        [Test]
        public void TryParseParameters_ParsesObjectsAndRejectsInvalidJson()
        {
            Assert.IsTrue(PiUnityPipelineCommandExecutor.TryParseParameters("{\"name\":\"agent\"}", out JObject parsed, out string error));
            Assert.AreEqual("agent", (string)parsed["name"]);
            Assert.IsNull(error);

            Assert.IsTrue(PiUnityPipelineCommandExecutor.TryParseParameters(null, out parsed, out error));
            Assert.AreEqual(0, parsed.Count);

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryParseParameters("[1,2]", out parsed, out error));
            StringAssert.Contains("JSON object", error);

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryParseParameters("{bad", out parsed, out error));
            StringAssert.Contains("invalid parametersJson", error);
        }

        [Test]
        public void TryBindParameters_ConvertsProvidedValuesAndAppliesDefaults()
        {
            CommandInfo command = CreateCommand("sample", false);
            JObject parameters = JObject.Parse("{\"name\":\"agent\",\"count\":\"5\",\"enabled\":true}");

            Assert.IsTrue(PiUnityPipelineCommandExecutor.TryBindParameters(command, parameters, out object[] values, out string error));
            Assert.AreEqual(new object[] { "agent", 5, true }, values);
            Assert.IsNull(error);

            Assert.IsTrue(PiUnityPipelineCommandExecutor.TryBindParameters(command, JObject.Parse("{\"name\":\"agent\"}"), out values, out error));
            Assert.AreEqual(3, values[1]);
            Assert.AreEqual(false, values[2]);
        }

        [Test]
        public void TryBindParameters_ReportsMissingRequiredAndConversionErrors()
        {
            CommandInfo command = CreateCommand("sample", false);

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryBindParameters(command, new JObject(), out _, out string error));
            StringAssert.Contains("Required parameter 'name' is missing", error);

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryBindParameters(command, JObject.Parse("{\"name\":\"agent\",\"count\":\"oops\"}"), out _, out error));
            StringAssert.Contains("Failed to convert parameter 'count'", error);
        }

        [Test]
        public void VisibilityHelpers_FilterForbiddenAndRuntimeOnlyCommands()
        {
            Assert.IsFalse(PiUnityPipelineCommandExecutor.IsCommandVisible(CreateCommand("eval", false)));
            StringAssert.Contains("disabled", PiUnityPipelineCommandExecutor.GetForbiddenReason(CreateCommand("eval", false)));

            Assert.IsFalse(PiUnityPipelineCommandExecutor.IsCommandVisible(CreateCommand("runtime_status", true)));
            StringAssert.Contains("runtime-only", PiUnityPipelineCommandExecutor.GetForbiddenReason(CreateCommand("runtime_status", true)));

            Assert.IsTrue(PiUnityPipelineCommandExecutor.IsCommandVisible(CreateCommand("list_tests", false)));
            Assert.IsNull(PiUnityPipelineCommandExecutor.GetForbiddenReason(CreateCommand("list_tests", false)));
        }

        private static CommandInfo CreateCommand(string name, bool runtimeOnly)
        {
            return new CommandInfo(name, "description", false, SampleMethod(), new List<CommandParameterInfo>
            {
                new CommandParameterInfo("name", "Name", true, typeof(string)),
                new CommandParameterInfo("count", "Count", false, typeof(int), 3),
                new CommandParameterInfo("enabled", "Enabled", false, typeof(bool), false),
            }, runtimeOnly);
        }

        private static MethodInfo SampleMethod()
        {
            return typeof(PiUnityPipelineExecutorTests).GetMethod(nameof(SampleCommand), BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static string SampleCommand(string name, int count, bool enabled)
        {
            return name + count + enabled;
        }
    }
}
#endif
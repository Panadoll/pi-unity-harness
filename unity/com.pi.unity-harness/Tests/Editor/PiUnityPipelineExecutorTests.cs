#if PI_UNITY_PIPELINE
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
        public void TryBindParameters_RejectsDiscoveredAssetsCreateFoldersWithoutFoldersJson()
        {
            CommandInfo command = CommandRegistry.DiscoverCommands()
                .Single(item => item.Name == "assets_create_folders"
                    && item.Method.DeclaringType.Assembly == typeof(PiUnityPipelineCommandExecutor).Assembly);

            Assert.That(command.Parameters.Single(parameter => parameter.Name == "folders_json").Required, Is.True,
                "发现 metadata 必须把 folders_json 标为必填，不能靠绑定侧二次推断");

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryBindParameters(command, new JObject(), out _, out string missing));
            StringAssert.Contains("Required parameter 'folders_json' is missing", missing);

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryBindParameters(command, JObject.Parse("{\"folders_json\":null}"), out _, out string nullValue));
            StringAssert.Contains("Required parameter 'folders_json' is missing", nullValue);

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryBindParameters(command, JObject.Parse("{\"folders_json\":\"\"}"), out _, out string empty));
            StringAssert.Contains("Required parameter 'folders_json' is empty", empty);

            JObject listed = JObject.Parse(PiUnityPipelineCommandExecutor.BuildListCommandsResponse("bind"));
            JObject row = ((JArray)listed["result"]["commands"]).Cast<JObject>()
                .Single(item => item.Value<string>("name") == "assets_create_folders");
            CollectionAssert.Contains(
                row["schema"]["required"].Select(token => token.Value<string>()),
                "folders_json");
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

        [Test]
        public void CommandSuggestions_RankDeterministicallyAndOnlyIncludeVisibleCommands()
        {
            var commands = new[]
            {
                CreateCommand("console_clear_logs", false),
                CreateCommand("eval", false),
                CreateCommand("console_clear_hidden", true),
                CreateCommand("console_get_logs", false),
                CreateCommand("assets_refresh", false),
            };

            List<string> forward = PiUnityPipelineCommandExecutor.GetCommandSuggestions("console_clear", commands);
            List<string> reversed = PiUnityPipelineCommandExecutor.GetCommandSuggestions("console_clear", commands.Reverse());

            CollectionAssert.AreEqual(new[] { "console_clear_logs" }, forward);
            CollectionAssert.AreEqual(forward, reversed);
            CollectionAssert.AreEqual(
                new[] { "console_clear_logs" },
                PiUnityPipelineCommandExecutor.GetCommandSuggestions("console_claer_logs", commands));
            string message = PiUnityPipelineCommandExecutor.BuildCommandNotFoundMessage("console_clear", commands);
            StringAssert.Contains("Did you mean: [console_clear_logs]?", message);
            StringAssert.DoesNotContain("Available:", message);
            StringAssert.DoesNotContain("eval", message);
            StringAssert.DoesNotContain("console_clear_hidden", message);
        }

        [Test]
        public void CommandSuggestions_ReturnAtMostFiveAndBoundLongUnknownInput()
        {
            var commands = Enumerable.Range(0, 8)
                .Select(i => CreateCommand("scene_create_" + i, false))
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { "scene_create_0", "scene_create_1", "scene_create_2", "scene_create_3", "scene_create_4" },
                PiUnityPipelineCommandExecutor.GetCommandSuggestions("scene_create", commands));

            string longName = new string('x', 10000);
            string message = PiUnityPipelineCommandExecutor.BuildCommandNotFoundMessage(longName, commands);
            Assert.That(message.Length, Is.LessThan(200));
            StringAssert.DoesNotContain("scene_create_0", message);
            StringAssert.Contains("Use 'pi-unity list-commands'", message);

            string longCommandName = "scene_" + new string('x', 512);
            var longCommand = new[] { CreateCommand(longCommandName, false) };
            CollectionAssert.AreEqual(new[] { longCommandName },
                PiUnityPipelineCommandExecutor.GetCommandSuggestions("scene", longCommand));
            string boundedSuggestionMessage =
                PiUnityPipelineCommandExecutor.BuildCommandNotFoundMessage("scene", longCommand);
            Assert.That(boundedSuggestionMessage.Length, Is.LessThan(200));
            StringAssert.DoesNotContain(longCommandName, boundedSuggestionMessage);
        }

        [Test]
        public void InvokeCommand_BackgroundCommandCompletesOnMainThreadWithoutServerRoundTrip()
        {
            CommandInfo command = CreateCommand("sample", false);

            object result = PiUnityPipelineCommandExecutor.InvokeCommand(command, JObject.Parse("{\"name\":\"agent\"}"));

            Assert.AreEqual("agent3False", result);
        }

        [Test]
        public void InvokeCommand_RejectsPendingTaskInsteadOfBlockingMainThread()
        {
            s_pendingCommand = new TaskCompletionSource<string>();
            MethodInfo method = typeof(PiUnityPipelineExecutorTests).GetMethod(nameof(PendingCommand), BindingFlags.Static | BindingFlags.NonPublic);
            var command = new CommandInfo("pending", "description", false, method, new List<CommandParameterInfo>());

            var ex = Assert.Throws<System.InvalidOperationException>(() => PiUnityPipelineCommandExecutor.InvokeCommand(command, new JObject()));

            StringAssert.Contains("InvokeCommandAsync", ex.Message);
            s_pendingCommand.SetResult("done");
        }

        private static TaskCompletionSource<string> s_pendingCommand;

        private static Task<string> PendingCommand()
        {
            return s_pendingCommand.Task;
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
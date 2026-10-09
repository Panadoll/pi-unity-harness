#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class PipelineCommandDiscoveryTests
    {
        // 原发现名单（c2f3173）。清单本身不是 metadata 复制，缺一条即发现回归。
        private static readonly string[] HarnessRequiredCommands =
        {
            "gameobject_find",
            "gameobject_create",
            "gameobject_destroy",
            "gameobject_duplicate",
            "gameobject_modify",
            "gameobject_set_parent",
            "gameobject_component_add",
            "gameobject_component_destroy",
            "gameobject_component_get",
            "gameobject_component_list_all",
            "gameobject_component_modify",

            "assets_find",
            "assets_find_builtin",
            "assets_get_data",
            "assets_create_folders",
            "assets_copy",
            "assets_move",
            "assets_delete",
            "assets_modify",
            "assets_refresh",
            "assets_material_create",
            "assets_prefab_create",
            "assets_prefab_open",
            "assets_prefab_close",
            "assets_prefab_save",
            "assets_prefab_instantiate",
            "assets_shader_get_data",
            "assets_shader_list_all",

            "scene_create",
            "scene_open",
            "scene_save",
            "scene_get_data",
            "scene_list_opened",
            "scene_set_active",
            "scene_unload",

            "editor_application_get_state",
            "editor_application_set_state",
            "editor_selection_get",
            "editor_selection_set",

            "console_get_logs",
            "console_clear_logs",

            "package_add",
            "package_list",
            "package_remove",
            "package_search",

            "vision_capture_camera",
            "vision_capture_isolated",

            "reflection_method_call",
            "reflection_method_find",

            "object_get_data",
            "object_modify",

            "profiler_start",
            "profiler_stop",
            "profiler_capture_frame",
            "profiler_clear_data",
            "profiler_enable_module",
            "profiler_get_memory_stats",
            "profiler_get_rendering_stats",
            "profiler_get_script_stats",
            "profiler_get_status",
            "profiler_list_modules",
            "profiler_load_data",
            "profiler_save_data",

            "type_get_json_schema",
            "skills_create",
            "skills_generate",

            "input_ready_state",
            "input_wait_ready",
            "input_click",
            "input_sequence",
            "input_raycast",
            "input_probe",

            "uitree_roots",
            "uitree_snapshot",
            "uitree_find",
            "uitree_describe",
            "uitree_text",

            "vision_capture",
            "vision_capture_gameview",
            "vision_capture_async",
            "vision_annotate",
            "vision_build_analysis_request",
            "vision_analyze_image",
            "vision_capture_and_analyze",
            "vision_settings",
            "vision_test_provider",
            "vision_observe",
            "vision_capture_after",
            "vision_annotate_raycast",

            "hot_reload",
            "hot_reload_status",
            "hot_reload_revert_all",

            "pause_point_enable",
            "pause_point_clear",
            "pause_point_status",
            "pause_point_await",

            "input_record_start",
            "input_record_stop",
            "input_record_status",
            "input_replay",
            "input_replay_stop",
            "input_replay_status",
        };

        [Test]
        public void DiscoversRegisteredPipelineCommands()
        {
            var names = CommandRegistry.DiscoverCommands()
                .Select(command => command.Name)
                .ToArray();

            foreach (string name in HarnessRequiredCommands)
                CollectionAssert.Contains(names, name, "Harness 必需命令必须可被发现");
        }

        [Test]
        public void ListCommandsResponse_ReportsResolvedPoliciesAndExcludesForbiddenCommands()
        {
            JObject list = ParseList(PiUnityPipelineCommandExecutor.BuildListCommandsResponse("discovery"));
            var harnessAssembly = typeof(PiUnityPipelineCommandExecutor).Assembly;
            var harnessCommands = CommandRegistry.DiscoverCommands()
                .Where(command => command.Method.DeclaringType.Assembly == harnessAssembly)
                .ToList();
            var commands = ((JArray)list["commands"]).Cast<JObject>().ToList();

            Assert.That(list.Value<bool>("pipelineAvailable"), Is.True);
            Assert.That(list["commands"], Is.Not.Null);

            AssertPolicy(FindHarnessRow(commands, harnessCommands, "scene_get_data"), "read", "sidecar");
            AssertPolicy(FindHarnessRow(commands, harnessCommands, "assets_refresh"), "write", "sidecar");
            AssertPolicy(FindHarnessRow(commands, harnessCommands, "gameobject_find"), "read", "sidecar");
            AssertPolicy(FindHarnessRow(commands, harnessCommands, "gameobject_create"), "write", "default");

            foreach (string name in new[] { "eval", "recompile", "recompile_status" })
                Assert.That(commands.Any(row => row.Value<string>("name") == name), Is.False, name);

            foreach (string name in HarnessRequiredCommands)
            {
                CommandInfo info = harnessCommands.SingleOrDefault(command => command.Name == name);
                if (info == null)
                    continue;
                Assert.That(commands.Any(row => SameCommand(row, info)), Is.True, name + " 必须出现在 list_commands");
            }
        }

        [Test]
        public void ListCommandsResponse_ExcludesRuntimeOnlyAndKeepsRequiredMetadataConsistent()
        {
            JObject list = ParseList(PiUnityPipelineCommandExecutor.BuildListCommandsResponse("discovery"));
            var harnessAssembly = typeof(PiUnityPipelineCommandExecutor).Assembly;
            var commands = ((JArray)list["commands"]).Cast<JObject>().ToList();

            Assert.That(commands.Any(row => row.Value<bool>("runtimeOnly")), Is.False, "runtimeOnly 命令不得出现在 Editor 列表");

            foreach (CommandInfo command in CommandRegistry.DiscoverCommands())
            {
                if (command.RuntimeOnly || command.Method.DeclaringType.Assembly != harnessAssembly)
                    continue;

                JObject row = commands.Single(item => SameCommand(item, command));
                Assert.That(row.Value<bool>("runtimeOnly"), Is.False, command.Name);

                var schemaRequired = ((JArray)row["schema"]["required"] ?? new JArray())
                    .Select(token => token.Value<string>())
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                var parameterRequired = row["parameters"]
                    .Where(parameter => parameter.Value<bool>("required"))
                    .Select(parameter => parameter.Value<string>("name"))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                var discoveredRequired = command.Parameters
                    .Where(parameter => parameter.Required)
                    .Select(parameter => parameter.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();

                CollectionAssert.AreEqual(discoveredRequired, parameterRequired, command.Name + " parameters.required");
                CollectionAssert.AreEqual(discoveredRequired, schemaRequired, command.Name + " schema.required");
            }
        }

        [Test]
        public void RequiredParameters_ExposeConsumerJsonTypeAndDefaults()
        {
            JObject list = ParseList(PiUnityPipelineCommandExecutor.BuildListCommandsResponse("discovery"));
            var commands = ((JArray)list["commands"]).Cast<JObject>().ToList();

            JObject folders = Parameter(commands, "assets_create_folders", "folders_json");
            Assert.That(folders.Value<bool>("required"), Is.True);
            Assert.That(folders.Value<string>("jsonType"), Is.EqualTo("string"));
            Assert.That(folders["defaultValue"].Type, Is.EqualTo(JTokenType.Null));
            CollectionAssert.Contains(SchemaRequired(commands, "assets_create_folders"), "folders_json");

            JObject clickX = Parameter(commands, "input_click", "x");
            Assert.That(clickX.Value<bool>("required"), Is.True);
            Assert.That(clickX.Value<string>("jsonType"), Is.EqualTo("number"));
            Assert.That(clickX["defaultValue"].Type, Is.EqualTo(JTokenType.Null));
            CollectionAssert.Contains(SchemaRequired(commands, "input_click"), "x");

            JObject scenePath = Parameter(commands, "scene_open", "path");
            Assert.That(scenePath.Value<bool>("required"), Is.True);
            Assert.That(scenePath.Value<string>("jsonType"), Is.EqualTo("string"));
            Assert.That(scenePath["defaultValue"].Type, Is.EqualTo(JTokenType.Null));
            CollectionAssert.Contains(SchemaRequired(commands, "scene_open"), "path");

            // 有 C# 默认值的可选参数：列表必须露出真实默认，供绑定侧消费。
            JObject mode = Parameter(commands, "scene_open", "mode");
            Assert.That(mode.Value<bool>("required"), Is.False);
            Assert.That(mode.Value<string>("jsonType"), Is.EqualTo("string"));
            Assert.That(mode.Value<string>("defaultValue"), Is.EqualTo("Single"));

            JObject limit = Parameter(commands, "assets_find", "limit");
            Assert.That(limit.Value<bool>("required"), Is.False);
            Assert.That(limit.Value<string>("jsonType"), Is.EqualTo("integer"));
            Assert.That(limit.Value<int>("defaultValue"), Is.EqualTo(100));

            JObject button = Parameter(commands, "input_click", "button");
            Assert.That(button.Value<bool>("required"), Is.False);
            Assert.That(button.Value<string>("jsonType"), Is.EqualTo("string"));
            Assert.That(button.Value<string>("defaultValue"), Is.EqualTo("left"));
        }

        private static JObject FindHarnessRow(List<JObject> commands, List<CommandInfo> harnessCommands, string name)
        {
            CommandInfo info = harnessCommands.Single(command => command.Name == name);
            return commands.Single(row => SameCommand(row, info));
        }

        private static bool SameCommand(JObject row, CommandInfo info)
        {
            if (row.Value<string>("name") != info.Name)
                return false;
            var rowNames = row["parameters"].Select(parameter => parameter.Value<string>("name"))
                .OrderBy(parameterName => parameterName, StringComparer.Ordinal);
            var infoNames = info.Parameters.Select(parameter => parameter.Name)
                .OrderBy(parameterName => parameterName, StringComparer.Ordinal);
            return rowNames.SequenceEqual(infoNames);
        }

        private static JObject Parameter(List<JObject> commands, string commandName, string parameterName)
        {
            JObject command = commands.Single(row => row.Value<string>("name") == commandName);
            return command["parameters"].Cast<JObject>().Single(parameter => parameter.Value<string>("name") == parameterName);
        }

        private static List<string> SchemaRequired(List<JObject> commands, string commandName)
        {
            JObject command = commands.Single(row => row.Value<string>("name") == commandName);
            return ((JArray)command["schema"]["required"] ?? new JArray())
                .Select(token => token.Value<string>())
                .ToList();
        }

        private static void AssertPolicy(JObject command, string mutability, string source)
        {
            Assert.That(command["policy"].Value<string>("mutability"), Is.EqualTo(mutability), command.Value<string>("name"));
            Assert.That(command["policy"].Value<string>("source"), Is.EqualTo(source), command.Value<string>("name"));
        }

        private static JObject ParseList(string envelope)
        {
            JObject root = JObject.Parse(envelope);
            Assert.That(root.Value<bool>("ok"), Is.True, envelope);
            Assert.That(root.Value<string>("reply_to"), Is.EqualTo("discovery"));
            return (JObject)root["result"];
        }
    }
}
#endif

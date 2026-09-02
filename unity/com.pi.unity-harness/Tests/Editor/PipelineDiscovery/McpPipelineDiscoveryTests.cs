#if PI_UNITY_PIPELINE
using System.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpPipelineDiscoveryTests
    {
        [Test]
        public void DiscoversMigratedMcpCommands()
        {
            var names = CommandRegistry.DiscoverCommands()
                .Select(command => command.Name)
                .ToArray();

            string[] expected =
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

            foreach (var name in expected)
                CollectionAssert.Contains(names, name);
        }
    }
}
#endif

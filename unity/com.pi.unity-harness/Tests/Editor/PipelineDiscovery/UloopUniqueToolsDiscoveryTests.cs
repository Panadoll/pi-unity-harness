#if PI_UNITY_PIPELINE
using System.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    /// <summary>
    /// uloop V3 unique tools must be discoverable through pipeline metadata.
    /// </summary>
    public sealed class UloopUniqueToolsDiscoveryTests
    {
        [Test]
        public void DiscoversHotReloadCommands()
        {
            var names = CommandRegistry.DiscoverCommands().Select(c => c.Name).ToArray();
            CollectionAssert.Contains(names, "hot_reload");
            CollectionAssert.Contains(names, "hot_reload_status");
            CollectionAssert.Contains(names, "hot_reload_revert_all");
        }

        [Test]
        public void DiscoversPausePointCommands()
        {
            var names = CommandRegistry.DiscoverCommands().Select(c => c.Name).ToArray();
            CollectionAssert.Contains(names, "pause_point_enable");
            CollectionAssert.Contains(names, "pause_point_clear");
            CollectionAssert.Contains(names, "pause_point_status");
            CollectionAssert.Contains(names, "pause_point_await");
        }

        [Test]
        public void DiscoversRecordReplayCommands()
        {
            var names = CommandRegistry.DiscoverCommands().Select(c => c.Name).ToArray();
            CollectionAssert.Contains(names, "input_record_start");
            CollectionAssert.Contains(names, "input_record_stop");
            CollectionAssert.Contains(names, "input_record_status");
            CollectionAssert.Contains(names, "input_replay");
            CollectionAssert.Contains(names, "input_replay_stop");
            CollectionAssert.Contains(names, "input_replay_status");
        }

        [Test]
        public void DiscoversRaycastAnnotationCommand()
        {
            var names = CommandRegistry.DiscoverCommands().Select(c => c.Name).ToArray();
            CollectionAssert.Contains(names, "vision_annotate_raycast");
        }
    }
}
#endif

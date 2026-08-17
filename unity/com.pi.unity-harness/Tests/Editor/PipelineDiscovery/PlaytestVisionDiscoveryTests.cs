#if PI_UNITY_PIPELINE
using System.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    /// <summary>
    /// playtest-loop-borrow-plan Phase 1：新感知原语必须通过 pipeline 元数据被发现，
    /// 且不改变既有 vision 命令集合。
    /// </summary>
    public sealed class PlaytestVisionDiscoveryTests
    {
        [Test]
        public void DiscoversPlaytestVisionCommands()
        {
            var names = CommandRegistry.DiscoverCommands()
                .Select(command => command.Name)
                .ToArray();

            CollectionAssert.Contains(names, "vision_observe");
            CollectionAssert.Contains(names, "vision_capture_after");
        }

        [Test]
        public void ExistingVisionCommandsStillDiscovered()
        {
            var names = CommandRegistry.DiscoverCommands()
                .Select(command => command.Name)
                .ToArray();

            CollectionAssert.Contains(names, "vision_capture");
            CollectionAssert.Contains(names, "vision_capture_async");
            CollectionAssert.Contains(names, "vision_annotate");
        }
    }
}
#endif

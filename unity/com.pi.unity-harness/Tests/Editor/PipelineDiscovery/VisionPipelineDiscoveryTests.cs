#if PI_UNITY_PIPELINE
using System.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class VisionPipelineDiscoveryTests
    {
        [Test]
        public void DiscoversVisionCommands()
        {
            var names = CommandRegistry.DiscoverCommands()
                .Select(command => command.Name)
                .ToArray();

            CollectionAssert.Contains(names, "vision_capture");
            CollectionAssert.Contains(names, "vision_capture_gameview");
            CollectionAssert.Contains(names, "vision_annotate");
            CollectionAssert.Contains(names, "vision_build_analysis_request");
            CollectionAssert.Contains(names, "vision_analyze_image");
            CollectionAssert.Contains(names, "vision_capture_and_analyze");
            CollectionAssert.Contains(names, "vision_settings");
            CollectionAssert.Contains(names, "vision_test_provider");
        }
    }
}
#endif

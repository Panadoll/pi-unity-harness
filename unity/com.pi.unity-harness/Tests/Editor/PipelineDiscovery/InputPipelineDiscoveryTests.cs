#if PI_UNITY_PIPELINE
using System.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class InputPipelineDiscoveryTests
    {
        [Test]
        public void DiscoversInputCommands()
        {
            var names = CommandRegistry.DiscoverCommands()
                .Select(command => command.Name)
                .ToArray();

            CollectionAssert.Contains(names, "input_ready_state");
            CollectionAssert.Contains(names, "input_wait_ready");
            CollectionAssert.Contains(names, "input_click");
            CollectionAssert.Contains(names, "input_sequence");
            CollectionAssert.Contains(names, "input_raycast");
            CollectionAssert.Contains(names, "input_probe");
        }
    }
}
#endif

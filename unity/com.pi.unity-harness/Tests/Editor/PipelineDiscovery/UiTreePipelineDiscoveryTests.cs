#if PI_UNITY_PIPELINE
using System.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class UiTreePipelineDiscoveryTests
    {
        [Test]
        public void DiscoversUiTreeCommands()
        {
            var names = CommandRegistry.DiscoverCommands()
                .Select(command => command.Name)
                .ToArray();

            CollectionAssert.Contains(names, "uitree_roots");
            CollectionAssert.Contains(names, "uitree_snapshot");
            CollectionAssert.Contains(names, "uitree_find");
            CollectionAssert.Contains(names, "uitree_describe");
            CollectionAssert.Contains(names, "uitree_text");
        }
    }
}
#endif

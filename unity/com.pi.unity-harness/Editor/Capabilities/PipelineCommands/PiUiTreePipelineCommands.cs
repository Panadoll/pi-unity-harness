#if PI_UNITY_PIPELINE
using Pi.UnityHarness.Editor.Capabilities.UiTree;
using Unity.Pipeline.Commands;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiUiTreePipelineCommands
    {
        [CliCommand("uitree_roots", "List UI tree roots")]
        public static string Roots()
        {
            return HarnessUiTree.ListRootsJson();
        }

        [CliCommand("uitree_snapshot", "Return a UI tree snapshot")]
        public static string Snapshot(
            [CliArg("interactive_only", "Only include interactive nodes")] bool interactiveOnly = true,
            [CliArg("max_depth", "Maximum traversal depth")] int maxDepth = 6,
            [CliArg("limit", "Maximum returned nodes")] int limit = 100,
            [CliArg("root", "Root name filter")] string root = null,
            [CliArg("selector", "Selector filter")] string selector = null,
            [CliArg("include_invisible", "Include invisible nodes")] bool includeInvisible = false)
        {
            return HarnessUiTree.SnapshotJson(interactiveOnly, maxDepth, limit, root, selector, includeInvisible);
        }

        [CliCommand("uitree_find", "Find UI nodes by query")]
        public static string Find(
            [CliArg("query", "Name, text, type, class, or path query")] string query,
            [CliArg("limit", "Maximum returned nodes")] int limit = 50,
            [CliArg("root", "Root name filter")] string root = null,
            [CliArg("include_invisible", "Include invisible nodes")] bool includeInvisible = false)
        {
            return HarnessUiTree.FindJson(query, limit, root, includeInvisible);
        }

        [CliCommand("uitree_describe", "Describe a UI node ref")]
        public static string Describe(
            [CliArg("node_ref", "Node ref from snapshot/find")] string nodeRef,
            [CliArg("max_depth", "Maximum child depth")] int maxDepth = 2,
            [CliArg("limit", "Maximum returned descendants")] int limit = 80)
        {
            return HarnessUiTree.DescribeJson(nodeRef, maxDepth, limit);
        }

        [CliCommand("uitree_text", "Read text from a UI node ref")]
        public static string Text([CliArg("node_ref", "Node ref from snapshot/find")] string nodeRef)
        {
            return HarnessUiTree.TextJson(nodeRef);
        }
    }
}
#endif

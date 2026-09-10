#if PI_UNITY_PIPELINE
using System.Collections;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEditor;
using UnityEngine.TestTools;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpSkillsPackagePipelineTests
    {
        [UnityTest]
        public IEnumerator PackageSearch_ReturnsStructuredSearchResult()
        {
            var task = PiMcpPipelineCommands.PackageSearch("com.unity");
            while (!task.IsCompleted)
                yield return null;
            Assert.IsNull(task.Exception);
            var result = JObject.Parse(task.Result);
            Assert.GreaterOrEqual(result.Value<int>("Count"), 0);
            Assert.IsNotNull(result["Packages"]);
        }
    }
}
#endif

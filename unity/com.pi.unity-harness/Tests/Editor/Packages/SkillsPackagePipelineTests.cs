#if PI_UNITY_PIPELINE
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters;
using UnityEditor;
using UnityEngine.TestTools;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class SkillsPackagePipelineTests
    {
        [UnityTest]
        public IEnumerator PackageSearch_ReturnsStructuredSearchResult()
        {
            var task = PiPackagesPipelineCommands.PackageSearch("com.unity");
            while (!task.IsCompleted)
                yield return null;

            Assert.IsNull(task.Exception);
            var result = JObject.Parse(task.Result);
            Assert.That(result.Value<int>("Count"), Is.EqualTo(result["Packages"]?.Count() ?? -1));
            Assert.That(result["Packages"], Is.TypeOf<JArray>());
            foreach (var package in (JArray)result["Packages"])
            {
                var name = package.Value<string>("Name");
                var displayName = package.Value<string>("DisplayName");
                Assert.That(name, Is.Not.Null.And.Not.Empty);
                Assert.That(name.IndexOf("com.unity", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (displayName != null && displayName.IndexOf("com.unity", System.StringComparison.OrdinalIgnoreCase) >= 0),
                    Is.True, name);
            }
        }
    }
}
#endif

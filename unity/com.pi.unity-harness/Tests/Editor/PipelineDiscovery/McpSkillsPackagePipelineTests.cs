#if PI_UNITY_PIPELINE
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;
using UnityEditor;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpSkillsPackagePipelineTests
    {
        private const string SkillsFolder = "Assets/PiMcpSkillTests";

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(SkillsFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void SkillsCreateAndGenerate_WriteMarkdownAssets()
        {
            var created = JObject.Parse(PiMcpPipelineCommands.SkillsCreate("Test Skill", "desc", "body", SkillsFolder));
            Assert.IsTrue(created.Value<bool>("created"));
            Assert.AreEqual(SkillsFolder + "/Test-Skill.md", created.Value<string>("path"));

            var generated = JObject.Parse(PiMcpPipelineCommands.SkillsGenerate(SkillsFolder, "generated-tools"));
            Assert.IsTrue(generated.Value<bool>("created"));
            Assert.AreEqual(SkillsFolder + "/generated-tools.md", generated.Value<string>("path"));
        }

        [Test]
        public async Task PackageSearch_ReturnsStructuredSearchResult()
        {
            string json = await PiMcpPipelineCommands.PackageSearch("com.unity");
            var result = JObject.Parse(json);
            Assert.GreaterOrEqual(result.Value<int>("Count"), 0);
            Assert.IsNotNull(result["Packages"]);
        }
    }
}
#endif

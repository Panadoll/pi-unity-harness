#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class McpProfilerPipelineTests
    {
        [TearDown]
        public void TearDown()
        {
            PiMcpPipelineCommands.ProfilerStop();
        }

        [Test]
        public void ProfilerStatusStartStopAndStats_ReturnStructuredData()
        {
            var started = JObject.Parse(PiMcpPipelineCommands.ProfilerStart());
            Assert.IsTrue(started.Value<bool>("profilerEnabled"));

            var memory = JObject.Parse(PiMcpPipelineCommands.ProfilerGetMemoryStats());
            Assert.Greater(memory.Value<long>("totalReservedMemory"), 0);

            var rendering = JObject.Parse(PiMcpPipelineCommands.ProfilerGetRenderingStats());
            Assert.IsNotNull(rendering.Value<string>("renderPipeline"));

            var scripting = JObject.Parse(PiMcpPipelineCommands.ProfilerGetScriptStats());
            Assert.Greater(scripting.Value<int>("domainAssemblies"), 0);

            var stopped = JObject.Parse(PiMcpPipelineCommands.ProfilerStop());
            Assert.IsFalse(stopped.Value<bool>("profilerEnabled"));
        }

        [Test]
        public void ProfilerModulesCaptureAndClear_ReturnExpectedFlags()
        {
            var modules = JObject.Parse(PiMcpPipelineCommands.ProfilerListModules());
            Assert.Greater(modules.Value<int>("count"), 0);

            var capture = JObject.Parse(PiMcpPipelineCommands.ProfilerCaptureFrame());
            Assert.IsTrue(capture.Value<bool>("captured"));

            var toggle = JObject.Parse(PiMcpPipelineCommands.ProfilerEnableModule("CPU Usage", true));
            Assert.AreEqual("CPU Usage", toggle.Value<string>("moduleName"));

            var clear = JObject.Parse(PiMcpPipelineCommands.ProfilerClearData());
            Assert.IsTrue(clear.Value<bool>("cleared"));
        }
    }
}
#endif

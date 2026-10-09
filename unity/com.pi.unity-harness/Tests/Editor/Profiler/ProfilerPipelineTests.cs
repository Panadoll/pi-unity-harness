#if PI_UNITY_PIPELINE
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters;

namespace Pi.UnityHarness.Editor.Tests.PipelineDiscovery
{
    public sealed class ProfilerPipelineTests
    {
        [TearDown]
        public void TearDown()
        {
            PiProfilerPipelineCommands.ProfilerStop();
        }

        [Test]
        public void ProfilerStatusStartStopAndStats_ReturnStructuredData()
        {
            var started = JObject.Parse(PiProfilerPipelineCommands.ProfilerStart());
            Assert.IsTrue(started.Value<bool>("profilerEnabled"));

            var memory = JObject.Parse(PiProfilerPipelineCommands.ProfilerGetMemoryStats());
            Assert.Greater(memory.Value<long>("totalReservedMemory"), 0);

            var rendering = JObject.Parse(PiProfilerPipelineCommands.ProfilerGetRenderingStats());
            Assert.IsNotNull(rendering.Value<string>("renderPipeline"));

            var scripting = JObject.Parse(PiProfilerPipelineCommands.ProfilerGetScriptStats());
            Assert.Greater(scripting.Value<int>("domainAssemblies"), 0);

            var stopped = JObject.Parse(PiProfilerPipelineCommands.ProfilerStop());
            Assert.IsFalse(stopped.Value<bool>("profilerEnabled"));
        }

        [Test]
        public void ProfilerModulesCaptureAndClear_ReturnExpectedFlags()
        {
            var modules = JObject.Parse(PiProfilerPipelineCommands.ProfilerListModules());
            Assert.Greater(modules.Value<int>("count"), 0);

            var capture = JObject.Parse(PiProfilerPipelineCommands.ProfilerCaptureFrame());
            Assert.IsTrue(capture.Value<bool>("captured"));

            var toggle = JObject.Parse(PiProfilerPipelineCommands.ProfilerEnableModule("CPU Usage", true));
            Assert.AreEqual("CPU Usage", toggle.Value<string>("moduleName"));

            var clear = JObject.Parse(PiProfilerPipelineCommands.ProfilerClearData());
            Assert.IsTrue(clear.Value<bool>("cleared"));
        }
    }
}
#endif

#if PI_UNITY_PIPELINE
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class PiUnityTestCoordinatorTests
    {
        [Test]
        public void TryParseParameters_PreservesRequestFields()
        {
            string json = "{\"mode\":\"editor\",\"filter\":\"Smoke\",\"filter_type\":\"category\",\"include_explicit\":true,\"timeout\":12}";

            Assert.IsTrue(PiUnityPipelineCommandExecutor.TryParseParameters(json, out JObject parameters, out string error));

            Assert.IsNull(error);
            Assert.AreEqual("editor", (string)parameters["mode"]);
            Assert.AreEqual("Smoke", ReadString(parameters, "filter", string.Empty));
            Assert.AreEqual("category", ReadString(parameters, "filter_type", "testName"));
            Assert.AreEqual(12, ReadInt(parameters, "timeout", 300));
            Assert.AreEqual(true, (bool)parameters["include_explicit"]);
        }

        [Test]
        public void NormalizeMode_AcceptsSupportedModesAndRejectsInvalidMode()
        {
            Assert.AreEqual("editor", NormalizeMode("editor"));
            Assert.AreEqual("editor", NormalizeMode("editmode"));
            Assert.AreEqual("playmode", NormalizeMode("play"));
            Assert.AreEqual("playmode", NormalizeMode("playmode"));
            Assert.AreEqual("all", NormalizeMode("all"));
            Assert.AreEqual("all", NormalizeMode(""));
            Assert.IsNull(NormalizeMode("invalid"));
        }

        [Test]
        public void TryParseParameters_DefaultsToEmptyObjectAndDefaultTimeout()
        {
            Assert.IsTrue(PiUnityPipelineCommandExecutor.TryParseParameters(null, out JObject parameters, out string error));

            Assert.IsNull(error);
            Assert.AreEqual(0, parameters.Count);
            Assert.AreEqual(300, ReadInt(parameters, "timeout", 300));
            Assert.AreEqual(330000, System.Math.Max(1, ReadInt(parameters, "timeout", 300)) * 1000 + 30000);
        }

        [Test]
        public void TryParseParameters_RejectsInvalidJsonAndNonObjectJson()
        {
            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryParseParameters("{bad", out _, out string error));
            StringAssert.Contains("invalid parametersJson", error);

            Assert.IsFalse(PiUnityPipelineCommandExecutor.TryParseParameters("[]", out _, out error));
            StringAssert.Contains("JSON object", error);
        }

        private static string NormalizeMode(string mode)
        {
            return (string)PrivateStatic(nameof(NormalizeMode)).Invoke(null, new object[] { mode });
        }

        private static string ReadString(JObject obj, string name, string defaultValue)
        {
            return (string)PrivateStatic(nameof(ReadString)).Invoke(null, new object[] { obj, name, defaultValue });
        }

        private static int ReadInt(JObject obj, string name, int defaultValue)
        {
            return (int)PrivateStatic(nameof(ReadInt)).Invoke(null, new object[] { obj, name, defaultValue });
        }

        private static MethodInfo PrivateStatic(string name)
        {
            return typeof(PiUnityTestCoordinator).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        }
    }
}
#endif
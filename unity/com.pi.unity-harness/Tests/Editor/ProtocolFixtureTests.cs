using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Protocol;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class ProtocolFixtureTests
    {
        private static string FindFixtureRoot()
        {
            string current = null;
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ProtocolFixtureTests).Assembly);
                if (!string.IsNullOrEmpty(package?.resolvedPath))
                    current = Path.GetFullPath(package.resolvedPath);
            }
            catch { /* PackageInfo unavailable outside the editor context */ }

            if (string.IsNullOrEmpty(current))
                current = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../protocol/fixtures"));
            while (!string.IsNullOrEmpty(current))
            {
                string candidate = Path.Combine(current, "protocol", "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                string parent = Directory.GetParent(current)?.FullName;
                if (parent == current || string.IsNullOrEmpty(parent)) break;
                current = parent;
            }
            Assert.Ignore("protocol fixtures are unavailable in a registry-installed package");
            return string.Empty;
        }

        private static JObject Load(string relative)
        {
            return JObject.Parse(File.ReadAllText(Path.Combine(FindFixtureRoot(), relative)));
        }

        [Test]
        public void ResponseBuilderKeepsPipeCorrelationAndDoesNotEmitMuxId()
        {
            JObject successFixture = Load(Path.Combine("response", "ok_result_null.json"));
            string replyTo = (string)successFixture["wire"]["reply_to"];
            JObject success = JObject.Parse(PipeEnvelope.Success(replyTo, "null"));

            Assert.That((string)success["reply_to"], Is.EqualTo(replyTo));
            Assert.That((bool)success["ok"], Is.True);
            Assert.That(success["result"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(success["id"], Is.Null);

            JObject errorFixture = Load(Path.Combine("response", "err_usage.json"));
            JObject error = JObject.Parse(PipeEnvelope.Error(
                (string)errorFixture["wire"]["reply_to"],
                (string)errorFixture["wire"]["error_type"],
                (string)errorFixture["wire"]["error"]));
            Assert.That((string)error["reply_to"], Is.EqualTo((string)errorFixture["wire"]["reply_to"]));
            Assert.That((bool)error["ok"], Is.False);
            Assert.That((string)error["error_type"], Is.EqualTo((string)errorFixture["expect"]["errorType"]));
            Assert.That((string)error["error"], Is.EqualTo((string)errorFixture["expect"]["message"]));
            Assert.That(error["id"], Is.Null);
        }

        [Test]
        public void ResponseBuilderPreservesUnicodeAndEscapesCorrelation()
        {
            const string replyTo = "回复\"\\id";
            const string message = "错误\n雪";
            JObject error = JObject.Parse(PipeEnvelope.Error(replyTo, "usage", message));

            Assert.That((string)error["reply_to"], Is.EqualTo(replyTo));
            Assert.That((string)error["error"], Is.EqualTo(message));
            Assert.That((string)error["error_type"], Is.EqualTo("usage"));
        }
    }
}

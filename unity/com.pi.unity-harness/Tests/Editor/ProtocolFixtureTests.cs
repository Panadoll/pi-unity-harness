using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class ProtocolFixtureTests
    {
        private static string FindFixtureRoot()
        {
            string current = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../protocol/fixtures"));
            while (!string.IsNullOrEmpty(current))
            {
                string candidate = Path.Combine(current, "protocol", "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                string parent = Directory.GetParent(current)?.FullName;
                if (parent == current) break;
                current = parent;
            }
            Assert.Ignore("protocol fixtures are unavailable in a registry-installed package");
            return string.Empty;
        }

        private static IEnumerable<TestCaseData> FixtureCases()
        {
            string root = FindFixtureRoot();
            foreach (string file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
            {
                if (file.EndsWith("error-precedence.json", StringComparison.Ordinal)) continue;
                yield return new TestCaseData(file);
            }
        }

        [Test, TestCaseSource(nameof(FixtureCases))]
        public void FixtureHasExpectedEnvelopeShape(string path)
        {
            JObject fixture = JObject.Parse(File.ReadAllText(path));
            Assert.That(fixture["description"], Is.Not.Null, path);
            Assert.That(fixture["direction"], Is.Not.Null, path);
            Assert.That(fixture["wire"], Is.TypeOf<JObject>(), path);
            Assert.That(fixture["expect"], Is.TypeOf<JObject>(), path);

            string direction = (string)fixture["direction"];
            JObject wire = (JObject)fixture["wire"];
            if (direction == "response" && !Path.GetFileName(path).Equals("missing_reply_to.json", StringComparison.Ordinal))
            {
                Assert.That(wire["reply_to"], Is.Not.Null, path);
                Assert.That(wire["id"], Is.Null, path);
            }
            if (direction == "mux" && !Path.GetFileName(path).Equals("err_missing_id.json", StringComparison.Ordinal))
            {
                Assert.That(wire["id"], Is.Not.Null, path);
                Assert.That(wire["reply_to"], Is.Null, path);
            }
        }

        [Test]
        public void ResponseHelpersKeepPipeCorrelationField()
        {
            JObject success = JObject.Parse(PiUnityJsonHelper.SuccessJson("fixture-1", "null"));
            Assert.That(success["reply_to"], Is.EqualTo("fixture-1"));
            Assert.That(success["id"], Is.Null);
            Assert.That(success["result"], Is.EqualTo(JValue.CreateNull()));
        }
    }
}

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class PiUnityJsonHelperTests
    {
        [Test]
        public void SuccessJson_CreatesExpectedEnvelope()
        {
            JObject json = JObject.Parse(PiUnityJsonHelper.SuccessJson("req-1", "{\"value\":42}"));

            Assert.AreEqual("req-1", (string)json["reply_to"]);
            Assert.AreEqual(true, (bool)json["ok"]);
            Assert.AreEqual(42, (int)json["result"]["value"]);
        }

        [Test]
        public void ErrorJson_CreatesExpectedEnvelope()
        {
            JObject json = JObject.Parse(PiUnityJsonHelper.ErrorJson("req-2", "usage", "bad input"));

            Assert.AreEqual("req-2", (string)json["reply_to"]);
            Assert.AreEqual(false, (bool)json["ok"]);
            Assert.AreEqual("usage", (string)json["error_type"]);
            Assert.AreEqual("bad input", (string)json["error"]);
        }

        [Test]
        public void JsonString_EscapesSpecialCharactersAndKeepsUnicode()
        {
            string raw = "quote\" backslash\\ newline\n tab\t ctrl" + (char)1 + " unicode雪";
            string json = PiUnityJsonHelper.JsonString(raw);

            Assert.AreEqual(raw, (string)JToken.Parse(json));
            StringAssert.Contains("\\\"", json);
            StringAssert.Contains("\\\\", json);
            StringAssert.Contains("\\n", json);
            StringAssert.Contains("\\t", json);
            StringAssert.Contains("\\u0001", json);
            StringAssert.Contains("雪", json);
        }

        [Test]
        public void EscapeJson_MatchesJsonStringEscaping()
        {
            string raw = "quote\" backslash\\ newline\n bell\b feed\f ctrl" + (char)1;

            Assert.AreEqual(PiUnityJsonHelper.JsonString(raw), "\"" + PiUnityJsonHelper.EscapeJson(raw) + "\"");
            Assert.AreEqual(raw, (string)JToken.Parse("\"" + PiUnityJsonHelper.EscapeJson(raw) + "\""));
        }

        [Test]
        public void EmptyAndNullStrings_FollowHelperContracts()
        {
            Assert.AreEqual("\"\"", PiUnityJsonHelper.JsonString(string.Empty));
            Assert.AreEqual("null", PiUnityJsonHelper.JsonString(null));
            Assert.AreEqual(string.Empty, PiUnityJsonHelper.EscapeJson(string.Empty));
            Assert.AreEqual(string.Empty, PiUnityJsonHelper.EscapeJson(null));
        }
    }
}
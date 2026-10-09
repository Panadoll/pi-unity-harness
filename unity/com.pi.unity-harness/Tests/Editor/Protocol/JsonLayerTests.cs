using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using Pi.UnityHarness.Editor.Protocol;

namespace Pi.UnityHarness.Editor.Tests.Protocol
{
    internal sealed class JsonLayerTests
    {
        [Test]
        public void Success_KeepsReplyToAndDoesNotEmitMuxId()
        {
            JObject json = JObject.Parse(PipeEnvelope.Success("req-1", "{\"value\":42}"));

            Assert.AreEqual("req-1", (string)json["reply_to"]);
            Assert.AreEqual(true, (bool)json["ok"]);
            Assert.AreEqual(42, (int)json["result"]["value"]);
            Assert.IsNull(json["id"]);
            Assert.IsNull(json["text"]);
            Assert.IsNull(json["truncated"]);
        }

        [Test]
        public void Error_KeepsReplyToTypeAndMessage()
        {
            JObject json = JObject.Parse(PipeEnvelope.Error("req-2", "usage", "bad input"));

            Assert.AreEqual("req-2", (string)json["reply_to"]);
            Assert.AreEqual(false, (bool)json["ok"]);
            Assert.AreEqual("usage", (string)json["error_type"]);
            Assert.AreEqual("bad input", (string)json["error"]);
            Assert.IsNull(json["id"]);
        }

        [Test]
        public void Quote_RoundTripsControlsQuotesSlashesAndUnicode()
        {
            var raw = new System.Text.StringBuilder("quote\" slash\\ 雪");
            for (int c = 0; c < 32; c++)
                raw.Append((char)c);

            string value = raw.ToString();
            string json = JsonText.Quote(value);
            Assert.AreEqual(value, (string)JToken.Parse(json));
            for (int c = 0; c < 32; c++)
                Assert.That(json.IndexOf((char)c), Is.LessThan(0));
        }

        [Test]
        public void Escape_MatchesQuotedContentAndNullBecomesEmpty()
        {
            string raw = "quote\" slash\\ \n\r\t\b\f" + (char)1 + "雪";

            Assert.AreEqual(JsonText.Quote(raw), "\"" + JsonText.Escape(raw) + "\"");
            Assert.AreEqual(raw, (string)JToken.Parse("\"" + JsonText.Escape(raw) + "\""));
            Assert.AreEqual("\"\"", JsonText.Quote(string.Empty));
            Assert.AreEqual("null", JsonText.Quote(null));
            Assert.AreEqual(string.Empty, JsonText.Escape(string.Empty));
            Assert.AreEqual(string.Empty, JsonText.Escape(null));
        }
    }
}

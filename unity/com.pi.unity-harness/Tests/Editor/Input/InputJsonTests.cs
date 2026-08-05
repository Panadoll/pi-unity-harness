using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Input;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Tests.Input
{
    public sealed class InputJsonTests
    {
        [Test]
        public void Object_EscapesStringsAndManagesCommas()
        {
            string json = InputJson.Object()
                .String("status", "succeeded")
                .String("text", "quote \" slash \\ newline\n")
                .Bool("ready", true)
                .Number("x", 12.5f)
                .StringArray("keys", new[] { "LeftCtrl", "S" })
                .ToString();

            Assert.That(json, Is.EqualTo(
                "{\"status\":\"succeeded\",\"text\":\"quote \\\" slash \\\\ newline\\n\",\"ready\":true,\"x\":12.5,\"keys\":[\"LeftCtrl\",\"S\"]}"));
        }

        [Test]
        public void Raw_EmbedsNestedJsonWithoutEscaping()
        {
            string json = InputJson.Object()
                .String("status", "succeeded")
                .Raw("dispatch", "{\"attempted\":true,\"dispatched\":false}")
                .ToString();

            Assert.That(json, Is.EqualTo(
                "{\"status\":\"succeeded\",\"dispatch\":{\"attempted\":true,\"dispatched\":false}}"));
        }

        [Test]
        public void StringArray_ReturnsEmptyArrayForNull()
        {
            Assert.That(InputJson.StringArray(null), Is.EqualTo("[]"));
        }

        [Test]
        public void HarnessInputJsonOutputsRemainValidAfterBuilderMigration()
        {
            string ready = HarnessInput.GetReadyStateJson();
            Assert.That(JsonUtility.FromJson<StatusDto>(ready).status, Is.EqualTo("succeeded"));

            string invalidButton = RunOneResult(HarnessInput.ClickJson(1f, 2f, "invalid-button"));
            var failure = JsonUtility.FromJson<StatusDto>(invalidButton);
            Assert.That(failure.status, Is.EqualTo("failed"));
            Assert.That(failure.error_type, Is.EqualTo("usage"));
        }

        [Test]
        public void RunSequenceJson_MissingCoordinatesFailureIsValidJson()
        {
            string result = RunFinalResult(HarnessInput.RunSequenceJson("{\"actions\":[{\"type\":\"click\"}]}"));

            var failure = JsonUtility.FromJson<StatusDto>(result);
            Assert.That(failure.status, Is.EqualTo("failed"));
            Assert.That(failure.error_type, Is.EqualTo("usage"));
            Assert.That(result, Does.Contain("requires 'x' and 'y'"));
        }

        private static string RunOneResult(System.Collections.IEnumerator enumerator)
        {
            Assert.That(enumerator.MoveNext(), Is.True);
            return enumerator.Current as string;
        }

        private static string RunFinalResult(System.Collections.IEnumerator enumerator)
        {
            object last = null;
            while (enumerator.MoveNext())
                last = enumerator.Current;
            return last as string;
        }

        [System.Serializable]
        private sealed class StatusDto
        {
            public string status;
            public string error_type;
        }
    }
}

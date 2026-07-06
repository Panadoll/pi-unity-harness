using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using UnityEngine.TestTools;

namespace Pi.UnityHarness.Editor.Tests
{
    public sealed class PiAbilityCoroutineTests
    {
        [UnityTest]
        public IEnumerator ToTask_ReturnsLastStringYield()
        {
            Task<string> task = PiAbilityCoroutine.ToTask(YieldStrings(), "test_last_yield", 1000);
            yield return WaitForTask(task);

            Assert.IsTrue(task.IsCompletedSuccessfully, GetFailureMessage(task));
            Assert.AreEqual("{\"status\":\"succeeded\"}", task.Result);
        }

        [UnityTest]
        public IEnumerator ToTask_ReturnsNestedLastStringYield()
        {
            Task<string> task = PiAbilityCoroutine.ToTask(YieldNested(), "test_nested_yield", 1000);
            yield return WaitForTask(task);

            Assert.IsTrue(task.IsCompletedSuccessfully, GetFailureMessage(task));
            Assert.AreEqual("{\"nested\":true}", task.Result);
        }

        [UnityTest]
        public IEnumerator ToTask_FaultsOnException()
        {
            Task<string> task = PiAbilityCoroutine.ToTask(Throwing(), "test_exception", 1000);
            yield return WaitForTask(task);

            Assert.IsTrue(task.IsFaulted);
            Assert.IsInstanceOf<InvalidOperationException>(task.Exception.GetBaseException());
            StringAssert.Contains("boom", task.Exception.GetBaseException().Message);
        }

        [UnityTest]
        public IEnumerator ToTask_FaultsOnTimeout()
        {
            Task<string> task = PiAbilityCoroutine.ToTask(NeverEnding(), "test_timeout", 1);
            yield return WaitForTask(task);

            Assert.IsTrue(task.IsFaulted);
            Assert.IsInstanceOf<TimeoutException>(task.Exception.GetBaseException());
            StringAssert.Contains("coroutine exceeded", task.Exception.GetBaseException().Message);
        }

        [UnityTest]
        public IEnumerator ToTask_ReturnsEmptyJsonWhenNoStringYielded()
        {
            Task<string> task = PiAbilityCoroutine.ToTask(YieldNoString(), "test_no_string", 1000);
            yield return WaitForTask(task);

            Assert.IsTrue(task.IsCompletedSuccessfully, GetFailureMessage(task));
            Assert.AreEqual("{}", task.Result);
        }

        private static IEnumerator WaitForTask(Task task)
        {
            double deadline = UnityEditor.EditorApplication.timeSinceStartup + 2.0;
            while (!task.IsCompleted && UnityEditor.EditorApplication.timeSinceStartup < deadline)
                yield return null;
        }

        private static string GetFailureMessage(Task task)
        {
            return task.Exception == null ? "Task did not complete successfully" : task.Exception.GetBaseException().ToString();
        }

        private static IEnumerator YieldStrings()
        {
            yield return null;
            yield return "ignored";
            yield return "{\"status\":\"succeeded\"}";
        }

        private static IEnumerator YieldNested()
        {
            yield return Nested();
        }

        private static IEnumerator Nested()
        {
            yield return null;
            yield return "{\"nested\":true}";
        }

        private static IEnumerator Throwing()
        {
            yield return null;
            throw new InvalidOperationException("boom");
        }

        private static IEnumerator NeverEnding()
        {
            while (true)
                yield return null;
        }

        private static IEnumerator YieldNoString()
        {
            yield return null;
        }
    }
}

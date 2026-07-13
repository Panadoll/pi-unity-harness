using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests.Shared
{
    public sealed class EvalAsyncTaskTests
    {
        [Test]
        public void FromValue_Task_IsAsyncTask()
        {
            var task = Task.FromResult(42);
            var result = PiUnityEvaluator.EvalResult.FromValue(task);
            Assert.That(result.Ok, Is.True);
            Assert.That(result.IsAsyncTask, Is.True);
            Assert.That(result.IsCoroutine, Is.False);
            Assert.That(result.AsyncTask, Is.SameAs(task));
        }

        [Test]
        public void FromValue_NonGenericCompletedTask_Ok()
        {
            var task = Task.CompletedTask;
            var result = PiUnityEvaluator.EvalResult.FromValue(task);
            Assert.That(result.IsAsyncTask, Is.True);
            Assert.That(PiUnityEvaluator.EvalResult.GetTaskResult(task), Is.Null);
        }

        [Test]
        public void GetTaskResult_TaskOfT_ReturnsValue()
        {
            var task = Task.FromResult("hello");
            object value = PiUnityEvaluator.EvalResult.GetTaskResult(task);
            Assert.That(value, Is.EqualTo("hello"));
        }

        [Test]
        public void FromValue_TaskLike_IsAsyncTask()
        {
            var awaitable = new ImmediateAwaitable(7);
            var result = PiUnityEvaluator.EvalResult.FromValue(awaitable);
            Assert.That(result.IsAsyncTask, Is.True);
            Assert.That(result.AsyncTask, Is.Not.Null);
            Assert.That(result.AsyncTask.IsCompleted, Is.True);
            Assert.That(PiUnityEvaluator.EvalResult.GetTaskResult(result.AsyncTask), Is.EqualTo(7));
        }

        [Test]
        public void FromValue_PlainObject_NotAsync()
        {
            var result = PiUnityEvaluator.EvalResult.FromValue(123);
            Assert.That(result.IsAsyncTask, Is.False);
            Assert.That(result.IsCoroutine, Is.False);
            Assert.That(result.Output, Is.EqualTo("123"));
        }

        [Test]
        public void AsyncEvalPump_CompletesTaskResult()
        {
            var pump = new PiUnityAsyncEvalPump();
            string seenId = null;
            bool? seenOk = null;
            string seenText = null;
            string seenType = null;

            pump.Enqueue("r1", Task.FromResult(99), 1000);
            pump.Tick((id, ok, text, typeName, state) =>
            {
                seenId = id;
                seenOk = ok;
                seenText = text;
                seenType = typeName;
            });

            Assert.That(seenId, Is.EqualTo("r1"));
            Assert.That(seenOk, Is.True);
            Assert.That(seenText, Is.EqualTo("99"));
            Assert.That(pump.PendingCount, Is.EqualTo(0));
        }

        [Test]
        public void AsyncEvalPump_TimeoutAbandonsPendingTask()
        {
            var pump = new PiUnityAsyncEvalPump();
            var tcs = new TaskCompletionSource<int>();
            string errorType = null;
            string errorText = null;

            pump.Enqueue("slow", tcs.Task, timeoutMs: 10);
            pump.Tick((id, ok, text, typeName, state) =>
            {
                errorType = typeName;
                errorText = text;
            }, nowUtc: DateTime.UtcNow.AddSeconds(1));

            Assert.That(errorType, Is.EqualTo("timeout"));
            Assert.That(errorText, Does.Contain("TIMEOUT"));
            Assert.That(pump.PendingCount, Is.EqualTo(0));

            // Late completion must not fire again
            bool lateFired = false;
            tcs.SetResult(1);
            pump.Tick((id, ok, text, typeName, state) => { lateFired = true; });
            Assert.That(lateFired, Is.False);
        }

        [Test]
        public void AsyncEvalPump_FaultedTask_ReportsRuntimeError()
        {
            var pump = new PiUnityAsyncEvalPump();
            var faulted = Task.FromException(new InvalidOperationException("boom"));
            string typeName = null;
            string text = null;
            pump.Enqueue("bad", faulted, 1000);
            pump.Tick((id, ok, t, tn, state) =>
            {
                typeName = tn;
                text = t;
            });
            Assert.That(typeName, Is.EqualTo("runtime_error"));
            Assert.That(text, Does.Contain("boom"));
        }

        private sealed class ImmediateAwaitable
        {
            private readonly int _value;

            public ImmediateAwaitable(int value)
            {
                _value = value;
            }

            public ImmediateAwaiter GetAwaiter()
            {
                return new ImmediateAwaiter(_value);
            }
        }

        private sealed class ImmediateAwaiter : INotifyCompletion
        {
            private readonly int _value;

            public ImmediateAwaiter(int value)
            {
                _value = value;
            }

            public bool IsCompleted => true;

            public void OnCompleted(Action continuation)
            {
                continuation?.Invoke();
            }

            public int GetResult()
            {
                return _value;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class StepJobRunnerTests
    {
        private bool _playing;
        private bool _willChange;
        private bool _paused;
        private bool _busy;
        private int _frame;
        private List<bool> _setPlayingCalls;

        [SetUp]
        public void SetUp()
        {
            PiUnityStepJobRunner.ResetForTests();
            _playing = false;
            _willChange = false;
            _paused = false;
            _busy = false;
            _frame = 100;
            _setPlayingCalls = new List<bool>();
            PiUnityPlayModeStepJob.IsPlaying = () => _playing;
            PiUnityPlayModeStepJob.IsPlayingOrWillChange = () => _willChange;
            PiUnityPlayModeStepJob.IsPaused = () => _paused;
            PiUnityPlayModeStepJob.IsBusy = () => _busy;
            PiUnityPlayModeStepJob.FrameCount = () => _frame;
            PiUnityPlayModeStepJob.SetPlaying = value =>
            {
                _setPlayingCalls.Add(value);
                _willChange = value;
            };
        }

        [TearDown]
        public void TearDown()
        {
            PiUnityStepJobRunner.ResetForTests();
            PiUnityPlayModeStepJob.ResetForTests();
        }

        [Test]
        public void EnterPlayMode_CompletesThroughResumeCallbackAfterReload()
        {
            var original = new List<string>();
            var resumed = new List<string>();
            StartEnter("req-1", original);

            CollectionAssert.AreEqual(new[] { true }, _setPlayingCalls);
            Assert.AreEqual(1, PiUnityStepJobRunner.ActiveCount);
            Assert.IsTrue(PiUnityStepJobRunner.HasPendingRequest());

            PiUnityStepJobRunner.BeforeReload();
            PiUnityStepJobRunner.ResumeAfterReload((id, json) => resumed.Add(json));
            _playing = true;

            PiUnityStepJobRunner.Tick();
            _frame += 1;
            PiUnityStepJobRunner.Tick();
            Assert.IsEmpty(resumed, "must wait for settle frames after isPlaying turns true");

            _frame += 2;
            PiUnityStepJobRunner.Tick();

            Assert.IsEmpty(original, "the pre-reload callback belongs to the old domain");
            Assert.AreEqual(1, resumed.Count);
            StringAssert.Contains("\"ok\":true", resumed[0]);
            StringAssert.Contains("\"command\":\"playmode_enter\"", resumed[0]);
            StringAssert.Contains("\"isPlaying\":true", resumed[0]);
            StringAssert.Contains("\"reloads\":1", resumed[0]);
            Assert.AreEqual(0, PiUnityStepJobRunner.ActiveCount);
            Assert.IsFalse(PiUnityStepJobRunner.HasPendingRequest());
        }

        [Test]
        public void RetryWithSameRequestId_ReattachesToRestoredJob()
        {
            var resumed = new List<string>();
            var retry = new List<string>();
            StartEnter("req-retry", new List<string>());
            PiUnityStepJobRunner.BeforeReload();
            PiUnityStepJobRunner.ResumeAfterReload((id, json) => resumed.Add(json));

            StartEnter("req-retry", retry);
            Assert.AreEqual(1, PiUnityStepJobRunner.ActiveCount, "retry must not start a second job");
            CollectionAssert.AreEqual(new[] { true }, _setPlayingCalls, "retry must not toggle PlayMode again");

            _playing = true;
            PiUnityStepJobRunner.Tick();
            _frame += 2;
            PiUnityStepJobRunner.Tick();

            Assert.IsEmpty(resumed);
            Assert.AreEqual(1, retry.Count);
            StringAssert.Contains("\"reloads\":1", retry[0]);
        }

        [Test]
        public void RetryAfterCompletion_ReplaysTheSameResponse()
        {
            var first = new List<string>();
            StartEnter("req-replay", first);
            _playing = true;
            PiUnityStepJobRunner.Tick();
            _frame += 2;
            PiUnityStepJobRunner.Tick();
            Assert.AreEqual(1, first.Count);

            var retry = new List<string>();
            StartEnter("req-replay", retry);

            CollectionAssert.AreEqual(first, retry);
            CollectionAssert.AreEqual(new[] { true }, _setPlayingCalls);
            Assert.AreEqual(0, PiUnityStepJobRunner.ActiveCount);
        }

        [Test]
        public void EnterPlayMode_WaitsWhileCompilingEvenWhenPlaying()
        {
            var completed = new List<string>();
            StartEnter("req-busy", completed);
            _playing = true;
            _busy = true;

            for (int i = 0; i < 5; i++)
            {
                _frame += 5;
                PiUnityStepJobRunner.Tick();
            }
            Assert.IsEmpty(completed);

            _busy = false;
            PiUnityStepJobRunner.Tick();
            _frame += 2;
            PiUnityStepJobRunner.Tick();
            Assert.AreEqual(1, completed.Count);
        }

        [Test]
        public void EnterPlayMode_ReportsRejectionWhenUnityCancelsTransition()
        {
            var completed = new List<string>();
            StartEnter("req-2", completed);

            _willChange = false;
            PiUnityStepJobRunner.Tick();

            Assert.AreEqual(1, completed.Count);
            StringAssert.Contains("\"error_type\":\"playmode_rejected\"", completed[0]);
        }

        [Test]
        public void AlreadyInTargetState_CompletesImmediatelyWithoutToggling()
        {
            _playing = true;
            _willChange = true;
            var completed = new List<string>();

            StartEnter("req-3", completed);

            Assert.IsEmpty(_setPlayingCalls);
            Assert.AreEqual(1, completed.Count);
            StringAssert.Contains("\"alreadyInState\":true", completed[0]);
        }

        [Test]
        public void ExitPlayMode_WaitsUntilTransitionFinishes()
        {
            _playing = true;
            _willChange = true;
            var completed = new List<string>();
            PiUnityStepJobRunner.Start("req-4", "playmode_exit", PiUnityPlayModeStepJob.KindName,
                PiUnityPlayModeStepJob.CreateData(PiUnityPlayModeStepJob.TargetExit), 30000, (id, json) => completed.Add(json));

            CollectionAssert.AreEqual(new[] { false }, _setPlayingCalls);
            PiUnityStepJobRunner.Tick();
            Assert.IsEmpty(completed, "still playing");

            _playing = false;
            PiUnityStepJobRunner.Tick();
            Assert.AreEqual(1, completed.Count);
            StringAssert.Contains("\"isPlaying\":false", completed[0]);
        }

        [Test]
        public void Deadline_FailsWithTimeoutAndClearsState()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            PiUnityStepJobRunner.UtcNow = () => now;
            var completed = new List<string>();
            StartEnter("req-5", completed);

            now = now.AddSeconds(31);
            PiUnityStepJobRunner.Tick();

            Assert.AreEqual(1, completed.Count);
            StringAssert.Contains("\"error_type\":\"timeout\"", completed[0]);
            Assert.IsFalse(PiUnityStepJobRunner.HasPendingRequest());
        }

        [Test]
        public void UnknownKind_IsRejectedWithoutPersisting()
        {
            var completed = new List<string>();
            PiUnityStepJobRunner.Start("req-6", "nope", "no_such_kind", null, 1000, (id, json) => completed.Add(json));

            Assert.AreEqual(1, completed.Count);
            StringAssert.Contains("\"error_type\":\"usage\"", completed[0]);
            Assert.IsFalse(PiUnityStepJobRunner.HasPendingRequest());
        }

        private static void StartEnter(string requestId, List<string> completed)
        {
            PiUnityStepJobRunner.Start(requestId, "playmode_enter", PiUnityPlayModeStepJob.KindName,
                PiUnityPlayModeStepJob.CreateData(PiUnityPlayModeStepJob.TargetEnter), 30000, (id, json) => completed.Add(json));
        }
    }
}

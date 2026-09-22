using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Tests
{
    internal sealed class ReloadOperationRegistryTests
    {
        private sealed class FakeOperation : IReloadAwareOperation
        {
            private readonly Action _before;
            private readonly Action _resume;
            private readonly bool _pending;
            public FakeOperation(string name, bool pending, Action before, Action resume)
            {
                Name = name; _pending = pending; _before = before; _resume = resume;
            }
            public string Name { get; private set; }
            public bool HasPendingRequest() { return _pending; }
            public void BeforeReload() { _before(); }
            public void ResumeAfterReload() { _resume(); }
        }

        [SetUp]
        public void SetUp() { PiUnityReloadOperationRegistry.ResetForTests(); }

        [TearDown]
        public void TearDown() { PiUnityReloadOperationRegistry.ResetForTests(); }

        [Test]
        public void BeforeFailureDoesNotPreventLaterOperation()
        {
            bool secondCalled = false;
            PiUnityReloadOperationRegistry.Register(new FakeOperation("first", false, () => { throw new InvalidOperationException("expected"); }, () => { }));
            PiUnityReloadOperationRegistry.Register(new FakeOperation("second", true, () => secondCalled = true, () => { }));

            PiUnityReloadOperationRegistry.BeforeReloadAll();

            Assert.IsTrue(secondCalled);
            CollectionAssert.AreEqual(new[] { "second" }, PiUnityReloadOperationRegistry.PendingNames());
        }

        [Test]
        public void ResumeFailureDoesNotPreventLaterOperation()
        {
            bool secondCalled = false;
            PiUnityReloadOperationRegistry.Register(new FakeOperation("first", false, () => { }, () => { throw new InvalidOperationException("expected"); }));
            PiUnityReloadOperationRegistry.Register(new FakeOperation("second", false, () => { }, () => secondCalled = true));

            PiUnityReloadOperationRegistry.ResumeAfterReloadAll();

            Assert.IsTrue(secondCalled);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    internal interface IReloadAwareOperation
    {
        string Name { get; }
        bool HasPendingRequest();
        void BeforeReload();
        void ResumeAfterReload();
    }

    internal static class PiUnityReloadOperationRegistry
    {
        private static readonly List<IReloadAwareOperation> Operations = new List<IReloadAwareOperation>();

        internal static void Configure(Action<string, bool, string, string> compileComplete, Action<string, string> testComplete)
        {
            Operations.Clear();
            Register(new CompileReloadOperation(compileComplete));
            Register(new TestReloadOperation(testComplete));
        }

        internal static void Register(IReloadAwareOperation operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));
            Operations.Add(operation);
        }

        internal static void BeforeReloadAll()
        {
            foreach (IReloadAwareOperation operation in Operations.ToArray())
            {
                try
                {
                    operation.BeforeReload();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PiUnityHarness] reload operation before failed (" + operation.Name + "): " + ex.Message);
                }
            }
        }

        internal static void ResumeAfterReloadAll()
        {
            foreach (IReloadAwareOperation operation in Operations.ToArray())
            {
                try
                {
                    operation.ResumeAfterReload();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PiUnityHarness] reload operation resume failed (" + operation.Name + "): " + ex.Message);
                }
            }
        }

        internal static IReadOnlyList<string> PendingNames()
        {
            var names = new List<string>();
            foreach (IReloadAwareOperation operation in Operations)
            {
                try
                {
                    if (operation.HasPendingRequest())
                        names.Add(operation.Name);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[PiUnityHarness] reload operation pending check failed (" + operation.Name + "): " + ex.Message);
                }
            }
            return names;
        }

        internal static void ResetForTests()
        {
            Operations.Clear();
        }

        private sealed class CompileReloadOperation : IReloadAwareOperation
        {
            private readonly Action<string, bool, string, string> _complete;

            public CompileReloadOperation(Action<string, bool, string, string> complete)
            {
                _complete = complete;
            }

            public string Name { get { return "compile"; } }
            public bool HasPendingRequest() { return PiUnityCompileCoordinator.HasPendingRequest(); }
            public void BeforeReload() { }
            public void ResumeAfterReload() { PiUnityCompileCoordinator.ResumeAfterReload(_complete); }
        }

        private sealed class TestReloadOperation : IReloadAwareOperation
        {
            private readonly Action<string, string> _complete;

            public TestReloadOperation(Action<string, string> complete)
            {
                _complete = complete;
            }

            public string Name { get { return "run_tests"; } }
            public bool HasPendingRequest() { return PiUnityTestCoordinator.HasPendingRequestForReload(); }
            public void BeforeReload() { }
            public void ResumeAfterReload() { PiUnityTestCoordinator.ResumeAfterReload(_complete); }
        }
    }
}

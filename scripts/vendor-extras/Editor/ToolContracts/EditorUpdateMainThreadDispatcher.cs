using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;

namespace io.github.hatayama.UnityCliLoop.ToolContracts
{
    /// <summary>
    /// Editor update-loop dispatcher. Does not depend on uloop InternalAPIBridge.
    /// </summary>
    public sealed class EditorUpdateMainThreadDispatcher : IMainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _continuationQueue = new ConcurrentQueue<Action>();
        private int _mainThreadId;

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public void Initialize()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update -= ProcessContinuationQueue;
            EditorApplication.update += ProcessContinuationQueue;
        }

        public void AddContinuation(Action continuation)
        {
            if (continuation == null)
                return;
            _continuationQueue.Enqueue(continuation);
        }

        private void ProcessContinuationQueue()
        {
            while (_continuationQueue.TryDequeue(out Action continuation))
            {
                try
                {
                    continuation?.Invoke();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
            }
        }
    }
}

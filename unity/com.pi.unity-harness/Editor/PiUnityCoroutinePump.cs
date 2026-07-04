using System;
using System.Collections;
using System.Collections.Generic;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// 在主线程逐帧驱动 IEnumerator 协程。
    /// 不依赖 MonoBehaviour，通过 EditorApplication.update 驱动。
    /// 支持串行队列、超时、取消和嵌套 IEnumerator。
    /// </summary>
    internal sealed class PiUnityCoroutinePump : IDisposable
    {
        private const int MaxQueue = 8;
        private const int MaxStepsPerTick = 1000;

        private readonly Queue<CoroutineEntry> _queue = new Queue<CoroutineEntry>();
        private CoroutineEntry _active;
        private bool _disposed;

        public int PendingCount => _queue.Count + (_active.Stack != null ? 1 : 0);

        public bool Enqueue(IEnumerator coroutine, string requestId, Action<bool, string, string> onComplete, int timeoutMs = 60000)
        {
            if (_disposed || coroutine == null)
                return false;

            if (_queue.Count >= MaxQueue)
                return false;

            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 60000;
            var stack = new Stack<IEnumerator>();
            stack.Push(coroutine);

            _queue.Enqueue(new CoroutineEntry
            {
                Stack = stack,
                RequestId = requestId,
                OnComplete = onComplete,
                TimeoutMs = effectiveTimeoutMs,
                StartTime = DateTime.UtcNow,
            });
            return true;
        }

        public void Tick()
        {
            if (_disposed)
                return;

            if (_active.Stack == null && _queue.Count > 0)
            {
                _active = _queue.Dequeue();
                _active.StartTime = DateTime.UtcNow;
            }

            if (_active.Stack == null)
                return;

            double elapsed = (DateTime.UtcNow - _active.StartTime).TotalMilliseconds;
            if (elapsed > _active.TimeoutMs)
            {
                Complete(false, "TIMEOUT: coroutine exceeded " + _active.TimeoutMs + "ms", "timeout");
                return;
            }

            try
            {
                StepActiveStack();
                if (_active.Stack != null && _active.Stack.Count == 0)
                    Complete(true, "(ok)", "void");
            }
            catch (Exception ex)
            {
                Complete(false, "RUNTIME ERROR: coroutine failed: " + ex.GetType().Name + ": " + ex.Message, "runtime_error");
            }
        }

        private void StepActiveStack()
        {
            int steps = 0;
            while (_active.Stack != null && _active.Stack.Count > 0)
            {
                double elapsed = (DateTime.UtcNow - _active.StartTime).TotalMilliseconds;
                if (elapsed > _active.TimeoutMs)
                {
                    Complete(false, "TIMEOUT: coroutine exceeded " + _active.TimeoutMs + "ms", "timeout");
                    return;
                }

                if (steps++ >= MaxStepsPerTick)
                {
                    // 防止纯嵌套且无普通 yield 的协程在单帧内无限展开，下一帧继续。
                    return;
                }

                IEnumerator current = _active.Stack.Peek();
                bool hasNext = current.MoveNext();
                if (!hasNext)
                {
                    _active.Stack.Pop();
                    continue;
                }

                if (current.Current is IEnumerator nested)
                {
                    _active.Stack.Push(nested);
                    continue;
                }

                // 普通 yield 指令（WaitForSeconds/AsyncOperation/null 等）按一帧等待处理。
                return;
            }
        }

        public void Cancel(string requestId)
        {
            if (_active.RequestId == requestId)
            {
                Complete(false, "CANCELLED: client disconnected", "cancelled");
                return;
            }

            if (_queue.Count == 0)
                return;

            var remaining = new Queue<CoroutineEntry>();
            while (_queue.Count > 0)
            {
                var entry = _queue.Dequeue();
                if (entry.RequestId != requestId)
                    remaining.Enqueue(entry);
                else
                    entry.OnComplete?.Invoke(false, "CANCELLED: client disconnected", "cancelled");
            }
            while (remaining.Count > 0)
                _queue.Enqueue(remaining.Dequeue());
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_active.Stack != null)
                Complete(false, "DISPOSED: coroutine pump shutting down", "cancelled");

            while (_queue.Count > 0)
            {
                var entry = _queue.Dequeue();
                entry.OnComplete?.Invoke(false, "DISPOSED: coroutine pump shutting down", "cancelled");
            }

            _active = default(CoroutineEntry);
        }

        private void Complete(bool success, string text, string typeName)
        {
            var entry = _active;
            _active = default(CoroutineEntry);
            entry.OnComplete?.Invoke(success, text, typeName);
        }

        private struct CoroutineEntry
        {
            public Stack<IEnumerator> Stack;
            public string RequestId;
            public Action<bool, string, string> OnComplete;
            public int TimeoutMs;
            public DateTime StartTime;
        }
    }
}

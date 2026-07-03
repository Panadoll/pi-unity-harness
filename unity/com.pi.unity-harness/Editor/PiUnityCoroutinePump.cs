using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// 在主线程逐帧驱动 IEnumerator 协程。
    /// 不依赖 MonoBehaviour，通过 EditorApplication.update 驱动。
    /// 支持队列、超时、和取消。
    /// </summary>
    internal sealed class PiUnityCoroutinePump : IDisposable
    {
        private const int MaxQueue = 8;

        private readonly Queue<CoroutineEntry> _queue = new Queue<CoroutineEntry>();
        private CoroutineEntry _active;
        private bool _disposed;

        public int PendingCount => _queue.Count + (_active.Enumerator != null ? 1 : 0);

        /// <summary>
        /// 将协程加入队列。返回 false 表示队列已满。
        /// </summary>
        public bool Enqueue(IEnumerator coroutine, string requestId, Action<string, string> onComplete, int timeoutMs = 60000)
        {
            if (_disposed || coroutine == null)
                return false;

            if (_queue.Count >= MaxQueue)
                return false;

            _queue.Enqueue(new CoroutineEntry
            {
                Enumerator = coroutine,
                RequestId = requestId,
                OnComplete = onComplete,
                TimeoutMs = timeoutMs,
                StartTime = DateTime.UtcNow,
            });
            return true;
        }

        /// <summary>
        /// 每帧调用一次 (从 EditorApplication.update)。
        /// </summary>
        public void Tick()
        {
            if (_disposed)
                return;

            // 如果没有活跃协程且队列中有等待的，提升一个
            if (_active.Enumerator == null && _queue.Count > 0)
            {
                _active = _queue.Dequeue();
                _active.StartTime = DateTime.UtcNow;
            }

            if (_active.Enumerator == null)
                return;

            // 检查超时
            double elapsed = (DateTime.UtcNow - _active.StartTime).TotalMilliseconds;
            if (elapsed > _active.TimeoutMs)
            {
                Complete("TIMEOUT: coroutine exceeded " + _active.TimeoutMs + "ms", "timeout");
                return;
            }

            // 推进协程
            try
            {
                bool hasNext = _active.Enumerator.MoveNext();

                // 如果 yield 返回值本身也是 IEnumerator，将其嵌套
                if (hasNext && _active.Enumerator.Current is IEnumerator nested)
                {
                    _active.NestedEnumerator = nested;
                }

                // 推进嵌套协程
                if (_active.NestedEnumerator != null)
                {
                    bool nestedNext = _active.NestedEnumerator.MoveNext();
                    if (!nestedNext)
                    {
                        _active.NestedEnumerator = null; // 嵌套协程结束
                    }
                    return; // 下一帧继续推进外层
                }

                if (!hasNext)
                {
                    // 协程正常结束
                    object result = _active.Enumerator.Current;
                    if (result == null)
                    {
                        Complete("(ok)", "void");
                    }
                    else
                    {
                        Complete(result.ToString(), result.GetType().FullName ?? "object");
                    }
                }
            }
            catch (Exception ex)
            {
                Complete("RUNTIME ERROR: coroutine failed: " + ex.GetType().Name + ": " + ex.Message, "runtime_error");
            }
        }

        /// <summary>
        /// 取消指定请求的协程（如果正在执行）。
        /// </summary>
        public void Cancel(string requestId)
        {
            if (_active.RequestId == requestId)
            {
                Complete("CANCELLED: client disconnected", "cancelled");
                return;
            }

            // 从队列中移除（不常见，但覆盖此路径）
            // 由于 Queue 不支持按值删除，这里简单重建队列
            int count = _queue.Count;
            if (count == 0)
                return;

            var remaining = new Queue<CoroutineEntry>();
            while (_queue.Count > 0)
            {
                var entry = _queue.Dequeue();
                if (entry.RequestId != requestId)
                    remaining.Enqueue(entry);
            }
            while (remaining.Count > 0)
                _queue.Enqueue(remaining.Dequeue());
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // 取消所有等待中的协程
            if (_active.Enumerator != null)
            {
                Complete("DISPOSED: coroutine pump shutting down", "cancelled");
            }

            while (_queue.Count > 0)
            {
                var entry = _queue.Dequeue();
                entry.OnComplete?.Invoke(
                    "DISPOSED: coroutine pump shutting down",
                    "cancelled");
            }

            _active = default(CoroutineEntry);
        }

        private void Complete(string text, string typeName)
        {
            var entry = _active;
            _active = default(CoroutineEntry); // 清除活跃引用，让 GC 回收
            entry.OnComplete?.Invoke(text, typeName);
        }

        private struct CoroutineEntry
        {
            public IEnumerator Enumerator;
            public IEnumerator NestedEnumerator;
            public string RequestId;
            public Action<string, string> OnComplete;
            public int TimeoutMs;
            public DateTime StartTime;
        }
    }
}

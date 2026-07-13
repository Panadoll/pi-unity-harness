using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Tracks Task / Task-like eval results until completion or timeout.
    /// Timeout abandons the entry so late completion cannot double-fire.
    /// </summary>
    internal sealed class PiUnityAsyncEvalPump
    {
        private readonly List<Entry> _entries = new List<Entry>();

        public int PendingCount => _entries.Count;

        public void Enqueue(string id, Task task, int timeoutMs, object state = null)
        {
            if (string.IsNullOrEmpty(id) || task == null)
                throw new ArgumentException("id and task are required");

            _entries.Add(new Entry
            {
                Id = id,
                Task = task,
                TimeoutMs = timeoutMs > 0 ? timeoutMs : 60000,
                StartUtc = DateTime.UtcNow,
                State = state,
            });
        }

        /// <summary>
        /// Poll pending tasks. Invokes onComplete(id, success, outputOrError, typeName, state).
        /// </summary>
        public void Tick(Action<string, bool, string, string, object> onComplete, DateTime? nowUtc = null)
        {
            if (_entries.Count == 0 || onComplete == null)
                return;

            DateTime now = nowUtc ?? DateTime.UtcNow;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Abandoned)
                {
                    _entries.RemoveAt(i);
                    continue;
                }

                double elapsedMs = (now - entry.StartUtc).TotalMilliseconds;
                if (!entry.Task.IsCompleted && elapsedMs > entry.TimeoutMs)
                {
                    entry.Abandoned = true;
                    _entries.RemoveAt(i);
                    onComplete(
                        entry.Id,
                        false,
                        "TIMEOUT: async eval exceeded " + entry.TimeoutMs + "ms",
                        "timeout",
                        entry.State);
                    continue;
                }

                if (!entry.Task.IsCompleted)
                    continue;

                _entries.RemoveAt(i);
                CompleteEntry(entry, onComplete);
            }
        }

        public void CancelAll(Action<string, bool, string, string, object> onComplete, string reason)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                entry.Abandoned = true;
                onComplete?.Invoke(entry.Id, false, reason ?? "cancelled", "cancelled", entry.State);
            }
            _entries.Clear();
        }

        private static void CompleteEntry(Entry entry, Action<string, bool, string, string, object> onComplete)
        {
            Task task = entry.Task;
            if (task.IsFaulted)
            {
                Exception ex = task.Exception?.GetBaseException() ?? (Exception)task.Exception;
                string message = ex != null
                    ? "RUNTIME ERROR: " + ex.GetType().Name + ": " + ex.Message
                    : "RUNTIME ERROR: async eval faulted";
                onComplete(entry.Id, false, message, "runtime_error", entry.State);
                return;
            }

            if (task.IsCanceled)
            {
                onComplete(entry.Id, false, "ASYNC EVAL CANCELLED", "cancelled", entry.State);
                return;
            }

            try
            {
                object value = PiUnityEvaluator.EvalResult.GetTaskResult(task);
                // Encode nested value via typeName marker so bridge can re-enter FromValue.
                if (value == null)
                {
                    onComplete(entry.Id, true, "(ok)", "void", entry.State);
                    return;
                }

                var nested = PiUnityEvaluator.EvalResult.FromValue(value);
                if (nested.IsAsyncTask)
                {
                    // Signal caller to re-enqueue nested task via state bag if needed.
                    onComplete(entry.Id, true, "(nested_task)", "nested_task",
                        new NestedAsyncResult { Nested = nested, OriginalState = entry.State });
                    return;
                }

                if (nested.IsCoroutine)
                {
                    onComplete(entry.Id, true, "(coroutine)", "nested_coroutine",
                        new NestedAsyncResult { Nested = nested, OriginalState = entry.State });
                    return;
                }

                if (!nested.Ok)
                {
                    onComplete(entry.Id, false, nested.Error ?? "async nested failed", nested.TypeName ?? "runtime_error", entry.State);
                    return;
                }

                onComplete(entry.Id, true, nested.Output ?? string.Empty, nested.TypeName ?? "object", entry.State);
            }
            catch (Exception ex)
            {
                onComplete(entry.Id, false,
                    "RUNTIME ERROR: " + ex.GetType().Name + ": " + ex.Message,
                    "runtime_error",
                    entry.State);
            }
        }

        internal sealed class NestedAsyncResult
        {
            public PiUnityEvaluator.EvalResult Nested;
            public object OriginalState;
        }

        private sealed class Entry
        {
            public string Id;
            public Task Task;
            public int TimeoutMs;
            public DateTime StartUtc;
            public bool Abandoned;
            public object State;
        }
    }
}

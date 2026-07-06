using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEditor;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    /// <summary>
    /// 将迁移能力中的 IEnumerator API 适配为 pipeline executor 支持的 Task<string>。
    /// </summary>
    internal static class PiAbilityCoroutine
    {
        public static Task<string> ToTask(IEnumerator coroutine, string requestId, int timeoutMs = 60000)
        {
            if (coroutine == null)
                throw new ArgumentNullException(nameof(coroutine));

            var completion = new TaskCompletionSource<string>();
            var pump = new PiUnityCoroutinePump();
            string effectiveRequestId = string.IsNullOrEmpty(requestId) ? Guid.NewGuid().ToString("N") : requestId;
            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 60000;

            EditorApplication.CallbackFunction tick = null;
            Action cleanup = () =>
            {
                if (tick != null)
                    EditorApplication.update -= tick;
                pump.Dispose();
            };

            tick = () =>
            {
                pump.Tick();
                if (completion.Task.IsCompleted)
                    cleanup();
            };

            bool queued = pump.Enqueue(coroutine, effectiveRequestId,
                (success, text, typeName) =>
                {
                    if (success)
                    {
                        completion.TrySetResult(string.IsNullOrEmpty(text) ? "{}" : text);
                        return;
                    }

                    if (typeName == "timeout")
                    {
                        completion.TrySetException(new TimeoutException(text ?? "coroutine timed out"));
                        return;
                    }

                    completion.TrySetException(new InvalidOperationException(text ?? "coroutine failed"));
                }, effectiveTimeoutMs, true);

            if (!queued)
            {
                pump.Dispose();
                completion.TrySetException(new InvalidOperationException("coroutine queue full"));
                return completion.Task;
            }

            EditorApplication.update += tick;
            tick();
            return completion.Task;
        }
    }
}

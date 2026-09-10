#if PI_UNITY_PIPELINE
using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiUloopToolRunner
    {
        public static async Task<string> Run<T>(Func<CancellationToken, Task<T>> action, int timeoutMs)
        {
            using var innerCts = new CancellationTokenSource();
            using var delayCts = new CancellationTokenSource();

            Task<T> task = action(innerCts.Token);
            Task delayTask = Task.Delay(Math.Max(1, timeoutMs), delayCts.Token);

            Task completed = await Task.WhenAny(task, delayTask);
            if (completed != task)
            {
                innerCts.Cancel();
                task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

                var timeoutResponse = new JObject
                {
                    ["Success"] = false,
                    ["Message"] = "Timed out.",
                    ["success"] = false,
                    ["message"] = "Timed out."
                };
                return timeoutResponse.ToString(Formatting.None);
            }

            delayCts.Cancel();
            return JsonConvert.SerializeObject(await task);
        }
    }
}
#endif

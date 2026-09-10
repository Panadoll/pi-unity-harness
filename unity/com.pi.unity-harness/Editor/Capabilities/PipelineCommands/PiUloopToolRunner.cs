#if PI_UNITY_PIPELINE
using System;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static class PiUloopToolRunner
    {
        public static async Task<string> Run<T>(Func<Task<T>> action, int timeoutMs)
        {
            Task<T> task = action();
            Task completed = await Task.WhenAny(task, Task.Delay(Math.Max(1, timeoutMs)));
            if (completed != task)
                return JsonConvert.SerializeObject(new { success = false, message = "Timed out." });
            try
            {
                return JsonConvert.SerializeObject(await task);
            }
            catch (Exception ex)
            {
                string msg = ex is AggregateException agg && agg.InnerException != null
                    ? agg.InnerException.Message
                    : ex.Message;
                return JsonConvert.SerializeObject(new { success = false, message = msg });
            }
        }
    }
}
#endif

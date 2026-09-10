#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.PipelineCommands.Data;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Capabilities.PipelineCommands
{
    internal static partial class PiMcpPipelineCommands
    {
        [CliCommand("console_get_logs", "Get Unity console logs")]
        public static string ConsoleGetLogs([CliArg("limit", "Maximum logs")] int limit = 100, [CliArg("level", "Log level filter")] string level = null)
        {
            var logs = PiUnityConsoleLogBuffer.Get(limit, level);
            return PiMcpPipelineSupport.Ok(new { count = logs.Count, logs });
        }

        [CliCommand("console_clear_logs", "Clear Unity console logs")]
        public static string ConsoleClearLogs()
        {
            PiUnityConsoleLogBuffer.Clear();
            var clearMethod = Type.GetType("UnityEditor.LogEntries,UnityEditor")?.GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            clearMethod?.Invoke(null, null);
            return PiMcpPipelineSupport.Ok(new { cleared = true });
        }
    }
}
#endif

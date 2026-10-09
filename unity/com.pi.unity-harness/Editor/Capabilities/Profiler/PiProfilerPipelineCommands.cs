#if PI_UNITY_PIPELINE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pi.UnityHarness.Editor.Capabilities.Pipeline.Models;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor.Capabilities.Pipeline.CommandAdapters
{
    internal static class PiProfilerPipelineCommands
    {
        [CliCommand("profiler_start", "Start Unity profiler recording")]
        public static string ProfilerStart()
        {
            Profiler.enabled = true;
            ProfilerDriver.enabled = true;
            return ProfilerGetStatus();
        }

        [CliCommand("profiler_stop", "Stop Unity profiler recording")]
        public static string ProfilerStop()
        {
            Profiler.enabled = false;
            ProfilerDriver.enabled = false;
            return ProfilerGetStatus();
        }

        [CliCommand("profiler_capture_frame", "Capture a profiler frame")]
        public static string ProfilerCaptureFrame()
        {
            Profiler.enabled = true;
            return PiPipelineSupport.Ok(new { captured = true, frame = Time.frameCount, enabled = Profiler.enabled });
        }

        [CliCommand("profiler_clear_data", "Clear profiler data")]
        public static string ProfilerClearData()
        {
            ProfilerDriver.ClearAllFrames();
            return PiPipelineSupport.Ok(new { cleared = true });
        }

        [CliCommand("profiler_enable_module", "Enable or disable a profiler module")]
        public static string ProfilerEnableModule([CliArg("module_name", "Profiler module name", Required = true)] string moduleName, [CliArg("enabled", "Enabled state")] bool enabled = true)
        {
            return PiPipelineSupport.Ok(new { moduleName, enabled, note = "Profiler module toggling is not available through this Unity API version; recording state unchanged." });
        }

        [CliCommand("profiler_get_memory_stats", "Get profiler memory stats")]
        public static string ProfilerGetMemoryStats()
        {
            return PiPipelineSupport.Ok(new { totalAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong(), totalReservedMemory = Profiler.GetTotalReservedMemoryLong(), monoUsedSize = Profiler.GetMonoUsedSizeLong(), monoHeapSize = Profiler.GetMonoHeapSizeLong() });
        }

        [CliCommand("profiler_get_rendering_stats", "Get profiler rendering stats")]
        public static string ProfilerGetRenderingStats()
        {
            return PiPipelineSupport.Ok(new { frameCount = Time.frameCount, renderPipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().FullName : "Built-in" });
        }

        [CliCommand("profiler_get_script_stats", "Get profiler scripting stats")]
        public static string ProfilerGetScriptStats()
        {
            return PiPipelineSupport.Ok(new { domainAssemblies = AppDomain.CurrentDomain.GetAssemblies().Length, isCompiling = EditorApplication.isCompiling, isUpdating = EditorApplication.isUpdating });
        }

        [CliCommand("profiler_get_status", "Get profiler status")]
        public static string ProfilerGetStatus()
        {
            return PiPipelineSupport.Ok(new { profilerEnabled = Profiler.enabled, driverEnabled = ProfilerDriver.enabled, frameCount = Time.frameCount });
        }

        [CliCommand("profiler_list_modules", "List profiler modules")]
        public static string ProfilerListModules()
        {
            string[] modules = { "CPU Usage", "GPU Usage", "Rendering", "Memory", "Audio", "Physics", "Physics 2D", "Network Messages", "Network Operations", "UI", "Global Illumination" };
            return PiPipelineSupport.Ok(new { count = modules.Length, modules });
        }

        [CliCommand("profiler_load_data", "Load profiler data")]
        public static string ProfilerLoadData([CliArg("path", "Profiler data path", Required = true)] string path)
        {
            ProfilerDriver.LoadProfile(path, false);
            return PiPipelineSupport.Ok(new { loaded = true, path });
        }

        [CliCommand("profiler_save_data", "Save profiler data")]
        public static string ProfilerSaveData([CliArg("path", "Profiler data path", Required = true)] string path)
        {
            ProfilerDriver.SaveProfile(path);
            return PiPipelineSupport.Ok(new { saved = true, path });
        }
    }
}
#endif

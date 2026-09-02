using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    [InitializeOnLoad]
    internal static partial class PiUnityBridge
    {
        private const string NativeDll = "pi_unity_harness_native";
        private const int NativeProtocolVersion = 1;
        private const int ManagedStateInitializing = 0;
        private const int ManagedStateReady = 1;
        private const int ManagedStateReloading = 2;
        private const int ManagedStateQuitting = 3;
        private const int NativeInitialBufferSize = 64 * 1024;
        private const int MaxRequestsPerUpdate = 16;
        private const double HeartbeatStaleSeconds = 2.0d;
        private const double StaleWakeIntervalSeconds = 1.0d;
        /// <summary>Background-thread native heartbeat interval. Keeps broker alive while modal dialogs block the main thread.</summary>
        private const double BackgroundHeartbeatIntervalSeconds = 0.25d;
        private const string SessionKey_Generation = "PiUnityHarness_Generation";
        private const string SessionKey_Token = "PiUnityHarness_Token";
        private const string SessionKey_Pipe = "PiUnityHarness_Pipe";
        private const string SessionKey_RunInBackgroundCaptured = "PiUnityHarness_RunInBackgroundCaptured";
        private const string SessionKey_RunInBackgroundOriginal = "PiUnityHarness_RunInBackgroundOriginal";
        private const uint WmNull = 0x0000;
        private const int SwRestore = 9;

        [Serializable]
        private sealed class NativeRequest
        {
            public string id;
            public string type;
            public string token;
            public int timeoutMs;
            public ExecutePayload payload;
        }

        [Serializable]
        private sealed class ExecutePayload
        {
            public string code;
            public string filePath;
            public string name;
            public string parametersJson;
            public int maxDepth = -1;
            public int maxNodes = -1;
            public int logLimit = -1;
            public string logLevel;
            public bool includeComponents;
        }

        [Serializable]
        private sealed class BridgeInfo
        {
            public string project;
            public int pid;
            public string pipe;
            public string token;
            public long generation;
            public string statePlaneName;
        }

        private struct PendingLine
        {
            public string Line;
            public long PolledAtTicks;
        }

        private sealed class RequestTiming
        {
            public long PolledAtTicks;
            public long DequeuedAtTicks;
            public double ValidateMs = -1;
            public double EvalMs = -1;

            public string ToJsonFragment()
            {
                double pollToUpdateMs = TicksToMs(DequeuedAtTicks - PolledAtTicks);
                double managedTotalMs = TicksToMs(Stopwatch.GetTimestamp() - PolledAtTicks);
                StringBuilder sb = new StringBuilder(128);
                sb.Append("{\"nativePollToUpdateMs\":").Append(FormatMs(pollToUpdateMs));
                if (ValidateMs >= 0)
                    sb.Append(",\"validateMs\":").Append(FormatMs(ValidateMs));
                if (EvalMs >= 0)
                    sb.Append(",\"evalMs\":").Append(FormatMs(EvalMs));
                sb.Append(",\"managedTotalMs\":").Append(FormatMs(managedTotalMs));
                sb.Append('}');
                return sb.ToString();
            }

            public static double TicksToMs(long ticks)
            {
                return ticks * 1000.0 / Stopwatch.Frequency;
            }

            private static string FormatMs(double ms)
            {
                return ms.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static readonly ConcurrentQueue<PendingLine> PendingRequests = new ConcurrentQueue<PendingLine>();
        private static Thread s_nativePumpThread;
        private static CancellationTokenSource s_nativePumpCts;
        private static PiUnityEvaluator s_evaluator;
        private static PiUnityCoroutinePump s_pump;
        private static PiUnityAsyncEvalPump s_asyncEvalPump;
        private static byte[] s_nativeBuffer;
        private static bool s_started;
        private static string s_projectPath;
        private static string s_pipeName;
        private static string s_token;
        private static long s_generation;
        private static IntPtr s_mainWindowHandle;
        private static uint s_mainThreadId;
        private static double s_lastHeartbeatAt;
        /// <summary>Updated only from main-thread OnUpdate. Used to detect main-thread stalls (modal dialogs).</summary>
        private static long s_lastMainThreadPumpUtcTicks;
        /// <summary>Updated from main-thread and background heartbeats. For diagnostics only.</summary>
        private static long s_lastNativeHeartbeatUtcTicks;
        private static bool s_runInBackgroundApplied;
        private static string s_lastMainThreadEditorStatus = "editing;focus=unknown;window=normal";

        static PiUnityBridge()
        {
            if (!ShouldRunInCurrentProcess())
                return;

            Start();
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;
        }

        private static void Start()
        {
            if (s_started || !ShouldRunInCurrentProcess())
                return;

            try
            {
                s_started = true;
                s_projectPath = DetectProjectPath();
                s_pipeName = GetOrCreatePipeName(s_projectPath);
                s_token = GetOrCreateToken();
                s_generation = NextGeneration();
                s_mainThreadId = GetCurrentThreadId();
                s_mainWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
                EnableRunInBackground();
                s_evaluator = new PiUnityEvaluator();
                s_pump = new PiUnityCoroutinePump();
                s_asyncEvalPump = new PiUnityAsyncEvalPump();
                s_nativeBuffer = new byte[NativeInitialBufferSize];

                if (pi_unity_init(Utf8(s_projectPath), ByteLen(s_projectPath), Utf8(s_pipeName), ByteLen(s_pipeName), Utf8(s_token), ByteLen(s_token), NativeProtocolVersion) != 0)
                    UnityEngine.Debug.LogError("[PiUnityHarness] native broker init failed");

                PublishManagedState(ManagedStateInitializing, "starting");
                WriteBridgeInfo();
                EditorApplication.update += OnUpdate;
                long nowTicks = DateTime.UtcNow.Ticks;
                Interlocked.Exchange(ref s_lastMainThreadPumpUtcTicks, nowTicks);
                Interlocked.Exchange(ref s_lastNativeHeartbeatUtcTicks, nowTicks);
                StartNativePumpThread();
                PublishManagedState(ManagedStateReady, CurrentEditorStatus());
                EditorApplication.delayCall += ForceReadyAfterStartup;
                UnityEngine.Debug.Log("[PiUnityHarness] started on pipe " + s_pipeName + " generation=" + s_generation);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[PiUnityHarness] start failed: " + ex);
                RestoreRunInBackground();
                s_started = false;
            }
        }

        private static void Stop()
        {
            if (!s_started)
                return;
            s_started = false;
            StopNativePumpThread();
            EditorApplication.update -= OnUpdate;
            PublishManagedState(ManagedStateQuitting, "quitting");
            RestoreRunInBackground();
            CancelAllPendingAsyncEvals("DISPOSED: bridge shutting down");
            s_pump?.Dispose();
            s_pump = null;
            s_asyncEvalPump = null;
            try
            {
                pi_unity_shutdown();
            }
            catch
            {
            }
        }

        private static void OnBeforeReload()
        {
            PublishManagedState(ManagedStateReloading, "reloading");
            StopNativePumpThread();
            CancelAllPendingAsyncEvals("CANCELLED: domain reload");
            s_pump?.Dispose();
            s_pump = null;
            s_asyncEvalPump = null;
        }

        private static void OnAfterReload()
        {
            Start();
            // Complete compile requests pending before domain reload (responses can only be sent after Start)
            PiUnityCompileCoordinator.ResumeAfterReload(CompleteCompileResult);
            PiUnityTestCoordinator.ResumeAfterReload(CompleteJson);
        }

        private static void ForceReadyAfterStartup()
        {
            if (!s_started)
                return;
            PublishManagedState(ManagedStateReady, CurrentEditorStatus());
        }

        private static void OnUpdate()
        {
            if (!s_started)
                return;

            double now = EditorApplication.timeSinceStartup;
            if (now - s_lastHeartbeatAt >= 0.25d)
            {
                s_lastHeartbeatAt = now;
                PublishHeartbeat();
            }

            // Drive the coroutine pump and async eval
            s_pump?.Tick();
            TickPendingAsyncEvals();

            int processed = 0;
            while (processed < MaxRequestsPerUpdate && PendingRequests.TryDequeue(out PendingLine pending))
            {
                HandleRequestLine(pending);
                processed++;
            }

            if (!PendingRequests.IsEmpty ||
                (s_pump != null && s_pump.PendingCount > 0) ||
                (s_asyncEvalPump != null && s_asyncEvalPump.PendingCount > 0))
            {
                EditorApplication.QueuePlayerLoopUpdate();
                InternalEditorUtility.RepaintAllViews();
            }
        }
        private static void HandleRequestLine(PendingLine pending)
        {
            RequestTiming timing = new RequestTiming
            {
                PolledAtTicks = pending.PolledAtTicks,
                DequeuedAtTicks = Stopwatch.GetTimestamp(),
            };

            NativeRequest request;
            try
            {
                request = JsonUtility.FromJson<NativeRequest>(pending.Line);
            }
            catch (Exception ex)
            {
                CompleteError("", "request_parse_failed: " + ex.Message, "usage");
                return;
            }

            if (request == null || string.IsNullOrEmpty(request.id) || string.IsNullOrEmpty(request.type))
            {
                CompleteError(request != null ? request.id : "", "missing_request_id_or_type", "usage");
                return;
            }

            switch (request.type)
            {
                case "execute_code":
                    ExecuteCode(request, timing);
                    return;
                case "execute_file":
                    ExecuteFile(request, timing);
                    return;
                case "validate_execute_code":
                    ValidateExecuteCode(request, timing);
                    return;
                case "validate_execute_file":
                    ValidateExecuteFile(request, timing);
                    return;
                case "validate_code":
                    ValidateCode(request);
                    return;
                case "validate_file":
                    ValidateFile(request);
                    return;
                case "recompile":
                    Recompile(request);
                    return;
                case "context_snapshot":
                    ContextSnapshot(request);
                    return;
                case "list_commands":
                    ListPipelineCommands(request);
                    return;
                case "command":
                    ExecutePipelineCommand(request);
                    return;
                default:
                    CompleteError(request.id, "unsupported_request_type", "usage");
                    return;
            }
        }

        private static void ContextSnapshot(NativeRequest request)
        {
            ExecutePayload payload = request.payload;
            int maxDepth = payload != null && payload.maxDepth >= 0
                ? payload.maxDepth
                : PiUnityContextSnapshot.DefaultMaxDepth;
            int maxNodes = payload != null && payload.maxNodes > 0
                ? payload.maxNodes
                : PiUnityContextSnapshot.DefaultMaxNodes;
            int logLimit = payload != null && payload.logLimit >= 0
                ? payload.logLimit
                : PiUnityContextSnapshot.DefaultLogLimit;

            try
            {
                string snapshot = PiUnityContextSnapshot.BuildJson(
                    maxDepth,
                    maxNodes,
                    logLimit,
                    payload != null ? payload.logLevel : null,
                    payload != null && payload.includeComponents);
                CompleteJson(request.id, PiUnityJsonHelper.SuccessJson(request.id, snapshot));
            }
            catch (Exception ex)
            {
                CompleteError(request.id, "context_snapshot_failed: " + ex.Message, "snapshot_error");
            }
        }

        private static void Recompile(NativeRequest request)
        {
            UnityEngine.Debug.Log("[PiUnityHarness] recompile request id=" + request.id);

            // 统一走守卫：确保 PlayMode 已安全退出后再触发 Domain Reload，避免 Playable 销毁回调
            // 访问上一域 GC handle 导致编辑器 SIGSEGV 闪退
            PiUnityCompileCoordinator.RequestRecompile(request.id, CompleteCompileResult);
        }

        private static void CompleteCompileResult(string compileId, bool success, string resultText, string errorSummary)
        {
            if (success)
            {
                CompleteJson(compileId, PiUnityJsonHelper.EvalResultJson(
                    compileId, resultText ?? "compilation_succeeded", "compile_status"));
                return;
            }

            string errorType = string.Equals(resultText, "busy", StringComparison.Ordinal) ? "busy"
                : string.Equals(resultText, "playmode_exit_timeout", StringComparison.Ordinal) ? "playmode_exit_timeout"
                : "compile_error";
            CompleteError(compileId, errorSummary ?? "Compilation failed", errorType);
        }

        // --- Pipeline command bridge ---

        private static void ListPipelineCommands(NativeRequest request)
        {
            CompleteJson(request.id, PiUnityPipelineCommandExecutor.BuildListCommandsResponse(request.id));
        }

        private static void ExecutePipelineCommand(NativeRequest request)
        {
            if (request.payload == null)
            {
                CompleteError(request.id, "missing_payload", "usage");
                return;
            }

            PiUnityPipelineCommandExecutor.ExecuteCommand(
                request.id,
                request.payload.name,
                request.payload.parametersJson,
                request.timeoutMs,
                CompleteJson);
        }
        private static void CompleteError(string id, string error, string errorType = "usage")
        {
            CompleteJson(id, PiUnityJsonHelper.ErrorJson(id, errorType, error));
        }

        private static void CompleteJson(string id, string json)
        {
            byte[] idBytes = Utf8(id ?? string.Empty);
            byte[] responseBytes = Utf8(json ?? string.Empty);
            pi_unity_complete_request(idBytes, idBytes.Length, responseBytes, responseBytes.Length);
        }
    }
}

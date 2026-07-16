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
using UnityEngine.SceneManagement;

namespace Pi.UnityHarness.Editor
{
    [InitializeOnLoad]
    internal static class PiUnityBridge
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

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pi_unity_init(
            byte[] project, int projectLen,
            byte[] pipe, int pipeLen,
            byte[] token, int tokenLen,
            int protocolVersion);

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pi_unity_shutdown();

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pi_unity_set_managed_state(
            int state, long generation, byte[] editorStatus, int editorStatusLen);

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pi_unity_managed_heartbeat(long generation);

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int pi_unity_poll_request(byte[] buffer, int bufferLen, ref int outRequiredLen);

        [DllImport(NativeDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void pi_unity_complete_request(byte[] id, int idLen, byte[] response, int responseLen);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

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
        private static int s_modalDialogPresent; // 0/1 via Interlocked

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
            // 完成域重载前挂起的编译请求（Start 后才可发送响应）
            PiUnityCompileCoordinator.FinalizeAfterReload((id, success, result, errorSummary) =>
            {
                if (success)
                {
                    string payload =
                        "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                        ",\"ok\":true,\"result\":{\"output\":" + PiUnityJsonHelper.JsonString(result ?? "compilation_succeeded") +
                        ",\"typeName\":\"compile_status\"}}";
                    CompleteJson(id, payload);
                }
                else
                {
                    string payload =
                        "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                        ",\"ok\":false,\"error_type\":\"compile_error\",\"error\":" +
                        PiUnityJsonHelper.JsonString(errorSummary ?? "Compilation failed") + "}";
                    CompleteJson(id, payload);
                }
            });
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

            // 驱动协程泵与 async eval
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

        private static void StartNativePumpThread()
        {
            StopNativePumpThread();
            s_nativePumpCts = new CancellationTokenSource();
            s_nativePumpThread = new Thread(() => NativePumpLoop(s_nativePumpCts.Token));
            s_nativePumpThread.IsBackground = true;
            s_nativePumpThread.Name = "PiUnityHarness.NativePump";
            s_nativePumpThread.Start();
        }

        private static void StopNativePumpThread()
        {
            if (s_nativePumpCts != null)
            {
                s_nativePumpCts.Cancel();
                s_nativePumpCts.Dispose();
                s_nativePumpCts = null;
            }
            if (s_nativePumpThread != null)
            {
                if (s_nativePumpThread.IsAlive)
                    s_nativePumpThread.Join(250);
                s_nativePumpThread = null;
            }
        }

        private static void NativePumpLoop(CancellationToken token)
        {
            long lastWakeAttemptTicks = 0;
            long lastBackgroundHeartbeatTicks = 0;
            long backgroundHeartbeatIntervalTicks = TimeSpan.FromSeconds(BackgroundHeartbeatIntervalSeconds).Ticks;
            long mainThreadStaleTicks = TimeSpan.FromSeconds(HeartbeatStaleSeconds).Ticks;

            while (!token.IsCancellationRequested)
            {
                int required = 0;
                int result;
                try
                {
                    result = pi_unity_poll_request(s_nativeBuffer, s_nativeBuffer.Length, ref required);
                }
                catch
                {
                    Thread.Sleep(50);
                    continue;
                }

                if (result == -1 && required > s_nativeBuffer.Length)
                {
                    s_nativeBuffer = new byte[required];
                    continue;
                }

                if (result == 1 && required > 0)
                {
                    string line = Encoding.UTF8.GetString(s_nativeBuffer, 0, required);
                    PendingRequests.Enqueue(new PendingLine { Line = line, PolledAtTicks = Stopwatch.GetTimestamp() });
                    EnsureEditorWindowCanPump();
                    // Keep polling hot while work is queued; still emit background heartbeat below.
                }

                long now = DateTime.UtcNow.Ticks;

                // Modal dialogs (Save Scene, etc.) block Unity's main thread so EditorApplication.update
                // stops. Keep native broker heartbeat alive from this background thread so clients
                // do not see managed_heartbeat_timeout while the dialog is open.
                if (now - lastBackgroundHeartbeatTicks >= backgroundHeartbeatIntervalTicks)
                {
                    lastBackgroundHeartbeatTicks = now;
                    PublishBackgroundHeartbeat(now);
                }

                bool mainThreadStale = now - Interlocked.Read(ref s_lastMainThreadPumpUtcTicks) > mainThreadStaleTicks;
                bool backlog = !PendingRequests.IsEmpty;
                if ((mainThreadStale || backlog) && now - lastWakeAttemptTicks >= TimeSpan.FromSeconds(StaleWakeIntervalSeconds).Ticks)
                {
                    EnsureEditorWindowCanPump();
                    lastWakeAttemptTicks = now;
                }

                Thread.Sleep(result == 1 ? 0 : 10);
            }
        }

        /// <summary>
        /// Thread-safe heartbeat for when the Unity main thread is blocked (modal dialogs, sync stalls).
        /// Does not touch UnityEditor APIs except via cached main-thread status + Win32 modal probe.
        /// </summary>
        private static void PublishBackgroundHeartbeat(long nowTicks)
        {
            if (!s_started)
                return;

            try
            {
                bool modal = HasVisibleModalDialog();
                Interlocked.Exchange(ref s_modalDialogPresent, modal ? 1 : 0);

                // Always refresh native last_heartbeat so reap_timeouts does not fail in-flight work.
                pi_unity_managed_heartbeat(s_generation);
                Interlocked.Exchange(ref s_lastNativeHeartbeatUtcTicks, nowTicks);

                // When main thread is stalled, publish richer status so pi/status can show modal=1.
                bool mainThreadStale =
                    nowTicks - Interlocked.Read(ref s_lastMainThreadPumpUtcTicks)
                    > TimeSpan.FromSeconds(HeartbeatStaleSeconds).Ticks;
                if (modal || mainThreadStale)
                {
                    string baseStatus = Volatile.Read(ref s_lastMainThreadEditorStatus) ?? "editing";
                    string status = ComposeBlockedEditorStatus(baseStatus, modal, mainThreadStale);
                    byte[] statusBytes = Utf8(status);
                    pi_unity_set_managed_state(ManagedStateReady, s_generation, statusBytes, statusBytes.Length);
                }
            }
            catch
            {
                // Background path must never take down the pump thread.
            }
        }

        private static string ComposeBlockedEditorStatus(string baseStatus, bool modal, bool mainThreadStale)
        {
            // baseStatus looks like: editing;focus=focused;window=normal
            string mode = "editing";
            string rest = string.Empty;
            if (!string.IsNullOrEmpty(baseStatus))
            {
                int semi = baseStatus.IndexOf(';');
                if (semi >= 0)
                {
                    mode = baseStatus.Substring(0, semi);
                    rest = baseStatus.Substring(semi); // includes leading ';'
                }
                else
                {
                    mode = baseStatus;
                }
            }

            if (modal)
                mode = "modal";
            else if (mainThreadStale && mode == "editing")
                mode = "blocked";

            string flags = string.Empty;
            if (modal)
                flags += ";modal=1";
            if (mainThreadStale)
                flags += ";mainThreadStale=1";

            // Avoid duplicating modal= if base already had it
            if (rest.IndexOf(";modal=", StringComparison.Ordinal) >= 0)
                flags = flags.Replace(";modal=1", string.Empty);

            return mode + rest + flags;
        }

        /// <summary>
        /// Detect Win32 modal/message dialogs owned by the Unity editor process
        /// (e.g. "Scene(s) Have Been Modified" Save/Don't Save/Cancel).
        /// </summary>
        private static bool HasVisibleModalDialog()
        {
            try
            {
                uint pid = (uint)Process.GetCurrentProcess().Id;
                bool found = false;
                EnumWindows((hWnd, lParam) =>
                {
                    if (found)
                        return false;
                    if (!IsWindowVisible(hWnd))
                        return true;

                    GetWindowThreadProcessId(hWnd, out uint windowPid);
                    if (windowPid != pid)
                        return true;

                    // Skip the main editor window itself.
                    if (s_mainWindowHandle != IntPtr.Zero && hWnd == s_mainWindowHandle)
                        return true;

                    var className = new StringBuilder(64);
                    GetClassName(hWnd, className, className.Capacity);
                    string cls = className.ToString();

                    // Standard Windows dialog class used by Unity save/prompt dialogs
                    // (e.g. "Scene(s) Have Been Modified" → Save / Don't Save / Cancel).
                    if (string.Equals(cls, "#32770", StringComparison.Ordinal))
                    {
                        found = true;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
                return found;
            }
            catch
            {
                return false;
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
                case "ping":
                    CompleteJson(request.id,
                        "{\"reply_to\":" + PiUnityJsonHelper.JsonString(request.id) + ",\"ok\":true,\"result\":{\"output\":\"pong\",\"typeName\":\"string\"}}");
                    return;
                case "status":
                    Status(request);
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

        private static void ExecuteCode(NativeRequest request, RequestTiming timing)
        {
            if (request.payload == null || string.IsNullOrEmpty(request.payload.code))
            {
                CompleteError(request.id, "empty_code", "usage");
                return;
            }

            long evalStart = Stopwatch.GetTimestamp();
            PiUnityEvaluator.EvalResult result = s_evaluator.Eval(request.payload.code);
            timing.EvalMs = RequestTiming.TicksToMs(Stopwatch.GetTimestamp() - evalStart);
            TryCompleteEvalOrCoroutine(request.id, result, timing, request.timeoutMs);
        }

        private static void ExecuteFile(NativeRequest request, RequestTiming timing)
        {
            string code;
            if (!TryReadPayloadFile(request, out code))
                return;

            long evalStart = Stopwatch.GetTimestamp();
            PiUnityEvaluator.EvalResult result = s_evaluator.Eval(code);
            timing.EvalMs = RequestTiming.TicksToMs(Stopwatch.GetTimestamp() - evalStart);
            TryCompleteEvalOrCoroutine(request.id, result, timing, request.timeoutMs);
        }

        private static void ValidateExecuteCode(NativeRequest request, RequestTiming timing)
        {
            if (request.payload == null || string.IsNullOrEmpty(request.payload.code))
            {
                CompleteError(request.id, "empty_code", "usage");
                return;
            }

            CompleteValidateThenEvalResult(request.id, request.payload.code, timing, request.timeoutMs);
        }

        private static void ValidateExecuteFile(NativeRequest request, RequestTiming timing)
        {
            string code;
            if (!TryReadPayloadFile(request, out code))
                return;

            CompleteValidateThenEvalResult(request.id, code, timing, request.timeoutMs);
        }

        private static void ValidateCode(NativeRequest request)
        {
            if (request.payload == null || string.IsNullOrEmpty(request.payload.code))
            {
                CompleteError(request.id, "empty_code", "usage");
                return;
            }

            CompleteValidationResult(request.id, s_evaluator.Validate(request.payload.code));
        }

        private static void ValidateFile(NativeRequest request)
        {
            string code;
            if (!TryReadPayloadFile(request, out code))
                return;

            CompleteValidationResult(request.id, s_evaluator.Validate(code));
        }

        private static bool TryReadPayloadFile(NativeRequest request, out string code)
        {
            code = null;
            if (request.payload == null || string.IsNullOrEmpty(request.payload.filePath))
            {
                CompleteError(request.id, "empty_file_path", "usage");
                return false;
            }

            string path = request.payload.filePath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(s_projectPath, path);
            path = Path.GetFullPath(path);

            if (!File.Exists(path))
            {
                CompleteError(request.id, "file_not_found: " + path, "usage");
                return false;
            }

            code = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
            return true;
        }

        // --- 协程 / Task 结果处理 ---

        /// <summary>
        /// 如果 EvalResult 包含 IEnumerator 协程或 Task/Task-like，异步完成；
        /// 否则作为同步结果直接完成。
        /// </summary>
        private static void TryCompleteEvalOrCoroutine(string id, PiUnityEvaluator.EvalResult result, RequestTiming timing, int timeoutMs)
        {
            if (result == null)
            {
                CompleteError(id, "null_eval_result", "runtime_error");
                return;
            }

            if (!result.Ok)
            {
                CompleteEvalResult(id, result, timing);
                return;
            }

            if (result.IsCoroutine && result.Coroutine != null)
            {
                // 协程结果：交给 pump 逐帧驱动
                int coroutineTimeoutMs = timeoutMs > 0 ? timeoutMs : 60000;
                bool queued = s_pump != null && s_pump.Enqueue(result.Coroutine, id,
                    (success, text, typeName) =>
                    {
                        if (success)
                        {
                            string timingFragment = timing != null ? ",\"timing\":" + timing.ToJsonFragment() : string.Empty;
                            string payload =
                                "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                                ",\"ok\":true,\"result\":{\"output\":" + PiUnityJsonHelper.JsonString(text ?? string.Empty) +
                                ",\"typeName\":" + PiUnityJsonHelper.JsonString(typeName ?? "void") + timingFragment + "}}";
                            CompleteJson(id, payload);
                        }
                        else
                        {
                            CompleteJson(id,
                                "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) + ",\"ok\":false,\"error_type\":" + PiUnityJsonHelper.JsonString(typeName ?? "runtime_error") + ",\"error\":" + PiUnityJsonHelper.JsonString(text ?? "coroutine_failed") + "}");
                        }
                    }, coroutineTimeoutMs);
                if (!queued)
                {
                    CompleteJson(id,
                        "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) + ",\"ok\":false,\"error_type\":\"busy\",\"error\":\"coroutine queue full\"}");
                }
                return;
            }

            if (result.IsAsyncTask && result.AsyncTask != null)
            {
                EnqueueAsyncEval(id, result.AsyncTask, timing, timeoutMs);
                return;
            }

            CompleteEvalResult(id, result, timing);
        }

        private sealed class AsyncEvalState
        {
            public RequestTiming Timing;
            public int TimeoutMs;
        }

        private static void EnqueueAsyncEval(string id, Task task, RequestTiming timing, int timeoutMs)
        {
            if (s_asyncEvalPump == null)
                s_asyncEvalPump = new PiUnityAsyncEvalPump();

            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 60000;
            s_asyncEvalPump.Enqueue(id, task, effectiveTimeoutMs, new AsyncEvalState
            {
                Timing = timing,
                TimeoutMs = effectiveTimeoutMs,
            });
            if (task.IsCompleted)
                TickPendingAsyncEvals();
        }

        private static void TickPendingAsyncEvals()
        {
            if (s_asyncEvalPump == null || s_asyncEvalPump.PendingCount == 0)
                return;

            s_asyncEvalPump.Tick(OnAsyncEvalCompleted);
        }

        private static void OnAsyncEvalCompleted(string id, bool success, string text, string typeName, object state)
        {
            RequestTiming timing = null;
            int timeoutMs = 60000;

            if (state is PiUnityAsyncEvalPump.NestedAsyncResult nestedBag)
            {
                if (nestedBag.OriginalState is AsyncEvalState bagFromNested)
                {
                    timing = bagFromNested.Timing;
                    timeoutMs = bagFromNested.TimeoutMs;
                }
            }
            else if (state is AsyncEvalState bag)
            {
                timing = bag.Timing;
                timeoutMs = bag.TimeoutMs;
            }

            if (success && typeName == "nested_task" && state is PiUnityAsyncEvalPump.NestedAsyncResult nestedTask)
            {
                TryCompleteEvalOrCoroutine(id, nestedTask.Nested, timing, timeoutMs);
                return;
            }

            if (success && typeName == "nested_coroutine" && state is PiUnityAsyncEvalPump.NestedAsyncResult nestedCo)
            {
                TryCompleteEvalOrCoroutine(id, nestedCo.Nested, timing, timeoutMs);
                return;
            }

            if (!success)
            {
                CompleteJson(id,
                    "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                    ",\"ok\":false,\"error_type\":" + PiUnityJsonHelper.JsonString(typeName ?? "runtime_error") +
                    ",\"error\":" + PiUnityJsonHelper.JsonString(text ?? "async_eval_failed") + "}");
                return;
            }

            string timingFragment = timing != null ? ",\"timing\":" + timing.ToJsonFragment() : string.Empty;
            string payload =
                "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                ",\"ok\":true,\"result\":{\"output\":" + PiUnityJsonHelper.JsonString(text ?? string.Empty) +
                ",\"typeName\":" + PiUnityJsonHelper.JsonString(typeName ?? "void") + timingFragment + "}}";
            CompleteJson(id, payload);
        }

        private static void CancelAllPendingAsyncEvals(string reason)
        {
            if (s_asyncEvalPump == null)
                return;

            s_asyncEvalPump.CancelAll((id, success, text, typeName, state) =>
            {
                CompleteJson(id,
                    "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                    ",\"ok\":false,\"error_type\":" + PiUnityJsonHelper.JsonString(typeName ?? "cancelled") +
                    ",\"error\":" + PiUnityJsonHelper.JsonString(text ?? reason) + "}");
            }, reason);
        }

        // --- 编译请求 ---

        private static void Recompile(NativeRequest request)
        {
            UnityEngine.Debug.Log("[PiUnityHarness] recompile request id=" + request.id);

            PiUnityCompileCoordinator.StartCompile(request.id,
                (compileId, success, resultText, errorSummary) =>
                {
                    if (success)
                    {
                        string successPayload =
                            "{\"reply_to\":" + PiUnityJsonHelper.JsonString(compileId) +
                            ",\"ok\":true,\"result\":{\"output\":" + PiUnityJsonHelper.JsonString(resultText ?? "compilation_succeeded") +
                            ",\"typeName\":\"compile_status\"}}";
                        CompleteJson(compileId, successPayload);
                    }
                    else
                    {
                        string errorType = string.Equals(resultText, "busy", StringComparison.Ordinal) ? "busy" : "compile_error";
                        string errorPayload =
                            "{\"reply_to\":" + PiUnityJsonHelper.JsonString(compileId) +
                            ",\"ok\":false,\"error_type\":" + PiUnityJsonHelper.JsonString(errorType) + ",\"error\":" +
                            PiUnityJsonHelper.JsonString(errorSummary ?? "Compilation failed") + "}";
                        CompleteJson(compileId, errorPayload);
                    }
                });
        }

        // --- pipeline 命令桥 ---

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

        // --- 状态查询 ---

        private static void Status(NativeRequest request)
        {
            string statusJson = BuildStatusResponse(request.id);
            CompleteJson(request.id, statusJson);
        }

        private static void ContextSnapshot(NativeRequest request)
        {
            int maxDepth = request.payload != null && request.payload.maxDepth >= 0
                ? request.payload.maxDepth
                : PiUnityContextSnapshot.DefaultMaxDepth;
            int maxNodes = request.payload != null && request.payload.maxNodes > 0
                ? request.payload.maxNodes
                : PiUnityContextSnapshot.DefaultMaxNodes;
            int logLimit = request.payload != null && request.payload.logLimit >= 0
                ? request.payload.logLimit
                : PiUnityContextSnapshot.DefaultLogLimit;
            string logLevel = request.payload != null ? request.payload.logLevel : null;
            bool includeComponents = request.payload != null && request.payload.includeComponents;

            try
            {
                string snapshot = PiUnityContextSnapshot.BuildJson(maxDepth, maxNodes, logLimit, logLevel, includeComponents);
                CompleteJson(request.id, PiUnityJsonHelper.SuccessJson(request.id, snapshot));
            }
            catch (Exception ex)
            {
                CompleteError(request.id, "context_snapshot_failed: " + ex.Message, "snapshot_error");
            }
        }

        private static string BuildStatusResponse(string replyTo)
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject[] roots = scene.IsValid() ? scene.GetRootGameObjects() : new GameObject[0];
            Camera mainCamera = Camera.main;

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"reply_to\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(replyTo));
            sb.Append("\",\"ok\":true,\"result\":{");
            sb.Append("\"unity_version\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(Application.unityVersion));
            sb.Append("\",\"platform\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(Application.platform.ToString()));
            sb.Append("\",\"project\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(Application.dataPath));
            sb.Append("\",\"playing\":");
            sb.Append(EditorApplication.isPlaying ? "true" : "false");
            sb.Append(",\"scene\":{\"name\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(scene.name));
            sb.Append("\",\"path\":\"");
            sb.Append(PiUnityJsonHelper.EscapeJson(scene.path));
            sb.Append("\",\"root_count\":");
            sb.Append(roots.Length);
            sb.Append("},\"main_camera\":");
            sb.Append(mainCamera != null ? "true" : "false");
            sb.Append(",\"is_compiling\":");
            sb.Append(EditorApplication.isCompiling ? "true" : "false");
            sb.Append(",\"is_updating\":");
            sb.Append(EditorApplication.isUpdating ? "true" : "false");
            sb.Append(",\"pump_pending\":");
            sb.Append(s_pump != null ? s_pump.PendingCount : 0);
            sb.Append(",\"async_eval_pending\":");
            sb.Append(s_asyncEvalPump != null ? s_asyncEvalPump.PendingCount : 0);
            sb.Append("}}");
            return sb.ToString();
        }

        private static void CompleteEvalResult(string id, PiUnityEvaluator.EvalResult result, RequestTiming timing)
        {
            if (!result.Ok)
            {
                CompleteJson(id,
                    "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) + ",\"ok\":false,\"error_type\":\"runtime_error\",\"error\":" + PiUnityJsonHelper.JsonString(result.Error ?? "execute_failed") + "}");
                return;
            }

            string timingFragment = timing != null ? ",\"timing\":" + timing.ToJsonFragment() : string.Empty;
            string payload =
                "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                ",\"ok\":true,\"result\":{\"output\":" + PiUnityJsonHelper.JsonString(result.Output ?? string.Empty) +
                ",\"typeName\":" + PiUnityJsonHelper.JsonString(result.TypeName ?? string.Empty) + timingFragment + "}}";
            CompleteJson(id, payload);
        }

        private static void CompleteValidateThenEvalResult(string id, string code, RequestTiming timing, int timeoutMs)
        {
            long validateStart = Stopwatch.GetTimestamp();
            string validation = s_evaluator.Validate(code);
            timing.ValidateMs = RequestTiming.TicksToMs(Stopwatch.GetTimestamp() - validateStart);
            if (!IsValidationOk(validation))
            {
                CompleteJson(id,
                    "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) + ",\"ok\":false,\"error_type\":\"compile_error\",\"error\":" + PiUnityJsonHelper.JsonString(validation ?? "validate_failed") + "}");
                return;
            }

            long evalStart = Stopwatch.GetTimestamp();
            PiUnityEvaluator.EvalResult result = s_evaluator.Eval(code);
            timing.EvalMs = RequestTiming.TicksToMs(Stopwatch.GetTimestamp() - evalStart);
            TryCompleteEvalOrCoroutine(id, result, timing, timeoutMs);
        }

        private static void CompleteValidationResult(string id, string validation)
        {
            if (!IsValidationOk(validation))
            {
                CompleteJson(id,
                    "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) + ",\"ok\":false,\"error\":" + PiUnityJsonHelper.JsonString(validation ?? "validate_failed") + "}");
                return;
            }

            string payload =
                "{\"reply_to\":" + PiUnityJsonHelper.JsonString(id) +
                ",\"ok\":true,\"result\":{\"output\":" + PiUnityJsonHelper.JsonString(validation) +
                ",\"typeName\":\"validation\"}}";
            CompleteJson(id, payload);
        }

        private static bool IsValidationOk(string validation)
        {
            return !string.IsNullOrEmpty(validation) && !validation.StartsWith("COMPILE ERROR", StringComparison.Ordinal);
        }

        private static void PublishManagedState(int state, string editorStatus)
        {
            try
            {
                byte[] status = Utf8(editorStatus ?? string.Empty);
                pi_unity_set_managed_state(state, s_generation, status, status.Length);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[PiUnityHarness] publish managed state failed: " + ex);
            }
        }

        private static void PublishHeartbeat()
        {
            try
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                string status = CurrentEditorStatus();
                Volatile.Write(ref s_lastMainThreadEditorStatus, status);
                Interlocked.Exchange(ref s_lastMainThreadPumpUtcTicks, nowTicks);
                Interlocked.Exchange(ref s_lastNativeHeartbeatUtcTicks, nowTicks);
                Interlocked.Exchange(ref s_modalDialogPresent, HasVisibleModalDialog() ? 1 : 0);

                pi_unity_managed_heartbeat(s_generation);
                // Prefer main-thread Unity API status; annotate modal if present.
                if (Interlocked.CompareExchange(ref s_modalDialogPresent, 0, 0) == 1
                    && status.IndexOf("modal=", StringComparison.Ordinal) < 0)
                {
                    // Replace mode segment with modal when a Win32 dialog is up.
                    status = ComposeBlockedEditorStatus(status, modal: true, mainThreadStale: false);
                }
                PublishManagedState(ManagedStateReady, status);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[PiUnityHarness] heartbeat failed: " + ex);
            }
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

        private static string CurrentEditorStatus()
        {
            string mode;
            if (EditorApplication.isCompiling)
                mode = "compiling";
            else if (EditorApplication.isPlayingOrWillChangePlaymode)
                mode = "playing";
            else if (Interlocked.CompareExchange(ref s_modalDialogPresent, 0, 0) == 1)
                mode = "modal";
            else
                mode = "editing";

            string focus = IsEditorProcessForeground() ? "focused" : "background";
            string window = IsMainWindowMinimized() ? "minimized" : "normal";
            string status = mode + ";focus=" + focus + ";window=" + window;
            if (mode == "modal")
                status += ";modal=1";
            return status;
        }

        private static void EnableRunInBackground()
        {
            try
            {
                if (!SessionState.GetBool(SessionKey_RunInBackgroundCaptured, false))
                {
                    SessionState.SetBool(SessionKey_RunInBackgroundOriginal, Application.runInBackground);
                    SessionState.SetBool(SessionKey_RunInBackgroundCaptured, true);
                }
                Application.runInBackground = true;
                s_runInBackgroundApplied = true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[PiUnityHarness] enable runInBackground failed: " + ex.Message);
            }
        }

        private static void RestoreRunInBackground()
        {
            if (!s_runInBackgroundApplied)
                return;
            s_runInBackgroundApplied = false;

            try
            {
                if (SessionState.GetBool(SessionKey_RunInBackgroundCaptured, false))
                {
                    Application.runInBackground = SessionState.GetBool(SessionKey_RunInBackgroundOriginal, false);
                    SessionState.SetBool(SessionKey_RunInBackgroundCaptured, false);
                    SessionState.SetBool(SessionKey_RunInBackgroundOriginal, false);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[PiUnityHarness] restore runInBackground failed: " + ex.Message);
            }
        }

        private static bool IsMainWindowMinimized()
        {
            try
            {
                return s_mainWindowHandle != IntPtr.Zero && IsIconic(s_mainWindowHandle);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEditorProcessForeground()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                    return Application.isFocused;
                uint processId;
                GetWindowThreadProcessId(foreground, out processId);
                return processId == (uint)Process.GetCurrentProcess().Id;
            }
            catch
            {
                return Application.isFocused;
            }
        }

        private static void EnsureEditorWindowCanPump()
        {
            try
            {
                if (IsMainWindowMinimized())
                    ShowWindow(s_mainWindowHandle, SwRestore);
            }
            catch
            {
            }
            WakeEditorMessagePump();
        }

        private static void WakeEditorMessagePump()
        {
            try
            {
                if (s_mainThreadId != 0)
                    PostThreadMessage(s_mainThreadId, WmNull, UIntPtr.Zero, IntPtr.Zero);
                if (s_mainWindowHandle != IntPtr.Zero)
                    PostMessage(s_mainWindowHandle, WmNull, UIntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
            }
        }

        private static bool ShouldRunInCurrentProcess()
        {
            if (Application.isBatchMode)
                return false;
            string[] args = Environment.GetCommandLineArgs() ?? Array.Empty<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? string.Empty;
                if (arg.IndexOf("AssetImportWorker", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                if (string.Equals(arg, "-batchMode", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static string DetectProjectPath()
        {
            return Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
        }

        private static string GetOrCreateToken()
        {
            string token = SessionState.GetString(SessionKey_Token, string.Empty);
            if (!string.IsNullOrEmpty(token))
                return token;
            token = Guid.NewGuid().ToString("N");
            SessionState.SetString(SessionKey_Token, token);
            return token;
        }

        private static string GetOrCreatePipeName(string projectPath)
        {
            string pipe = SessionState.GetString(SessionKey_Pipe, string.Empty);
            if (!string.IsNullOrEmpty(pipe))
                return pipe;
            pipe = "pi_unity_" + ShortHash(projectPath);
            SessionState.SetString(SessionKey_Pipe, pipe);
            return pipe;
        }

        private static long NextGeneration()
        {
            int next = SessionState.GetInt(SessionKey_Generation, 0) + 1;
            SessionState.SetInt(SessionKey_Generation, next);
            return next;
        }

        private static string ShortHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder sb = new StringBuilder(12);
                for (int i = 0; i < 6 && i < bytes.Length; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string GetStatePlaneName(string projectPath)
        {
            string normalized = (projectPath ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();
            while (normalized.EndsWith("/") && normalized.Length > 3)
                normalized = normalized.Substring(0, normalized.Length - 1);

            const ulong FnvOffset = 14695981039346656037UL;
            const ulong FnvPrime = 1099511628211UL;
            ulong hash = FnvOffset;
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= FnvPrime;
            }
            return "Local\\PiUnityHarnessState_" + hash.ToString("x16");
        }

        private static void WriteBridgeInfo()
        {
            string dir = Path.Combine(s_projectPath, "Library", "PiUnityHarness");
            Directory.CreateDirectory(dir);
            BridgeInfo info = new BridgeInfo
            {
                project = s_projectPath,
                pid = Process.GetCurrentProcess().Id,
                pipe = "\\\\.\\pipe\\" + s_pipeName,
                token = s_token,
                generation = s_generation,
                statePlaneName = GetStatePlaneName(s_projectPath),
            };
            File.WriteAllText(Path.Combine(dir, "bridge.json"), JsonUtility.ToJson(info, true), Encoding.UTF8);
        }

        private static byte[] Utf8(string value)
        {
            return Encoding.UTF8.GetBytes(value ?? string.Empty);
        }

        private static int ByteLen(string value)
        {
            return Encoding.UTF8.GetByteCount(value ?? string.Empty);
        }
    }
}

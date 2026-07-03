using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

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

        [Serializable]
        private sealed class NativeRequest
        {
            public string id;
            public string type;
            public string token;
            public ExecutePayload payload;
        }

        [Serializable]
        private sealed class ExecutePayload
        {
            public string code;
            public string filePath;
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

        private static readonly ConcurrentQueue<string> PendingRequests = new ConcurrentQueue<string>();
        private static Thread s_nativePumpThread;
        private static CancellationTokenSource s_nativePumpCts;
        private static PiUnityEvaluator s_evaluator;
        private static byte[] s_nativeBuffer;
        private static bool s_started;
        private static string s_projectPath;
        private static string s_pipeName;
        private static string s_token;
        private static long s_generation;
        private static IntPtr s_mainWindowHandle;
        private static uint s_mainThreadId;
        private static double s_lastHeartbeatAt;
        private static bool s_runInBackgroundApplied;

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
                s_nativeBuffer = new byte[NativeInitialBufferSize];

                if (pi_unity_init(Utf8(s_projectPath), ByteLen(s_projectPath), Utf8(s_pipeName), ByteLen(s_pipeName), Utf8(s_token), ByteLen(s_token), NativeProtocolVersion) != 0)
                    UnityEngine.Debug.LogError("[PiUnityHarness] native broker init failed");

                PublishManagedState(ManagedStateInitializing, "starting");
                WriteBridgeInfo();
                EditorApplication.update += OnUpdate;
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
        }

        private static void OnAfterReload()
        {
            Start();
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

            int processed = 0;
            while (processed < MaxRequestsPerUpdate && PendingRequests.TryDequeue(out string line))
            {
                HandleRequestLine(line);
                processed++;
            }

            if (!PendingRequests.IsEmpty)
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
                    PendingRequests.Enqueue(line);
                    EnsureEditorWindowCanPump();
                    continue;
                }

                Thread.Sleep(10);
            }
        }

        private static void HandleRequestLine(string line)
        {
            NativeRequest request;
            try
            {
                request = JsonUtility.FromJson<NativeRequest>(line);
            }
            catch (Exception ex)
            {
                CompleteError("", "request_parse_failed: " + ex.Message);
                return;
            }

            if (request == null || string.IsNullOrEmpty(request.id) || string.IsNullOrEmpty(request.type))
            {
                CompleteError(request != null ? request.id : "", "missing_request_id_or_type");
                return;
            }

            switch (request.type)
            {
                case "execute_code":
                    ExecuteCode(request);
                    return;
                case "execute_file":
                    ExecuteFile(request);
                    return;
                case "validate_code":
                    ValidateCode(request);
                    return;
                case "validate_file":
                    ValidateFile(request);
                    return;
                default:
                    CompleteError(request.id, "unsupported_request_type");
                    return;
            }
        }

        private static void ExecuteCode(NativeRequest request)
        {
            if (request.payload == null || string.IsNullOrEmpty(request.payload.code))
            {
                CompleteError(request.id, "empty_code");
                return;
            }

            CompleteEvalResult(request.id, s_evaluator.Eval(request.payload.code));
        }

        private static void ExecuteFile(NativeRequest request)
        {
            string code;
            if (!TryReadPayloadFile(request, out code))
                return;

            CompleteEvalResult(request.id, s_evaluator.Eval(code));
        }

        private static void ValidateCode(NativeRequest request)
        {
            if (request.payload == null || string.IsNullOrEmpty(request.payload.code))
            {
                CompleteError(request.id, "empty_code");
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
                CompleteError(request.id, "empty_file_path");
                return false;
            }

            string path = request.payload.filePath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(s_projectPath, path);
            path = Path.GetFullPath(path);

            if (!File.Exists(path))
            {
                CompleteError(request.id, "file_not_found: " + path);
                return false;
            }

            code = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
            return true;
        }

        private static void CompleteEvalResult(string id, PiUnityEvaluator.EvalResult result)
        {
            if (!result.Ok)
            {
                CompleteJson(id,
                    "{\"reply_to\":" + JsonString(id) + ",\"ok\":false,\"error\":" + JsonString(result.Error ?? "execute_failed") + "}");
                return;
            }

            string payload =
                "{\"reply_to\":" + JsonString(id) +
                ",\"ok\":true,\"result\":{\"output\":" + JsonString(result.Output ?? string.Empty) +
                ",\"typeName\":" + JsonString(result.TypeName ?? string.Empty) + "}}";
            CompleteJson(id, payload);
        }

        private static void CompleteValidationResult(string id, string validation)
        {
            bool ok = !string.IsNullOrEmpty(validation) && !validation.StartsWith("COMPILE ERROR", StringComparison.Ordinal);
            if (!ok)
            {
                CompleteJson(id,
                    "{\"reply_to\":" + JsonString(id) + ",\"ok\":false,\"error\":" + JsonString(validation ?? "validate_failed") + "}");
                return;
            }

            string payload =
                "{\"reply_to\":" + JsonString(id) +
                ",\"ok\":true,\"result\":{\"output\":" + JsonString(validation) +
                ",\"typeName\":\"validation\"}}";
            CompleteJson(id, payload);
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
                pi_unity_managed_heartbeat(s_generation);
                PublishManagedState(ManagedStateReady, CurrentEditorStatus());
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[PiUnityHarness] heartbeat failed: " + ex);
            }
        }

        private static void CompleteError(string id, string error)
        {
            string json = "{\"reply_to\":" + JsonString(id) + ",\"ok\":false,\"error\":" + JsonString(error) + "}";
            CompleteJson(id, json);
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
            else
                mode = "editing";

            string focus = IsEditorProcessForeground() ? "focused" : "background";
            string window = IsMainWindowMinimized() ? "minimized" : "normal";
            return mode + ";focus=" + focus + ";window=" + window;
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

        private static string JsonString(string value)
        {
            if (value == null)
                return "null";
            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}

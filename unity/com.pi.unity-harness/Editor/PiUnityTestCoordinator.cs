using System;
using System.Text;
#if PI_UNITY_PIPELINE
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Pipeline;
using Unity.Pipeline.Editor.Commands;
using UnityEditor;
using UnityEngine;
#endif

namespace Pi.UnityHarness.Editor
{
    internal static class PiUnityTestCoordinator
    {
#if PI_UNITY_PIPELINE
        private const string SessionKey_PendingTestRequestId = "PiUnityHarness_PendingTestRequestId";
        private const string SessionKey_OriginalParametersJson = "PiUnityHarness_OriginalTestParametersJson";
        private const string SessionKey_CurrentSegment = "PiUnityHarness_CurrentTestSegment";
        private const string SessionKey_EditorSegmentResultJson = "PiUnityHarness_EditorTestSegmentResultJson";
        private const string SessionKey_DeadlineUtcTicks = "PiUnityHarness_TestDeadlineUtcTicks";

        private const string SegmentSingle = "single";
        private const string SegmentEditor = "editor";
        private const string SegmentPlaymode = "playmode";
        private const double NoTestsGraceSeconds = 5.0d;
        private const double PollIntervalSeconds = 0.5d;

        /// <summary>
        /// 可注入的 isPlaying 委托（默认取 EditorApplication.isPlaying），测试可替换以模拟播放状态。
        /// </summary>
        internal static Func<bool> IsPlayingProvider = () => EditorApplication.isPlaying;

        /// <summary>
        /// 可注入的“pipeline 测试运行中”检测委托（默认走 TestCommands.GetTestStatus）。
        /// EditMode 测试本身运行在 pipeline 测试运行器中，若不注入会误判为 busy。
        /// </summary>
        internal static Func<bool> PipelineTestRunRunningProvider = () => IsPipelineTestRunRunning();

        /// <summary>
        /// 可注入的测试段执行委托（默认走 TestCommands.RunTests）。测试替身可避免真实启动测试执行。
        /// 签名与 TestCommands.RunTests 前 5 个参数一致（asyncTests 恒为 true）。
        /// </summary>
        internal static Func<string, string, string, bool, int, Task<TestExecutionResponse>> RunTestsInvoker;

        private static Action<string, string> s_completeJson;
        private static bool s_updateRegistered;
        private static double s_lastPollAt;
        private static long s_noTestsFirstSeenUtcTicks;
        private static Task<TestExecutionResponse> s_startTask;
        private static string s_startSegment;
#endif

        public static void StartRunTests(string requestId, string parametersJson, int timeoutMs, Action<string, string> completeJson)
        {
#if PI_UNITY_PIPELINE
            s_completeJson = completeJson;

            if (HasPendingRequest())
            {
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "busy", "run_tests request already in progress"));
                return;
            }

            if (PipelineTestRunRunningProvider())
            {
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "busy", "pipeline test run already in progress"));
                return;
            }

            JObject parameters;
            if (!PiUnityPipelineCommandExecutor.TryParseParameters(parametersJson, out parameters, out string parseError))
            {
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "parameter_error", parseError));
                return;
            }

            string mode = NormalizeMode(ReadString(parameters, "mode", "all"));
            if (mode == null)
            {
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "parameter_error", "mode must be editor, playmode, or all"));
                return;
            }

            // 播放模式下拒绝运行 PlayMode 测试（含 all 的 PlayMode 段），避免与运行中的播放状态冲突；
            // EditorMode 测试不受影响。守卫必须在设置 SessionState 之前返回，保证不进入测试执行路径。
            if (ShouldRejectPlaymodeRun(mode, IsPlayingProvider()))
            {
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(
                    requestId,
                    "playmode_active",
                    "Cannot run PlayMode tests while the editor is in play mode. Exit play mode (editor_stop) and retry."));
                return;
            }

            // 启动测试前应用 dirtyAction 脏场景策略（与 scene_open / scene_create / scene_unload 共用）
            string dirtyPolicyError = ApplyDirtyPolicy(parameters, "run_tests");
            if (dirtyPolicyError != null)
            {
                completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "dirty_scene", dirtyPolicyError));
                return;
            }

            int defaultTimeoutMs = Math.Max(1, ReadInt(parameters, "timeout", 300)) * 1000 + 30000;
            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : defaultTimeoutMs;

            s_noTestsFirstSeenUtcTicks = 0;
            SessionState.SetString(SessionKey_PendingTestRequestId, requestId);
            SessionState.SetString(SessionKey_OriginalParametersJson, parameters.ToString(Formatting.None));
            SessionState.SetString(SessionKey_EditorSegmentResultJson, string.Empty);
            SessionState.SetString(SessionKey_DeadlineUtcTicks, DateTime.UtcNow.AddMilliseconds(effectiveTimeoutMs).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (mode == "all")
            {
                SessionState.SetString(SessionKey_CurrentSegment, SegmentEditor);
                DeletePipelineStatusFile();
                StartSegment("editor");
            }
            else
            {
                SessionState.SetString(SessionKey_CurrentSegment, SegmentSingle);
                DeletePipelineStatusFile();
                StartSegment(mode);
            }

            EnsurePolling();
#else
            completeJson(requestId, PiUnityJsonHelper.ErrorJson(requestId, "pipeline_unavailable", "com.unity.pipeline is not installed or PI_UNITY_PIPELINE is not defined."));
#endif
        }

        public static void ResumeAfterReload(Action<string, string> completeJson)
        {
#if PI_UNITY_PIPELINE
            if (!HasPendingRequest())
                return;

            s_completeJson = completeJson;
            s_startTask = null;
            s_startSegment = null;
            EnsurePolling();
#endif
        }

#if PI_UNITY_PIPELINE
        private static void EnsurePolling()
        {
            if (s_updateRegistered)
                return;
            s_updateRegistered = true;
            EditorApplication.update += OnUpdate;
        }

        private static void StopPollingIfIdle()
        {
            if (HasPendingRequest())
                return;
            if (!s_updateRegistered)
                return;
            s_updateRegistered = false;
            EditorApplication.update -= OnUpdate;
        }

        private static void OnUpdate()
        {
            if (!HasPendingRequest())
            {
                StopPollingIfIdle();
                return;
            }

            if (IsTimedOut())
            {
                try { TestCommands.CancelTests(); } catch { }
                CompleteError("timeout", "run_tests exceeded requested timeout");
                return;
            }

            if (s_startTask != null)
            {
                if (!s_startTask.IsCompleted)
                    return;
                HandleStartTask();
                if (!HasPendingRequest())
                    return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - s_lastPollAt < PollIntervalSeconds)
                return;
            s_lastPollAt = now;

            PollStatus();
        }

        private static void StartSegment(string segmentMode)
        {
            JObject parameters = CurrentParameters();
            string filter = ReadString(parameters, "filter", string.Empty);
            string filterType = ReadString(parameters, "filter_type", "testName");
            bool includeExplicit = ReadBool(parameters, "include_explicit", false);
            int timeout = ReadInt(parameters, "timeout", 300);

            try
            {
                s_noTestsFirstSeenUtcTicks = 0;
                s_startSegment = segmentMode;
                s_startTask = RunTestsInvoker != null
                    ? RunTestsInvoker(segmentMode, filter, filterType, includeExplicit, timeout)
                    : TestCommands.RunTests(segmentMode, filter, filterType, includeExplicit, true, timeout);
                if (s_startTask.IsCompleted)
                    HandleStartTask();
            }
            catch (Exception ex)
            {
                CompleteError("command_error", "Failed to start run_tests " + segmentMode + " segment: " + ex.Message);
            }
        }

        private static void HandleStartTask()
        {
            Task<TestExecutionResponse> task = s_startTask;
            string segment = s_startSegment;
            s_startTask = null;
            s_startSegment = null;

            if (task == null)
                return;

            if (task.IsCanceled)
            {
                CompleteError("cancelled", "run_tests " + segment + " segment start was cancelled");
                return;
            }

            if (task.IsFaulted)
            {
                Exception ex = task.Exception != null ? task.Exception.GetBaseException() : null;
                CompleteError("command_error", "run_tests " + segment + " segment start failed: " + (ex != null ? ex.Message : "unknown error"));
                return;
            }

            TestExecutionResponse response = task.Result;
            if (response != null && !response.Success)
            {
                CompleteError("command_error", response.Error ?? response.Message ?? ("run_tests " + segment + " segment failed to start"));
            }
        }

        private static void PollStatus()
        {
            JObject status;
            string statusJson = TestCommands.GetTestStatus();
            if (string.IsNullOrWhiteSpace(statusJson))
            {
                status = new JObject { ["status"] = "no_tests" };
            }
            else
            {
                try
                {
                    status = JObject.Parse(statusJson);
                }
                catch (Exception ex)
                {
                    CompleteError("command_error", "Invalid test_status JSON: " + ex.Message);
                    return;
                }
            }

            string state = ReadString(status, "status", "no_tests").ToLowerInvariant();
            if (state != "no_tests")
                s_noTestsFirstSeenUtcTicks = 0;

            switch (state)
            {
                case "running":
                    return;
                case "completed":
                    OnSegmentCompleted(status);
                    return;
                case "error":
                    CompleteError("command_error", ReadString(status, "message", "test run failed"));
                    return;
                case "cancelled":
                    CompleteError("cancelled", ReadString(status, "message", "test run cancelled"));
                    return;
                case "no_tests":
                    OnNoTestsStatus();
                    return;
                default:
                    CompleteError("command_error", "Unknown test_status state: " + state);
                    return;
            }
        }

        private static void OnSegmentCompleted(JObject status)
        {
            string currentSegment = SessionState.GetString(SessionKey_CurrentSegment, SegmentSingle);
            if (currentSegment == SegmentEditor)
            {
                SessionState.SetString(SessionKey_EditorSegmentResultJson, status.ToString(Formatting.None));
                SessionState.SetString(SessionKey_CurrentSegment, SegmentPlaymode);
                DeletePipelineStatusFile();
                StartSegment("playmode");
                return;
            }

            string editorJson = SessionState.GetString(SessionKey_EditorSegmentResultJson, string.Empty);
            JObject result = string.IsNullOrWhiteSpace(editorJson) ? BuildSingleResult(status) : BuildMergedResult(editorJson, status);
            CompleteSuccess(result);
        }

        private static void OnNoTestsStatus()
        {
            string currentSegment = SessionState.GetString(SessionKey_CurrentSegment, SegmentSingle);
            string editorJson = SessionState.GetString(SessionKey_EditorSegmentResultJson, string.Empty);
            if (currentSegment == SegmentPlaymode && !string.IsNullOrWhiteSpace(editorJson) && !PipelineRequestFileExists() && !PipelineStatusFileExists())
            {
                StartSegment("playmode");
                return;
            }

            long nowUtcTicks = DateTime.UtcNow.Ticks;
            if (s_noTestsFirstSeenUtcTicks == 0)
            {
                s_noTestsFirstSeenUtcTicks = nowUtcTicks;
                return;
            }

            if (nowUtcTicks - s_noTestsFirstSeenUtcTicks < TimeSpan.FromSeconds(NoTestsGraceSeconds).Ticks)
                return;

            CompleteError("command_error", "test run disappeared before completion");
        }

        private static JObject BuildSingleResult(JObject status)
        {
            JObject result = (JObject)status.DeepClone();
            result["success"] = true;
            result["command"] = "run_tests";
            return result;
        }

        private static JObject BuildMergedResult(string editorJson, JObject playmode)
        {
            JObject editor;
            try
            {
                editor = JObject.Parse(editorJson);
            }
            catch
            {
                editor = new JObject { ["status"] = "error", ["message"] = "failed to parse editor segment result" };
            }

            JObject summary = MergeSummary(editor["summary"] as JObject, playmode["summary"] as JObject);
            JArray results = new JArray();
            AppendArray(results, editor["results"] as JArray);
            AppendArray(results, playmode["results"] as JArray);

            double duration = ReadDouble(editor, "duration", 0) + ReadDouble(playmode, "duration", 0);
            return new JObject
            {
                ["status"] = "completed",
                ["success"] = true,
                ["command"] = "run_tests",
                ["mode"] = "All",
                ["duration"] = Math.Round(duration, 2),
                ["summary"] = summary,
                ["results"] = results,
                ["segments"] = new JObject
                {
                    ["editor"] = editor,
                    ["playmode"] = playmode,
                },
            };
        }

        private static JObject MergeSummary(JObject a, JObject b)
        {
            return new JObject
            {
                ["total"] = ReadInt(a, "total", 0) + ReadInt(b, "total", 0),
                ["passed"] = ReadInt(a, "passed", 0) + ReadInt(b, "passed", 0),
                ["failed"] = ReadInt(a, "failed", 0) + ReadInt(b, "failed", 0),
                ["skipped"] = ReadInt(a, "skipped", 0) + ReadInt(b, "skipped", 0),
                ["inconclusive"] = ReadInt(a, "inconclusive", 0) + ReadInt(b, "inconclusive", 0),
            };
        }

        private static void AppendArray(JArray target, JArray source)
        {
            if (source == null)
                return;
            foreach (JToken item in source)
                target.Add(item.DeepClone());
        }



        private static JObject CurrentParameters()
        {
            string text = SessionState.GetString(SessionKey_OriginalParametersJson, "{}");
            try
            {
                return JObject.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch
            {
                return new JObject();
            }
        }

        private static string NormalizeMode(string mode)
        {
            switch ((mode ?? "all").Trim().ToLowerInvariant())
            {
                case "":
                case "all":
                    return "all";
                case "editor":
                case "editmode":
                    return "editor";
                case "play":
                case "playmode":
                    return "playmode";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 播放模式下是否应拒绝该模式的测试运行：PlayMode 与 all（含 PlayMode 段）拒绝，EditorMode 放行。
        /// 独立为纯函数以便 EditMode 测试直接验证。
        /// </summary>
        private static bool ShouldRejectPlaymodeRun(string mode, bool isPlaying)
        {
            if (!isPlaying)
                return false;
            return mode == "playmode" || mode == "all";
        }

        /// <summary>
        /// 解析 dirtyAction 参数并对当前所有已打开场景应用策略（run_tests 专用）。
        /// 返回 null 表示可继续；返回非 null 为错误信息（请求应中止）。
        /// </summary>
        private static string ApplyDirtyPolicy(JObject parameters, string commandName)
        {
            string dirtyAction = ReadString(parameters, "dirty_action", "abort");
            var action = Capabilities.PipelineCommands.DirtyScenePolicy.Parse(dirtyAction, commandName, out string parseError);
            if (parseError != null)
                return parseError;

            var openScenes = new System.Collections.Generic.List<UnityEngine.SceneManagement.Scene>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                openScenes.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i));

            return Capabilities.PipelineCommands.DirtyScenePolicy.Apply(action, openScenes, commandName);
        }

        private static bool HasPendingRequest()
        {
            return !string.IsNullOrEmpty(SessionState.GetString(SessionKey_PendingTestRequestId, string.Empty));
        }

        private static bool IsPipelineTestRunRunning()
        {
            string statusJson = TestCommands.GetTestStatus();
            if (string.IsNullOrWhiteSpace(statusJson))
                return false;
            try
            {
                JObject status = JObject.Parse(statusJson);
                return string.Equals(ReadString(status, "status", string.Empty), "running", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CompleteSuccess(JObject value)
        {
            string id = SessionState.GetString(SessionKey_PendingTestRequestId, string.Empty);
            string output = value.ToString(Formatting.None);
            JObject result = new JObject
            {
                ["output"] = output,
                ["typeName"] = "pipeline_command",
                ["command"] = "run_tests",
                ["valueTypeName"] = "Unity.Pipeline.TestExecutionResponse",
                ["value"] = value,
            };

            ClearSessionState();
            s_completeJson?.Invoke(id, PiUnityJsonHelper.SuccessJson(id, result.ToString(Formatting.None)));
            StopPollingIfIdle();
        }

        private static void CompleteError(string errorType, string error)
        {
            string id = SessionState.GetString(SessionKey_PendingTestRequestId, string.Empty);
            ClearSessionState();
            s_completeJson?.Invoke(id, PiUnityJsonHelper.ErrorJson(id, errorType, error));
            StopPollingIfIdle();
        }

        private static void ClearSessionState()
        {
            SessionState.EraseString(SessionKey_PendingTestRequestId);
            SessionState.EraseString(SessionKey_OriginalParametersJson);
            SessionState.EraseString(SessionKey_CurrentSegment);
            SessionState.EraseString(SessionKey_EditorSegmentResultJson);
            SessionState.EraseString(SessionKey_DeadlineUtcTicks);
            s_noTestsFirstSeenUtcTicks = 0;
            s_startTask = null;
            s_startSegment = null;
        }

        private static bool IsTimedOut()
        {
            string ticksText = SessionState.GetString(SessionKey_DeadlineUtcTicks, string.Empty);
            if (string.IsNullOrEmpty(ticksText))
                return false;
            long ticks;
            if (!long.TryParse(ticksText, out ticks) || ticks <= 0)
                return false;
            return DateTime.UtcNow.Ticks > ticks;
        }

        private static bool PipelineStatusFileExists()
        {
            return File.Exists(ProjectRelativePath("Temp/pipeline_test_status.json"));
        }

        private static bool PipelineRequestFileExists()
        {
            return File.Exists(ProjectRelativePath("Temp/pipeline_test_request.json"));
        }

        private static void DeletePipelineStatusFile()
        {
            try
            {
                string path = ProjectRelativePath("Temp/pipeline_test_status.json");
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PiUnityHarness] failed to delete pipeline test status: " + ex.Message);
            }
        }

        private static string ProjectRelativePath(string relativePath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ReadString(JObject obj, string name, string defaultValue)
        {
            if (obj == null)
                return defaultValue;
            JToken token;
            if (obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out token) && token.Type != JTokenType.Null)
                return token.ToString();
            return defaultValue;
        }

        private static bool ReadBool(JObject obj, string name, bool defaultValue)
        {
            if (obj == null)
                return defaultValue;
            JToken token;
            if (!obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out token) || token.Type == JTokenType.Null)
                return defaultValue;
            try { return token.ToObject<bool>(); } catch { return defaultValue; }
        }

        private static int ReadInt(JObject obj, string name, int defaultValue)
        {
            if (obj == null)
                return defaultValue;
            JToken token;
            if (!obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out token) || token.Type == JTokenType.Null)
                return defaultValue;
            try { return token.ToObject<int>(); } catch { return defaultValue; }
        }

        private static double ReadDouble(JObject obj, string name, double defaultValue)
        {
            if (obj == null)
                return defaultValue;
            JToken token;
            if (!obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out token) || token.Type == JTokenType.Null)
                return defaultValue;
            try { return token.ToObject<double>(); } catch { return defaultValue; }
        }

#endif

        /// <summary>
        /// 是否有测试正在运行（含 pipeline 运行器占用）。
        /// 供编译守卫判断是否可安全重编译：测试运行中强制返回 busy，既避免打断测试，也避免在测试 PlayMode 中触发 Domain Reload 导致闪退。
        /// </summary>
        internal static bool IsTestRunInProgress()
        {
#if PI_UNITY_PIPELINE
            return HasPendingRequest() || PipelineTestRunRunningProvider();
#else
            return false;
#endif
        }
    }
}

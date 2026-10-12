using System;
using System.Collections.Generic;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using Pi.UnityHarness.Editor.Protocol;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Progress of one step job. Everything a handler needs to continue in a new domain
    /// must live here; it is serialized to SessionState after every tick.
    /// </summary>
    [Serializable]
    internal sealed class StepJobState
    {
        public string requestId;
        public string command;
        public string kind;
        public int step;
        public string data;
        public long deadlineUtcTicks;
        public int reloads;
    }

    internal enum StepJobStatus
    {
        Continue,
        Done,
        Failed,
    }

    internal struct StepOutcome
    {
        public StepJobStatus Status;
        public string ResultJson;
        public string ErrorType;
        public string Error;

        public static StepOutcome Continue()
        {
            return new StepOutcome { Status = StepJobStatus.Continue };
        }

        public static StepOutcome Done(string resultJson)
        {
            return new StepOutcome { Status = StepJobStatus.Done, ResultJson = resultJson };
        }

        public static StepOutcome Fail(string errorType, string error)
        {
            return new StepOutcome { Status = StepJobStatus.Failed, ErrorType = errorType, Error = error };
        }
    }

    internal interface IStepJobHandler
    {
        string Kind { get; }

        /// <summary>
        /// Main thread, once per editor update. Must return quickly; advance by mutating
        /// state.step / state.data. ResultJson of Done must be a raw JSON value.
        /// </summary>
        StepOutcome Step(StepJobState state);

        /// <summary>
        /// Called once in the new domain before stepping resumes. Objects from the old domain
        /// are gone, so progress must be re-derived from state and the editor itself.
        /// </summary>
        void OnRestored(StepJobState state);
    }

    /// <summary>
    /// Runs explicit, serializable multi-step jobs that survive domain reload. Unlike
    /// pipeline Tasks (aborted on reload), only handlers that can resume from persisted
    /// state belong here; arbitrary async work is not resumable.
    /// </summary>
    internal static class PiUnityStepJobRunner
    {
        private const string SessionKey_Jobs = "PiUnityHarness_StepJobs";
        private const string SessionKey_Completed = "PiUnityHarness_StepJobsCompleted";
        private const int DefaultTimeoutMs = 120000;
        private const int MaxCompletedResponses = 8;

        [Serializable]
        private sealed class StepJobList
        {
            public List<StepJobState> jobs = new List<StepJobState>();
        }

        [Serializable]
        private sealed class CompletedResponse
        {
            public string requestId;
            public string json;
        }

        [Serializable]
        private sealed class CompletedList
        {
            public List<CompletedResponse> items = new List<CompletedResponse>();
        }

        private static readonly Dictionary<string, IStepJobHandler> Handlers = new Dictionary<string, IStepJobHandler>(StringComparer.Ordinal);
        private static readonly List<StepJobState> Jobs = new List<StepJobState>();
        // Per-request callbacks only live in the domain that started the job; after a reload
        // every job completes through the callback handed to ResumeAfterReload.
        private static readonly Dictionary<string, Action<string, string>> Callbacks = new Dictionary<string, Action<string, string>>(StringComparer.Ordinal);
        private static Action<string, string> s_resumedCompleteJson;
        private static bool s_updateRegistered;

        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        static PiUnityStepJobRunner()
        {
            Register(new PiUnityPlayModeStepJob());
        }

        internal static void Register(IStepJobHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            Handlers[handler.Kind] = handler;
        }

        internal static int ActiveCount
        {
            get { return Jobs.Count; }
        }

        /// <summary>
        /// The broker fails in-flight requests when the domain starts reloading, and the CLI
        /// retries with the same request id. A retry reattaches to the restored job, or replays
        /// the response if the job already finished in the new domain.
        /// </summary>
        internal static void Start(string requestId, string command, string kind, string data, int timeoutMs, Action<string, string> completeJson)
        {
            if (Jobs.Exists(job => job.requestId == requestId))
            {
                Callbacks[requestId] = completeJson;
                return;
            }

            string replay = FindCompletedResponse(requestId);
            if (replay != null)
            {
                completeJson(requestId, replay);
                return;
            }

            if (!Handlers.ContainsKey(kind ?? string.Empty))
            {
                completeJson(requestId, PipeEnvelope.Error(requestId, "usage", "unknown step job kind: " + kind));
                return;
            }

            int budgetMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            Jobs.Add(new StepJobState
            {
                requestId = requestId,
                command = command,
                kind = kind,
                step = 0,
                data = data ?? string.Empty,
                deadlineUtcTicks = UtcNow().AddMilliseconds(budgetMs).Ticks,
                reloads = 0,
            });
            Callbacks[requestId] = completeJson;
            EnsureUpdateRegistered();
            Tick();
        }

        internal static bool HasPendingRequest()
        {
            return Jobs.Count > 0 || !string.IsNullOrEmpty(SessionState.GetString(SessionKey_Jobs, string.Empty));
        }

        internal static void BeforeReload()
        {
            Persist();
        }

        internal static void ResumeAfterReload(Action<string, string> completeJson)
        {
            s_resumedCompleteJson = completeJson;
            Jobs.Clear();
            Callbacks.Clear();
            Jobs.AddRange(Load());

            for (int i = Jobs.Count - 1; i >= 0; i--)
            {
                StepJobState job = Jobs[i];
                job.reloads++;
                if (!Handlers.TryGetValue(job.kind ?? string.Empty, out IStepJobHandler handler))
                {
                    Finish(i, StepOutcome.Fail("command_error", "step job kind '" + job.kind + "' has no handler after domain reload"));
                    continue;
                }

                try
                {
                    handler.OnRestored(job);
                }
                catch (Exception ex)
                {
                    Finish(i, StepOutcome.Fail("command_error", "step job '" + job.command + "' failed to restore: " + ex.GetType().Name + ": " + ex.Message));
                }
            }

            Persist();
            if (Jobs.Count > 0)
                EnsureUpdateRegistered();
        }

        internal static void Tick()
        {
            long nowTicks = UtcNow().Ticks;
            for (int i = Jobs.Count - 1; i >= 0; i--)
            {
                StepJobState job = Jobs[i];
                StepOutcome outcome;
                if (nowTicks > job.deadlineUtcTicks)
                {
                    outcome = StepOutcome.Fail("timeout", "step job '" + job.command + "' exceeded its deadline at step " + job.step + " (reloads=" + job.reloads + ")");
                }
                else
                {
                    try
                    {
                        outcome = Handlers[job.kind].Step(job);
                    }
                    catch (Exception ex)
                    {
                        outcome = StepOutcome.Fail("command_error", "step job '" + job.command + "' threw at step " + job.step + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                if (outcome.Status != StepJobStatus.Continue)
                    Finish(i, outcome);
            }

            Persist();
            if (Jobs.Count == 0)
                UnregisterUpdate();
        }

        internal static void ResetForTests()
        {
            Jobs.Clear();
            Callbacks.Clear();
            s_resumedCompleteJson = null;
            SessionState.EraseString(SessionKey_Jobs);
            SessionState.EraseString(SessionKey_Completed);
            UnregisterUpdate();
            UtcNow = () => DateTime.UtcNow;
        }

        internal static string BuildSuccessJson(string requestId, string command, string resultJson)
        {
            string value = string.IsNullOrEmpty(resultJson) ? "null" : resultJson;
            string result = "{\"output\":" + JsonText.Quote(value) +
                ",\"typeName\":\"pipeline_command\",\"command\":" + JsonText.Quote(command) +
                ",\"valueTypeName\":\"System.String\",\"value\":" + value + "}";
            return PipeEnvelope.Success(requestId, result);
        }

        private static void Finish(int index, StepOutcome outcome)
        {
            StepJobState job = Jobs[index];
            Jobs.RemoveAt(index);

            Action<string, string> complete;
            if (!Callbacks.TryGetValue(job.requestId, out complete))
                complete = s_resumedCompleteJson;
            Callbacks.Remove(job.requestId);

            string json = outcome.Status == StepJobStatus.Done
                ? BuildSuccessJson(job.requestId, job.command, outcome.ResultJson)
                : PipeEnvelope.Error(job.requestId, outcome.ErrorType ?? "command_error", outcome.Error ?? "step job failed");
            RememberCompletedResponse(job.requestId, json);

            if (complete == null)
            {
                Debug.LogWarning("[PiUnityHarness] step job '" + job.command + "' finished without a completion callback: " + json);
                return;
            }

            try
            {
                complete(job.requestId, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PiUnityHarness] step job completion failed (" + job.command + "): " + ex.Message);
            }
        }

        private static string FindCompletedResponse(string requestId)
        {
            foreach (CompletedResponse item in LoadCompleted().items)
            {
                if (item.requestId == requestId)
                    return item.json;
            }
            return null;
        }

        private static void RememberCompletedResponse(string requestId, string json)
        {
            CompletedList list = LoadCompleted();
            list.items.RemoveAll(item => item.requestId == requestId);
            list.items.Add(new CompletedResponse { requestId = requestId, json = json });
            if (list.items.Count > MaxCompletedResponses)
                list.items.RemoveRange(0, list.items.Count - MaxCompletedResponses);
            SessionState.SetString(SessionKey_Completed, JsonUtility.ToJson(list));
        }

        private static CompletedList LoadCompleted()
        {
            string json = SessionState.GetString(SessionKey_Completed, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new CompletedList();
            try
            {
                CompletedList list = JsonUtility.FromJson<CompletedList>(json);
                return list != null && list.items != null ? list : new CompletedList();
            }
            catch (Exception)
            {
                return new CompletedList();
            }
        }

        private static void Persist()
        {
            if (Jobs.Count == 0)
            {
                SessionState.EraseString(SessionKey_Jobs);
                return;
            }

            var list = new StepJobList();
            list.jobs.AddRange(Jobs);
            SessionState.SetString(SessionKey_Jobs, JsonUtility.ToJson(list));
        }

        private static List<StepJobState> Load()
        {
            string json = SessionState.GetString(SessionKey_Jobs, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new List<StepJobState>();

            try
            {
                StepJobList list = JsonUtility.FromJson<StepJobList>(json);
                return list != null && list.jobs != null ? list.jobs : new List<StepJobState>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PiUnityHarness] step job state unreadable after reload: " + ex.Message);
                SessionState.EraseString(SessionKey_Jobs);
                return new List<StepJobState>();
            }
        }

        private static void EnsureUpdateRegistered()
        {
            if (s_updateRegistered)
                return;
            s_updateRegistered = true;
            EditorApplication.update += Tick;
        }

        private static void UnregisterUpdate()
        {
            if (!s_updateRegistered)
                return;
            s_updateRegistered = false;
            EditorApplication.update -= Tick;
        }
    }
}

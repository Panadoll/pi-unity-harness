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
    internal static partial class PiUnityBridge
    {
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

        // --- Coroutine / Task result handling ---

        /// <summary>
        /// If EvalResult contains an IEnumerator coroutine or Task/Task-like, completes asynchronously;
        /// otherwise completes synchronously.
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
                // Coroutine result: hand it to the pump for per-frame driving
                int coroutineTimeoutMs = timeoutMs > 0 ? timeoutMs : 60000;
                bool queued = s_pump != null && s_pump.Enqueue(result.Coroutine, id,
                    (success, text, typeName) =>
                    {
                        if (success)
                            CompleteJson(id, PiUnityJsonHelper.EvalResultJson(id, text ?? string.Empty, typeName ?? "void", TimingFragment(timing)));
                        else
                            CompleteError(id, text ?? "coroutine_failed", typeName ?? "runtime_error");
                    }, coroutineTimeoutMs);
                if (!queued)
                    CompleteError(id, "coroutine queue full", "busy");
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
                CompleteError(id, text ?? "async_eval_failed", typeName ?? "runtime_error");
                return;
            }

            CompleteJson(id, PiUnityJsonHelper.EvalResultJson(id, text ?? string.Empty, typeName ?? "void", TimingFragment(timing)));
        }

        private static void CancelAllPendingAsyncEvals(string reason)
        {
            if (s_asyncEvalPump == null)
                return;

            s_asyncEvalPump.CancelAll((id, success, text, typeName, state) =>
            {
                CompleteError(id, text ?? reason, typeName ?? "cancelled");
            }, reason);
        }

        // --- Compile requests ---
        private static string TimingFragment(RequestTiming timing)
        {
            return timing != null ? ",\"timing\":" + timing.ToJsonFragment() : string.Empty;
        }

        private static void CompleteEvalResult(string id, PiUnityEvaluator.EvalResult result, RequestTiming timing)
        {
            if (!result.Ok)
            {
                CompleteError(id, result.Error ?? "execute_failed", "runtime_error");
                return;
            }

            CompleteJson(id, PiUnityJsonHelper.EvalResultJson(
                id, result.Output ?? string.Empty, result.TypeName ?? string.Empty, TimingFragment(timing)));
        }

        private static void CompleteValidateThenEvalResult(string id, string code, RequestTiming timing, int timeoutMs)
        {
            long validateStart = Stopwatch.GetTimestamp();
            string validation = s_evaluator.Validate(code);
            timing.ValidateMs = RequestTiming.TicksToMs(Stopwatch.GetTimestamp() - validateStart);
            if (!IsValidationOk(validation))
            {
                CompleteError(id, validation ?? "validate_failed", "compile_error");
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

            CompleteJson(id, PiUnityJsonHelper.EvalResultJson(id, validation, "validation"));
        }

        private static bool IsValidationOk(string validation)
        {
            return !string.IsNullOrEmpty(validation) && !validation.StartsWith("COMPILE ERROR", StringComparison.Ordinal);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Coordinates compile requests.
    ///
    /// Leverages the domain-reload-stable Rust native broker of pi-unity-harness so
    /// compile results are returned directly over the pipe instead of file polling.
    ///
    /// Domain reload destroys C# static fields, so compile state is stashed in
    /// SessionState and restored in OnAfterReload.
    ///
    /// The callback fires exactly once per compile (success or failure).
    /// </summary>
    internal static class PiUnityCompileCoordinator
    {
        private const string SessionKey_PendingCompileId = "PiUnityHarness_PendingCompileId";
        private const string SessionKey_CompileCallbackFired = "PiUnityHarness_CompileCallbackFired";
        private const string SessionKey_CompileHasErrors = "PiUnityHarness_CompileHasErrors";
        private const string SessionKey_CompileErrorSummary = "PiUnityHarness_CompileErrorSummary";

        private static string s_pendingRequestId;
        private static List<CompilerMessage> s_collectedMessages;
        private static bool s_callbackFired;
        private static bool s_callbacksRegistered;
        private static int s_deferredNoCompileChecks;
        private static Action<string, bool, string, string> s_onComplete;

        /// <summary>
        /// Unified recompile entry: wait for a safe EditMode, then start compile.
        /// </summary>
        public static void RequestRecompile(string requestId, Action<string, bool, string, string> onComplete)
        {
            PiUnityRecompileGuard.RequestRecompile(requestId, onComplete);
        }

        /// <summary>
        /// After domain reload: resume a PlayMode-exit wait, then complete any pending compile result.
        /// </summary>
        public static void ResumeAfterReload(Action<string, bool, string, string> onComplete)
        {
            PiUnityRecompileGuard.ResumeAfterReload(onComplete);
            FinalizeAfterReload(onComplete);
        }

        /// <summary>
        /// Starts a compile request.
        /// </summary>
        public static void StartCompile(
            string requestId,
            Action<string, bool, string, string> onComplete)
        {
            string persistedPendingId = SessionState.GetString(SessionKey_PendingCompileId, "");
            if (!string.IsNullOrEmpty(s_pendingRequestId) || !string.IsNullOrEmpty(persistedPendingId))
            {
                onComplete(requestId, false, "busy", "compile request already in progress");
                return;
            }

            Log("[PiUnityHarness] compile start id=" + requestId);

            s_pendingRequestId = requestId;
            s_callbackFired = false;
            s_onComplete = onComplete;
            s_collectedMessages = new List<CompilerMessage>();
            EnsureCallbacksRegistered();

            // Persist the request id to SessionState (restored after domain reload)
            SessionState.SetString(SessionKey_PendingCompileId, requestId);
            SessionState.SetBool(SessionKey_CompileCallbackFired, false);
            SessionState.SetBool(SessionKey_CompileHasErrors, false);
            SessionState.SetString(SessionKey_CompileErrorSummary, "");

            // 先处理外部文件改动：编辑器不在前台时 Unity 不会自动刷新，
            // 少了这一步，"改完文件直接 compile" 会看不到新/改动脚本，静默地什么都不编译。
            // 上游 uloop 的 compile 路径同样是先 Refresh 再请求编译。
            AssetDatabase.Refresh();

            // Explicitly request script compilation. With only AssetDatabase.Refresh, dirty scripts /
            // asset-only refreshes can leave isCompiling=true without ever firing compilationFinished,
            // which would hang the pipe-side recompile until timeout.
            CompilationPipeline.RequestScriptCompilation();

            // Unity may only enter isCompiling on a later editor update.
            // Defer a few frames before deciding "no compilation was triggered" to avoid
            // reporting success too early; even when already compiling, keep a fallback so
            // a missed compilationFinished cannot hang the request.
            if (!s_callbackFired)
                ScheduleDeferredNoCompileCheck(8);
        }

        /// <summary>
        /// Called after domain reload; checks and completes a pending compile request.
        /// Only sends a result when the compilationFinished callback did not fire.
        /// Returns true when a pending result existed and was sent through the callback.
        /// </summary>
        public static bool FinalizeAfterReload(Action<string, bool, string, string> onCompleteFallback)
        {
            string pendingId = SessionState.GetString(SessionKey_PendingCompileId, "");
            if (string.IsNullOrEmpty(pendingId))
                return false;

            bool callbackFired = SessionState.GetBool(SessionKey_CompileCallbackFired, false);

            // If the callback already fired, the response was sent by OnCompilationFinished; no duplicate.
            if (callbackFired)
            {
                Log("[PiUnityHarness] compile finalized after reload (already sent) id=" + pendingId);
                ClearSessionState();
                return true;
            }

            Log("[PiUnityHarness] compile finalized after reload (callback missed) id=" + pendingId);

            // Callback did not fire — check the Console window
            string consoleErrors = TryGetConsoleCompileErrors();
            if (!string.IsNullOrEmpty(consoleErrors))
            {
                onCompleteFallback(pendingId, false, "compilation_failed", consoleErrors);
            }
            else
            {
                // A domain reload usually means compilation succeeded
                onCompleteFallback(pendingId, true, "compilation_succeeded", "");
            }

            ClearSessionState();
            return true;
        }

        private static void ScheduleDeferredNoCompileCheck(int frames)
        {
            s_deferredNoCompileChecks = Math.Max(1, frames);
            EditorApplication.delayCall += OnDeferredNoCompileCheck;
        }

        private static void OnDeferredNoCompileCheck()
        {
            if (string.IsNullOrEmpty(s_pendingRequestId) || s_callbackFired)
                return;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                // 仍在编译/刷新中：顺延检查，避免检查链丢失导致请求永不回调（pipe 端只能等超时）
                EditorApplication.delayCall += OnDeferredNoCompileCheck;
                return;
            }

            s_deferredNoCompileChecks--;
            if (s_deferredNoCompileChecks > 0)
            {
                EditorApplication.delayCall += OnDeferredNoCompileCheck;
                return;
            }

            string requestId = s_pendingRequestId;
            string consoleErrors = TryGetConsoleCompileErrors();
            if (!string.IsNullOrEmpty(consoleErrors))
            {
                s_callbackFired = true;
                SessionState.SetBool(SessionKey_CompileCallbackFired, true);
                SessionState.SetBool(SessionKey_CompileHasErrors, true);
                SessionState.SetString(SessionKey_CompileErrorSummary, consoleErrors);
                s_onComplete?.Invoke(requestId, false, "compilation_failed", consoleErrors);
            }
            else
            {
                s_onComplete?.Invoke(requestId, true, "compilation_succeeded", "");
            }

            s_pendingRequestId = null;
            s_onComplete = null;
            ClearSessionState();
        }

        // --- Compilation pipeline callbacks ---

        private static void EnsureCallbacksRegistered()
        {
            if (s_callbacksRegistered)
                return;

            s_callbacksRegistered = true;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (s_pendingRequestId == null)
                return;

            foreach (CompilerMessage msg in messages)
            {
                if (msg.type == CompilerMessageType.Error || msg.type == CompilerMessageType.Warning)
                    s_collectedMessages.Add(msg);
            }
        }

        private static void OnCompilationFinished(object obj)
        {
            string requestId = s_pendingRequestId;
            if (requestId == null)
                return;

            s_pendingRequestId = null;
            s_callbackFired = true;

            bool hasErrors = false;
            string errorSummary = "";

            // Collect compile errors
            List<CompilerMessage> errors = new List<CompilerMessage>();
            foreach (CompilerMessage msg in s_collectedMessages)
            {
                if (msg.type == CompilerMessageType.Error)
                {
                    hasErrors = true;
                    errors.Add(msg);
                }
            }

            // Build a human-readable error summary
            if (hasErrors)
            {
                StringBuilder summarySb = new StringBuilder();
                int errorCount = 0;
                foreach (CompilerMessage msg in errors)
                {
                    if (errorCount > 0)
                        summarySb.Append("\n");
                    summarySb.Append(msg.file);
                    summarySb.Append("(");
                    summarySb.Append(msg.line);
                    summarySb.Append(",");
                    summarySb.Append(msg.column);
                    summarySb.Append("): ");
                    summarySb.Append(msg.message);
                    errorCount++;
                    if (errorCount >= 10)
                    {
                        summarySb.Append("\n... and more errors");
                        break;
                    }
                }
                errorSummary = summarySb.ToString();
            }

            s_collectedMessages.Clear();

            // Persist to SessionState (so OnAfterReload can read it if a reload follows)
            SessionState.SetBool(SessionKey_CompileCallbackFired, true);
            SessionState.SetBool(SessionKey_CompileHasErrors, hasErrors);
            SessionState.SetString(SessionKey_CompileErrorSummary, errorSummary);

            Log("[PiUnityHarness] compile finished id=" + requestId +
                " hasErrors=" + hasErrors + " errors=" + errors.Count);

            // Send the result directly through the callback. Once the result reaches the native broker,
            // SessionState can be cleared so a later domain reload does not process it again.
            if (s_onComplete != null)
            {
                if (hasErrors)
                    s_onComplete(requestId, false, "compilation_failed", errorSummary);
                else
                    s_onComplete(requestId, true, "compilation_succeeded", "");
            }
            s_onComplete = null;
            ClearSessionState();
        }

        // --- Console compile-error reading ---

        private static string TryGetConsoleCompileErrors()
        {
            try
            {
                // Read UnityEditor.LogEntries internal API via reflection
                System.Reflection.Assembly editorAssembly = typeof(EditorWindow).Assembly;
                Type logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType == null)
                    return null;

                System.Reflection.MethodInfo getCountMethod = logEntriesType.GetMethod(
                    "GetCount", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (getCountMethod == null)
                    return null;

                int count = (int)getCountMethod.Invoke(null, null);
                if (count == 0)
                    return null;

                System.Reflection.MethodInfo startMethod = logEntriesType.GetMethod(
                    "StartGettingEntries", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                System.Reflection.MethodInfo getEntryMethod = logEntriesType.GetMethod(
                    "GetEntryInternal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                System.Reflection.MethodInfo endMethod = logEntriesType.GetMethod(
                    "EndGettingEntries", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

                if (startMethod == null || getEntryMethod == null || endMethod == null)
                    return null;

                Type logEntryType = editorAssembly.GetType("UnityEditor.LogEntry");
                if (logEntryType == null)
                    return null;

                startMethod.Invoke(null, null);

                StringBuilder errors = new StringBuilder();
                int errorsFound = 0;
                try
                {
                    for (int i = 0; i < count && errorsFound < 20; i++)
                    {
                        object logEntry = Activator.CreateInstance(logEntryType);
                        getEntryMethod.Invoke(null, new object[] { i, logEntry });

                        // LogEntry.mode is a bitmask and varies across Unity versions; filter compile
                        // errors directly by message content instead.
                        System.Reflection.FieldInfo messageField = logEntryType.GetField("message");
                        string message = messageField != null ? (string)messageField.GetValue(logEntry) : "";
                        if (!string.IsNullOrEmpty(message) &&
                            (message.Contains("error CS") || message.Contains(": error ")))
                        {
                            if (errorsFound > 0)
                                errors.Append("\n");
                            errors.Append(message);
                            errorsFound++;
                        }
                    }
                }
                finally
                {
                    endMethod.Invoke(null, null);
                }

                return errorsFound > 0 ? errors.ToString() : null;
            }
            catch (Exception ex)
            {
                Log("[PiUnityHarness] Console error read failed: " + ex.Message);
                return null;
            }
        }

        // --- Utility methods ---

        private static void ClearSessionState()
        {
            SessionState.EraseString(SessionKey_PendingCompileId);
            SessionState.EraseBool(SessionKey_CompileCallbackFired);
            SessionState.EraseBool(SessionKey_CompileHasErrors);
            SessionState.EraseString(SessionKey_CompileErrorSummary);
        }

        private static void Log(string message)
        {
            Debug.Log(message);
        }
    }
}

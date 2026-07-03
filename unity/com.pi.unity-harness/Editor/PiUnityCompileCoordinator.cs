using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// 编译请求协调器。
    ///
    /// 利用 pi-unity-harness 的 Rust native broker 域重载稳定特性，
    /// 编译结果直接通过 pipe 回传，不需要文件轮询。
    ///
    /// 域重载会销毁 C# 静态字段，所以使用 SessionState 暂存编译状态，
    /// 在 OnAfterReload 中恢复。
    ///
    /// 回调只会在编译完成时调用一次（成功或失败）。
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
        /// 开始编译请求。
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

            // 持久化请求 id 到 SessionState（域重载后恢复用）
            SessionState.SetString(SessionKey_PendingCompileId, requestId);
            SessionState.SetBool(SessionKey_CompileCallbackFired, false);
            SessionState.SetBool(SessionKey_CompileHasErrors, false);
            SessionState.SetString(SessionKey_CompileErrorSummary, "");

            // 触发刷新——可能导致编译 + 域重载
            AssetDatabase.Refresh();

            // Unity 2019.3+ 可能在后续 editor update 才进入 isCompiling。
            // 延迟数帧再判断“没有触发编译”，避免过早返回成功。
            if (!s_callbackFired && !EditorApplication.isCompiling)
                ScheduleDeferredNoCompileCheck(2);
            // 如果 isCompiling 为 true，等待 compilationFinished 回调
        }

        /// <summary>
        /// 域重载后调用，检查并完成待定编译请求。
        /// 仅当 compilationFinished 回调未触发时发送结果。
        /// 返回 true 表示有待定结果且已通过回调发送。
        /// </summary>
        public static bool FinalizeAfterReload(Action<string, bool, string, string> onCompleteFallback)
        {
            string pendingId = SessionState.GetString(SessionKey_PendingCompileId, "");
            if (string.IsNullOrEmpty(pendingId))
                return false;

            bool callbackFired = SessionState.GetBool(SessionKey_CompileCallbackFired, false);

            // 如果回调已经触发，响应已由 OnCompilationFinished 发送，无需重复。
            if (callbackFired)
            {
                Log("[PiUnityHarness] compile finalized after reload (already sent) id=" + pendingId);
                ClearSessionState();
                return true;
            }

            Log("[PiUnityHarness] compile finalized after reload (callback missed) id=" + pendingId);

            // 回调未触发——检查 Console 窗口
            string consoleErrors = TryGetConsoleCompileErrors();
            if (!string.IsNullOrEmpty(consoleErrors))
            {
                onCompleteFallback(pendingId, false, "compilation_failed", consoleErrors);
            }
            else
            {
                // 域重载通常意味着编译成功
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

            if (EditorApplication.isCompiling)
                return;

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

        // --- 编译管线回调 ---

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

            // 收集编译错误
            List<CompilerMessage> errors = new List<CompilerMessage>();
            foreach (CompilerMessage msg in s_collectedMessages)
            {
                if (msg.type == CompilerMessageType.Error)
                {
                    hasErrors = true;
                    errors.Add(msg);
                }
            }

            // 构造人类可读的错误摘要
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

            // 持久化到 SessionState（如果接下来域重载，OnAfterReload 可以读取）
            SessionState.SetBool(SessionKey_CompileCallbackFired, true);
            SessionState.SetBool(SessionKey_CompileHasErrors, hasErrors);
            SessionState.SetString(SessionKey_CompileErrorSummary, errorSummary);

            Log("[PiUnityHarness] compile finished id=" + requestId +
                " hasErrors=" + hasErrors + " errors=" + errors.Count);

            // 直接通过回调发送结果。结果进入 native broker 后即可清理 SessionState，避免后续域重载重复处理。
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

        // --- Console 编译错误读取 ---

        private static string TryGetConsoleCompileErrors()
        {
            try
            {
                // 通过反射读取 UnityEditor.LogEntries 内部 API
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

                        // LogEntry.mode 是位掩码且 Unity 版本差异大；直接按消息内容过滤编译错误。
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

        // --- 工具方法 ---

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

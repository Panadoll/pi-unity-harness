using System;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// 重编译前防御守卫。
    ///
    /// 背景：在 PlayMode 运行中直接触发强制同步重编译时，Unity 会在 BeginReloadAssembly
    /// 阶段通过 DirectorManager::BeforeLoadAllAssemblies 销毁活跃的 PlayableGraph，并向托管侧
    /// 回调 OnPlayableDestroy。此时旧 AppDomain 正处于卸载中，GC handle 已失效（指向上一域），
    /// mono 在 JIT 调用该托管方法时直接 SIGSEGV，编辑器闪退。
    ///
    /// 防御：触发编译前若编辑器处于 PlayMode（或 PlayMode 切换中），先强制安全退出 PlayMode，
    /// 等待 Playable 等非托管资源完全释放、进入 EditMode 稳定后再发起编译。
    /// 等待状态持久化到 SessionState，可跨 Domain Reload 存活（PlayMode 退出本身可能触发 reload）。
    /// </summary>
    internal static class PiUnityRecompileGuard
    {
        private const string SessionKey_PendingId = "PiUnityHarness_WaitPlaymodeCompileId";
        private const string SessionKey_DeadlineUtcTicks = "PiUnityHarness_WaitPlaymodeDeadlineUtcTicks";
        private const double WaitTimeoutSeconds = 60.0d;

        /// <summary>可注入的 isPlaying 委托（默认取 EditorApplication.isPlaying），测试可替换以模拟播放状态。</summary>
        internal static Func<bool> IsPlayingProvider = () => EditorApplication.isPlaying;

        /// <summary>可注入的 isPlayingOrWillChangePlaymode 委托，测试可替换以模拟播放状态切换。</summary>
        internal static Func<bool> WillChangePlaymodeProvider = () => EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>可注入的退出 PlayMode 动作（默认 EditorApplication.ExitPlaymode），测试替身可避免真实退出。</summary>
        internal static Action ExitPlaymodeAction = EditorApplication.ExitPlaymode;

        /// <summary>可注入的测试运行检测委托（默认走 PiUnityTestCoordinator），测试替身可避免依赖真实测试状态。</summary>
        internal static Func<bool> IsTestRunInProgressProvider = () => PiUnityTestCoordinator.IsTestRunInProgress();

        /// <summary>可注入的时钟（默认 DateTime.UtcNow），测试可控制超时。</summary>
        internal static Func<DateTime> UtcNowProvider = () => DateTime.UtcNow;

        /// <summary>可注入的编译启动器（默认 PiUnityCompileCoordinator.StartCompile），测试替身可断言调用。</summary>
        internal static Action<string, Action<string, bool, string, string>> CompileStarter = PiUnityCompileCoordinator.StartCompile;

        /// <summary>可注入的 isCompiling 委托，测试可在套件运行期间避开真实编译态。</summary>
        internal static Func<bool> IsCompilingProvider = () => EditorApplication.isCompiling;

        /// <summary>可注入的 isUpdating 委托，测试可在资源刷新期间避开真实更新态。</summary>
        internal static Func<bool> IsUpdatingProvider = () => EditorApplication.isUpdating;

        private static bool s_handlerRegistered;
        private static bool s_waitingPlayModeExit;
        private static Action<string, bool, string, string> s_onComplete;

        /// <summary>
        /// 重编译统一入口：确保 PlayMode 已安全退出后再发起编译。
        /// </summary>
        public static void RequestRecompile(string requestId, Action<string, bool, string, string> onComplete)
        {
            string queuedId = SessionState.GetString(SessionKey_PendingId, "");
            if (!string.IsNullOrEmpty(queuedId))
            {
                if (queuedId == requestId)
                    return; // 同请求重入，忽略
                onComplete(requestId, false, "busy", "recompile already queued: waiting for PlayMode to exit");
                return;
            }

            // pipeline 测试运行中：不打断测试，也不冒险在测试 PlayMode 中 reload
            if (IsTestRunInProgressProvider())
            {
                onComplete(requestId, false, "busy", "test run in progress; stop tests before recompiling");
                return;
            }

            if (!WillChangePlaymodeProvider())
            {
                CompileStarter(requestId, onComplete);
                return;
            }

            Log("[PiUnityHarness] recompile deferred until PlayMode exits (id=" + requestId + ")");
            s_waitingPlayModeExit = true;
            s_onComplete = onComplete;
            SessionState.SetString(SessionKey_PendingId, requestId);
            SessionState.SetString(SessionKey_DeadlineUtcTicks,
                UtcNowProvider().AddSeconds(WaitTimeoutSeconds).Ticks.ToString());

            EnsureHandlerRegistered();
            if (IsPlayingProvider())
                ExitPlaymodeAction();
        }

        /// <summary>
        /// 域名重载后恢复（静态字段已随 reload 丢失，但 SessionState 保留）。由 PiUnityBridge.OnAfterReload 调用。
        /// </summary>
        public static void ResumeAfterReload(Action<string, bool, string, string> onCompleteFallback)
        {
            if (string.IsNullOrEmpty(SessionState.GetString(SessionKey_PendingId, "")))
                return;

            s_waitingPlayModeExit = true;
            s_onComplete = onCompleteFallback;
            EnsureHandlerRegistered();

            if (WillChangePlaymodeProvider())
            {
                if (IsPlayingProvider())
                    ExitPlaymodeAction();
                // 否则等待 PlayMode 状态稳定
            }
            else
            {
                // 非 PlayMode 触发的 reload（无 EnteredEditMode 事件）：延迟一帧后继续
                EditorApplication.delayCall += ContinueCompileIfSafe;
            }
        }

        // --- 状态机 ---

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!s_waitingPlayModeExit)
                return;

            switch (state)
            {
                case PlayModeStateChange.EnteredEditMode:
                    // PlayMode 已安全退出，场景非托管资源已释放；延迟几帧等资源清理完成
                    EditorApplication.delayCall += ContinueCompileIfSafe;
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    // 等待期间 PlayMode 被重新进入（手动操作）：再次退出并顺延超时
                    Log("[PiUnityHarness] play mode re-entered while compile queued; exiting again");
                    ResetDeadline();
                    ExitPlaymodeAction();
                    break;
            }
        }

        private static void OnEditorUpdate()
        {
            if (!s_waitingPlayModeExit)
                return;

            if (IsTimedOut())
                FailTimeout();
        }

        private static void ContinueCompileIfSafe()
        {
            if (!s_waitingPlayModeExit)
                return;

            string pendingId = SessionState.GetString(SessionKey_PendingId, "");
            if (string.IsNullOrEmpty(pendingId))
            {
                s_waitingPlayModeExit = false;
                s_onComplete = null;
                return;
            }

            // 仍在 PlayMode 切换中：继续等
            if (WillChangePlaymodeProvider())
            {
                if (IsPlayingProvider())
                    ExitPlaymodeAction();
                return;
            }

            // Unity 仍在编译/刷新（退出 PlayMode 时可能自动触发）：等稳定再发起
            if (IsCompilingProvider() || IsUpdatingProvider())
            {
                EditorApplication.delayCall += ContinueCompileIfSafe;
                return;
            }

            Action<string, bool, string, string> onComplete = s_onComplete;
            s_waitingPlayModeExit = false;
            s_onComplete = null;
            SessionState.EraseString(SessionKey_PendingId);
            SessionState.EraseString(SessionKey_DeadlineUtcTicks);

            Log("[PiUnityHarness] play mode exited cleanly, starting compile id=" + pendingId);
            if (onComplete == null)
            {
                Log("[PiUnityHarness] ERROR: compile callback lost for id=" + pendingId + "; compile not started");
                return;
            }
            CompileStarter(pendingId, onComplete);
        }

        // --- 超时与工具 ---

        private static void FailTimeout()
        {
            string pendingId = SessionState.GetString(SessionKey_PendingId, "");
            Action<string, bool, string, string> onComplete = s_onComplete;
            s_waitingPlayModeExit = false;
            s_onComplete = null;
            SessionState.EraseString(SessionKey_PendingId);
            SessionState.EraseString(SessionKey_DeadlineUtcTicks);

            Log("[PiUnityHarness] recompile deferred too long, aborting id=" + pendingId);
            onComplete?.Invoke(pendingId, false, "playmode_exit_timeout",
                "timed out waiting for PlayMode to exit (" + WaitTimeoutSeconds + "s) before recompiling");
        }

        private static bool IsTimedOut()
        {
            string ticksStr = SessionState.GetString(SessionKey_DeadlineUtcTicks, "");
            if (string.IsNullOrEmpty(ticksStr))
                return false;
            long deadlineTicks;
            if (!long.TryParse(ticksStr, out deadlineTicks))
                return false;
            return UtcNowProvider().Ticks > deadlineTicks;
        }

        private static void ResetDeadline()
        {
            SessionState.SetString(SessionKey_DeadlineUtcTicks,
                UtcNowProvider().AddSeconds(WaitTimeoutSeconds).Ticks.ToString());
        }

        /// <summary>测试专用：注销编辑器事件挂接，避免测试间重复注册。</summary>
        internal static void UnregisterHandlersForTest()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;
            s_handlerRegistered = false;
        }

        private static void EnsureHandlerRegistered()
        {
            if (s_handlerRegistered)
                return;
            s_handlerRegistered = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void Log(string message)
        {
            Debug.Log(message);
        }
    }
}
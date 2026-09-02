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
        private static void PublishBackgroundHeartbeat(long nowTicks)
        {
            if (!s_started)
                return;

            try
            {
                // Always refresh native last_heartbeat so reap_timeouts does not fail in-flight work.
                pi_unity_managed_heartbeat(s_generation);
                Interlocked.Exchange(ref s_lastNativeHeartbeatUtcTicks, nowTicks);

                bool mainThreadStale =
                    nowTicks - Interlocked.Read(ref s_lastMainThreadPumpUtcTicks)
                    > TimeSpan.FromSeconds(HeartbeatStaleSeconds).Ticks;
                if (mainThreadStale)
                {
                    string baseStatus = Volatile.Read(ref s_lastMainThreadEditorStatus) ?? "editing";
                    string status = ComposeBlockedEditorStatus(baseStatus, mainThreadStale: true);
                    byte[] statusBytes = Utf8(status);
                    pi_unity_set_managed_state(ManagedStateReady, s_generation, statusBytes, statusBytes.Length);
                }
            }
            catch
            {
                // Background path must never take down the pump thread.
            }
        }

        private static string ComposeBlockedEditorStatus(string baseStatus, bool mainThreadStale)
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

            if (mainThreadStale && mode == "editing")
                mode = "blocked";

            string flags = string.Empty;
            if (mainThreadStale)
                flags += ";mainThreadStale=1";

            return mode + rest + flags;
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
    }
}

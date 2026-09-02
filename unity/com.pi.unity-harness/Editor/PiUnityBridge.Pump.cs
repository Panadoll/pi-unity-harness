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
    }
}

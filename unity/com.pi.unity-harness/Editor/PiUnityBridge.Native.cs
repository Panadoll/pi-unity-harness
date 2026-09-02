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

                pi_unity_managed_heartbeat(s_generation);
                PublishManagedState(ManagedStateReady, status);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[PiUnityHarness] heartbeat failed: " + ex);
            }
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

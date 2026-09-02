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
    }
}

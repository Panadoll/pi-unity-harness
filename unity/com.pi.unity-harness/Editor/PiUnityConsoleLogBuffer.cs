using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// 在托管域生命周期内保留最近的 Unity 日志，供上下文快照与 Pipeline 命令复用。
    /// </summary>
    [InitializeOnLoad]
    internal static class PiUnityConsoleLogBuffer
    {
        private const int MaxLogs = 1000;
        private const int MaxMessageLength = 4000;
        private const int MaxStackTraceLength = 8000;
        private static readonly object Gate = new object();
        private static readonly List<Entry> Entries = new List<Entry>();

        static PiUnityConsoleLogBuffer()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceived += OnLogMessageReceived;
        }

        public static List<Entry> Get(int limit, string level = null)
        {
            if (limit <= 0)
                return new List<Entry>();

            lock (Gate)
            {
                IEnumerable<Entry> query = Entries;
                if (!string.IsNullOrWhiteSpace(level) && !string.Equals(level, "all", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(entry => MatchesLevel(entry.type, level));

                int skip = Math.Max(0, query.Count() - Math.Min(limit, MaxLogs));
                return query.Skip(skip).Select(entry => entry.Clone()).ToList();
            }
        }

        public static void Clear()
        {
            lock (Gate)
                Entries.Clear();
        }

        private static bool MatchesLevel(string actual, string requested)
        {
            if (string.Equals(requested, "error", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(actual, LogType.Error.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(actual, LogType.Exception.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(actual, LogType.Assert.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(requested, "info", StringComparison.OrdinalIgnoreCase))
                requested = LogType.Log.ToString();

            return string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase);
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            Entry entry = new Entry
            {
                message = Truncate(condition, MaxMessageLength),
                stackTrace = Truncate(stackTrace, MaxStackTraceLength),
                type = type.ToString(),
                time = DateTime.UtcNow.ToString("o"),
            };

            lock (Gate)
            {
                Entries.Add(entry);
                if (Entries.Count > MaxLogs)
                    Entries.RemoveRange(0, Entries.Count - MaxLogs);
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "\n...[truncated]";
        }

        internal sealed class Entry
        {
            public string message;
            public string stackTrace;
            public string type;
            public string time;

            public Entry Clone()
            {
                return new Entry
                {
                    message = message,
                    stackTrace = stackTrace,
                    type = type,
                    time = time,
                };
            }
        }
    }
}

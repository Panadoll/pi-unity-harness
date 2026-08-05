using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor
{
    /// <summary>
    /// Keeps the most recent Unity logs within the managed domain lifetime, for context snapshots and Pipeline commands.
    /// </summary>
    [InitializeOnLoad]
    internal static class PiUnityConsoleLogBuffer
    {
        private const int MaxLogs = 1000;
        private const int MaxMessageLength = 4000;
        private const int MaxStackTraceLength = 8000;
        private static readonly object Gate = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly HashSet<string> ErrorLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LogType.Error.ToString(),
            LogType.Exception.ToString(),
            LogType.Assert.ToString(),
        };

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
                bool filterAll = string.IsNullOrWhiteSpace(level)
                    || string.Equals(level, "all", StringComparison.OrdinalIgnoreCase);
                int take = Math.Min(limit, MaxLogs);

                if (filterAll)
                    return CloneTail(Entries, take);

                List<Entry> matched = new List<Entry>();
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (MatchesLevel(Entries[i].type, level))
                        matched.Add(Entries[i]);
                }

                return CloneTail(matched, take);
            }
        }

        public static void Clear()
        {
            lock (Gate)
                Entries.Clear();
        }

        private static List<Entry> CloneTail(List<Entry> source, int take)
        {
            int start = Math.Max(0, source.Count - take);
            List<Entry> result = new List<Entry>(source.Count - start);
            for (int i = start; i < source.Count; i++)
                result.Add(source[i].Clone());
            return result;
        }

        private static bool MatchesLevel(string actual, string requested)
        {
            if (string.Equals(requested, "error", StringComparison.OrdinalIgnoreCase))
                return ErrorLevels.Contains(actual);

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

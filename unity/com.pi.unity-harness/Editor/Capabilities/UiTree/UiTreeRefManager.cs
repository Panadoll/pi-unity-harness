using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

[assembly: InternalsVisibleTo("Harness.UiTree.Editor.Tests")]

namespace Pi.UnityHarness.Editor.Capabilities.UiTree
{
    /// <summary>
    /// Manages short ref handles (ref_{epoch}_{seq}) for both UGUI GameObjects
    /// and UI Toolkit VisualElements. Domain reload increments epoch, making
    /// old refs stale. Agent receives stale_ref guidance to re-snapshot.
    /// </summary>
    internal static class UiTreeRefManager
    {
        private const string SessionKeyEpoch = "Harness.UiTree.Epoch";

        // --- UI Toolkit refs ---
        private static readonly Dictionary<string, WeakReference<VisualElement>> s_veRefs = new();
        private static readonly ConditionalWeakTable<VisualElement, string> s_veToRef = new();

        // --- UGUI refs (keyed by ref string -> GameObject InstanceID) ---
        // long stores InstanceID (int) pre-6000.4, EntityId.ToULong() (ulong) on 6000.4+
        private static readonly Dictionary<string, long> s_goRefs = new();
        private static readonly Dictionary<long, string> s_goToRef = new();

        private static int s_epoch;
        private static int s_nextSeq = 1;

        [InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            s_epoch = SessionState.GetInt(SessionKeyEpoch, 0) + 1;
            SessionState.SetInt(SessionKeyEpoch, s_epoch);
            s_nextSeq = 1;
            // Old refs are automatically stale because epoch changed.
        }

        internal static int CurrentEpoch => s_epoch;

        // ─── VisualElement refs ───

        internal static string FindOrAssignRef(VisualElement ve)
        {
            if (s_veToRef.TryGetValue(ve, out var refId))
                return refId;

            refId = "ref_" + s_epoch + "_" + s_nextSeq++;
            s_veRefs[refId] = new WeakReference<VisualElement>(ve);
            s_veToRef.Add(ve, refId);
            return refId;
        }

        internal static bool TryResolveVisualElement(string refId, out VisualElement ve)
        {
            ve = null;
            if (!ValidateEpoch(refId)) return false;

            if (!s_veRefs.TryGetValue(refId, out var weakRef) || !weakRef.TryGetTarget(out ve))
                return false;

            if (ve.panel == null)
            {
                s_veRefs.Remove(refId);
                ve = null;
                return false;
            }

            return true;
        }

        // ─── UGUI (GameObject) refs ───

        internal static string FindOrAssignRef(GameObject go)
        {
#if UNITY_6000_4_OR_NEWER
            var instanceId = (long)EntityId.ToULong(go.GetEntityId());
#else
            long instanceId = go.GetInstanceID();
#endif
            if (s_goToRef.TryGetValue(instanceId, out var refId))
                return refId;

            refId = "ref_" + s_epoch + "_" + s_nextSeq++;
            s_goRefs[refId] = instanceId;
            s_goToRef[instanceId] = refId;
            return refId;
        }

        internal static bool TryResolveGameObject(string refId, out GameObject go)
        {
            go = null;
            if (!ValidateEpoch(refId)) return false;

            if (!s_goRefs.TryGetValue(refId, out long rawId))
                return false;

#if UNITY_6000_4_OR_NEWER
            go = EditorUtility.EntityIdToObject(EntityId.FromULong((ulong)rawId)) as GameObject;
#else
            go = EditorUtility.InstanceIDToObject((int)rawId) as GameObject;
#endif
            if (go == null)
            {
                s_goRefs.Remove(refId);
                s_goToRef.Remove(rawId);
            }
            return go != null;
        }

        // ─── Stale ref detection ───

        internal static bool IsStaleRef(string refId)
        {
            return !ValidateEpoch(refId);
        }

        internal static string StaleRefError(string refId)
        {
            return UiTreeJson.Error(
                "Stale ref: " + refId + " belongs to a previous epoch. Re-run ListRootsJson() or SnapshotJson() to get fresh refs.",
                "stale_ref");
        }

        // ─── Resolve by refId (auto-detect type) ───

        internal enum RefKind { Unknown, VisualElement, GameObject }

        internal static RefKind ClassifyRef(string refId)
        {
            if (string.IsNullOrEmpty(refId)) return RefKind.Unknown;
            if (!ValidateEpoch(refId)) return RefKind.Unknown;
            if (s_veRefs.ContainsKey(refId)) return RefKind.VisualElement;
            if (s_goRefs.ContainsKey(refId)) return RefKind.GameObject;
            return RefKind.Unknown;
        }

        // ─── Helpers ───

        private static bool ValidateEpoch(string refId)
        {
            if (string.IsNullOrEmpty(refId)) return false;
            // ref format: ref_{epoch}_{seq}
            var parts = refId.Split('_');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[1], out int refEpoch)) return false;
            return refEpoch == s_epoch;
        }

        internal static void PruneDeadRefs()
        {
            var deadVe = new List<string>();
            foreach (var kvp in s_veRefs)
            {
                if (!kvp.Value.TryGetTarget(out _))
                    deadVe.Add(kvp.Key);
            }
            foreach (var key in deadVe)
                s_veRefs.Remove(key);

            var deadGo = new List<string>();
            foreach (var kvp in s_goRefs)
            {
#if UNITY_6000_4_OR_NEWER
                if (EditorUtility.EntityIdToObject(EntityId.FromULong((ulong)kvp.Value)) == null)
#else
                if (EditorUtility.InstanceIDToObject((int)kvp.Value) == null)
#endif
                    deadGo.Add(kvp.Key);
            }
            foreach (var key in deadGo)
            {
                if (s_goRefs.TryGetValue(key, out long id))
                {
                    s_goToRef.Remove(id);
                    s_goRefs.Remove(key);
                }
            }
        }

        /// <summary>
        /// Increment epoch manually (for testing).
        /// </summary>
        internal static void ForceNewEpoch()
        {
            s_epoch++;
            SessionState.SetInt(SessionKeyEpoch, s_epoch);
            s_nextSeq = 1;
        }
    }
}

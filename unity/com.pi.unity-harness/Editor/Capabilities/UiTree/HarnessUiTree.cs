using System.Collections.Generic;
using System.Text;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Pi.UnityHarness.Editor;

namespace Pi.UnityHarness.Editor.Capabilities.UiTree
{
    /// <summary>
    /// Public UI Tree API facade. All methods return JSON strings for uh eval -f recipes.
    /// Reads and locates UGUI and UI Toolkit elements; does NOT perform any actions.
    /// Actions (click, scroll, type) are handled by com.harness.input.
    ///
    /// Usage in uh eval -f:
    ///   Harness.UiTree.Editor.HarnessUiTree.ListRootsJson()
    ///   Harness.UiTree.Editor.HarnessUiTree.SnapshotJson()
    ///   Harness.UiTree.Editor.HarnessUiTree.FindJson("Button")
    ///   Harness.UiTree.Editor.HarnessUiTree.DescribeJson("ref_1_5")
    ///   Harness.UiTree.Editor.HarnessUiTree.TextJson("ref_1_5")
    /// </summary>
    public static class HarnessUiTree
    {
        // ─── ListRootsJson ───

        /// <summary>
        /// List all snapshotable UI roots: UGUI Canvases, UI Toolkit editor panels,
        /// and runtime UIDocuments. Returns only root summaries, not the tree.
        ///
        /// Usage in uh eval -f:
        ///   Harness.UiTree.Editor.HarnessUiTree.ListRootsJson()
        /// </summary>
        public static string ListRootsJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"status\":\"succeeded\",\"roots\":[");

            int index = 0;

            // UGUI roots
            var uguiRoots = UguiBackend.GetRoots();
            foreach (var r in uguiRoots)
            {
                if (index > 0) sb.Append(",");
                sb.Append("{\"root\":\"").Append(PiUnityJsonHelper.EscapeJson(r.Name));
                sb.Append("\",\"source\":\"ugui\"");
                sb.Append(",\"name\":\"").Append(PiUnityJsonHelper.EscapeJson(r.Name));
                sb.Append("\",\"context_type\":\"Scene\"");
                sb.Append(",\"element_count\":").Append(r.ElementCount);
                sb.Append(",\"active\":").Append(UiTreeJson.BoolStr(r.Active));
                sb.Append("}");
                index++;
            }

            // UI Toolkit roots
            var toolkitRoots = UIToolkitBackend.GetRoots();
            foreach (var r in toolkitRoots)
            {
                if (index > 0) sb.Append(",");
                sb.Append("{\"root\":\"").Append(PiUnityJsonHelper.EscapeJson(r.Name));
                sb.Append("\",\"source\":\"uitoolkit\"");
                sb.Append(",\"name\":\"").Append(PiUnityJsonHelper.EscapeJson(r.Name));
                sb.Append("\",\"context_type\":\"").Append(PiUnityJsonHelper.EscapeJson(r.ContextType));
                sb.Append("\",\"element_count\":").Append(r.ElementCount);
                sb.Append(",\"active\":").Append(UiTreeJson.BoolStr(r.Active));
                sb.Append("}");
                index++;
            }

            sb.Append("]}");
            return sb.ToString();
        }

        // ─── SnapshotJson ───

        /// <summary>
        /// Return a trimmed snapshot of the UI tree. Defaults to interactive-only nodes.
        ///
        /// Usage in uh eval -f:
        ///   Harness.UiTree.Editor.HarnessUiTree.SnapshotJson()
        ///   Harness.UiTree.Editor.HarnessUiTree.SnapshotJson(true, 6, 100, null, null, false)
        /// </summary>
        public static string SnapshotJson(
            bool interactiveOnly = true,
            int maxDepth = 6,
            int limit = 100,
            string root = null,
            string selector = null,
            bool includeInvisible = false)
        {
            if (limit <= 0) limit = 100;
            if (maxDepth < 0) maxDepth = -1; // -1 means unlimited

            UiTreeRefManager.PruneDeadRefs();

            var sb = new StringBuilder();
            sb.Append("{\"status\":\"succeeded\",\"nodes\":[");

            int totalCount = 0;
            int matchedCount = 0;
            int nodeIndex = 0;
            bool reachedLimit = false;
            var uguiRoots = UguiBackend.GetRoots();
            bool rootIsUgui = root != null && IsUguiRoot(root, uguiRoots);

            // UGUI
            if (root == null || rootIsUgui)
            {
                foreach (var r in uguiRoots)
                {
                    if (root != null && !r.Name.Equals(root, System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    int rootMatchedCount = UguiBackend.CountMatchingNodes(r.Canvas, interactiveOnly,
                        maxDepth, selector, includeInvisible);
                    totalCount += r.ElementCount;
                    matchedCount += rootMatchedCount;

                    int remaining = limit - nodeIndex;
                    if (remaining <= 0)
                    {
                        reachedLimit |= rootMatchedCount > 0;
                        continue;
                    }

                    var nodes = UguiBackend.CollectNodes(r.Canvas, interactiveOnly,
                        maxDepth, remaining, selector, includeInvisible);
                    if (nodes.Count < rootMatchedCount)
                        reachedLimit = true;

                    foreach (var n in nodes)
                    {
                        if (nodeIndex > 0) sb.Append(",");
                        UguiBackend.AppendNodeJson(sb, n);
                        nodeIndex++;
                    }
                }
            }

            // UI Toolkit
            if (root == null || !rootIsUgui)
            {
                var toolkitRoots = UIToolkitBackend.GetRoots();
                foreach (var r in toolkitRoots)
                {
                    if (root != null && !r.Name.Equals(root, System.StringComparison.OrdinalIgnoreCase) &&
                        r.Name.IndexOf(root, System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    int rootMatchedCount = UIToolkitBackend.CountMatchingNodes(r.Root, interactiveOnly,
                        maxDepth, selector, includeInvisible);
                    totalCount += r.ElementCount;
                    matchedCount += rootMatchedCount;

                    int remaining = limit - nodeIndex;
                    if (remaining <= 0)
                    {
                        reachedLimit |= rootMatchedCount > 0;
                        continue;
                    }

                    var nodes = UIToolkitBackend.CollectNodes(r.Root, interactiveOnly,
                        maxDepth, remaining, selector, includeInvisible, r.ContextType);
                    if (nodes.Count < rootMatchedCount)
                        reachedLimit = true;

                    foreach (var n in nodes)
                    {
                        if (nodeIndex > 0) sb.Append(",");
                        UIToolkitBackend.AppendNodeJson(sb, n);
                        nodeIndex++;
                    }
                }
            }

            sb.Append("]");
            UiTreeJson.AppendTruncationMeta(sb, totalCount, matchedCount, nodeIndex, maxDepth, limit);

            if (reachedLimit)
                sb.Append(",\"warning\":\"Result truncated at limit. Use selector or root to narrow scope.\"");

            sb.Append("}");
            return sb.ToString();
        }

        // ─── FindJson ───

        /// <summary>
        /// Search for UI elements by name, text, type, USS class, or path.
        /// Supports #name and .class prefix for exact matching.
        ///
        /// Usage in uh eval -f:
        ///   Harness.UiTree.Editor.HarnessUiTree.FindJson("Button")
        ///   Harness.UiTree.Editor.HarnessUiTree.FindJson("#submit-btn", 50, null, false)
        /// </summary>
        public static string FindJson(string query, int limit = 50,
            string root = null, bool includeInvisible = false)
        {
            if (string.IsNullOrEmpty(query))
                return UiTreeJson.Error("query is required.", "usage");

            if (limit <= 0) limit = 50;

            UiTreeRefManager.PruneDeadRefs();

            var sb = new StringBuilder();
            sb.Append("{\"status\":\"succeeded\",\"query\":\"").Append(PiUnityJsonHelper.EscapeJson(query));
            sb.Append("\",\"matches\":[");

            int nodeIndex = 0;
            int totalCount = 0;
            int matchedCount = 0;
            bool reachedLimit = false;
            var uguiRoots = UguiBackend.GetRoots();
            bool rootIsUgui = root != null && IsUguiRoot(root, uguiRoots);

            // UGUI
            if (root == null || rootIsUgui)
            {
                foreach (var r in uguiRoots)
                {
                    if (root != null && !r.Name.Equals(root, System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    int rootMatchedCount = UguiBackend.CountSearchMatches(r.Canvas, query, includeInvisible);
                    totalCount += r.ElementCount;
                    matchedCount += rootMatchedCount;

                    int remaining = limit - nodeIndex;
                    if (remaining <= 0)
                    {
                        reachedLimit |= rootMatchedCount > 0;
                        continue;
                    }

                    var nodes = UguiBackend.Search(r.Canvas, query, remaining, includeInvisible);
                    if (nodes.Count < rootMatchedCount)
                        reachedLimit = true;

                    foreach (var n in nodes)
                    {
                        if (nodeIndex > 0) sb.Append(",");
                        UguiBackend.AppendNodeJson(sb, n, false);
                        nodeIndex++;
                    }
                }
            }

            // UI Toolkit
            if (root == null || !rootIsUgui)
            {
                var toolkitRoots = UIToolkitBackend.GetRoots();
                foreach (var r in toolkitRoots)
                {
                    if (root != null && !r.Name.Equals(root, System.StringComparison.OrdinalIgnoreCase) &&
                        r.Name.IndexOf(root, System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    int rootMatchedCount = UIToolkitBackend.CountSearchMatches(r.Root, query, includeInvisible);
                    totalCount += r.ElementCount;
                    matchedCount += rootMatchedCount;

                    int remaining = limit - nodeIndex;
                    if (remaining <= 0)
                    {
                        reachedLimit |= rootMatchedCount > 0;
                        continue;
                    }

                    var nodes = UIToolkitBackend.Search(r.Root, query, remaining, includeInvisible, r.ContextType);
                    if (nodes.Count < rootMatchedCount)
                        reachedLimit = true;

                    foreach (var n in nodes)
                    {
                        if (nodeIndex > 0) sb.Append(",");
                        UIToolkitBackend.AppendNodeJson(sb, n, false);
                        nodeIndex++;
                    }
                }
            }

            sb.Append("]");
            sb.Append(",\"total_count\":").Append(totalCount);
            sb.Append(",\"matched_count\":").Append(matchedCount);
            sb.Append(",\"returned_count\":").Append(nodeIndex);
            sb.Append(",\"truncated\":").Append(UiTreeJson.BoolStr(nodeIndex < matchedCount));
            sb.Append(",\"omitted_count\":").Append(System.Math.Max(0, matchedCount - nodeIndex));
            sb.Append(",\"limit\":").Append(limit);
            if (reachedLimit)
                sb.Append(",\"warning\":\"Result truncated at limit. Narrow the query to see more.\"");
            sb.Append("}");
            return sb.ToString();
        }

        // ─── DescribeJson ───

        /// <summary>
        /// Return detailed info about a single node by ref.
        ///
        /// Usage in uh eval -f:
        ///   Harness.UiTree.Editor.HarnessUiTree.DescribeJson("ref_1_5")
        ///   Harness.UiTree.Editor.HarnessUiTree.DescribeJson("ref_1_5", 2, 80)
        /// </summary>
        public static string DescribeJson(string nodeRef, int maxDepth = 2, int limit = 80)
        {
            if (string.IsNullOrEmpty(nodeRef))
                return UiTreeJson.Error("nodeRef is required.", "usage");

            if (UiTreeRefManager.IsStaleRef(nodeRef))
                return UiTreeRefManager.StaleRefError(nodeRef);

            var kind = UiTreeRefManager.ClassifyRef(nodeRef);

            switch (kind)
            {
                case UiTreeRefManager.RefKind.VisualElement:
                    if (UiTreeRefManager.TryResolveVisualElement(nodeRef, out var ve))
                        return UIToolkitBackend.DescribeNode(ve, nodeRef, maxDepth, limit);
                    return UiTreeJson.Error("VisualElement ref not found: " + nodeRef, "not_found");

                case UiTreeRefManager.RefKind.GameObject:
                    if (UiTreeRefManager.TryResolveGameObject(nodeRef, out var go))
                        return DescribeUguiNode(go, nodeRef);
                    return UiTreeJson.Error("GameObject ref not found: " + nodeRef, "not_found");

                default:
                    return UiTreeJson.Error("Unknown ref: " + nodeRef +
                        ". Re-run ListRootsJson() or SnapshotJson() to get fresh refs.", "not_found");
            }
        }

        // ─── TextJson ───

        /// <summary>
        /// Read only the text content of a node. Low-context verification API.
        ///
        /// Usage in uh eval -f:
        ///   Harness.UiTree.Editor.HarnessUiTree.TextJson("ref_1_5")
        /// </summary>
        public static string TextJson(string nodeRef)
        {
            if (string.IsNullOrEmpty(nodeRef))
                return UiTreeJson.Error("nodeRef is required.", "usage");

            if (UiTreeRefManager.IsStaleRef(nodeRef))
                return UiTreeRefManager.StaleRefError(nodeRef);

            var kind = UiTreeRefManager.ClassifyRef(nodeRef);

            switch (kind)
            {
                case UiTreeRefManager.RefKind.VisualElement:
                    if (UiTreeRefManager.TryResolveVisualElement(nodeRef, out var ve))
                        return TextFromVisualElement(ve, nodeRef);
                    return UiTreeJson.Error("VisualElement ref not found: " + nodeRef, "not_found");

                case UiTreeRefManager.RefKind.GameObject:
                    if (UiTreeRefManager.TryResolveGameObject(nodeRef, out var go))
                        return TextFromGameObject(go, nodeRef);
                    return UiTreeJson.Error("GameObject ref not found: " + nodeRef, "not_found");

                default:
                    return UiTreeJson.Error("Unknown ref: " + nodeRef, "not_found");
            }
        }

        // ─── Private helpers ───

        private static bool IsUguiRoot(string rootName, List<UguiBackend.UguiRoot> uguiRoots)
        {
            if (string.IsNullOrEmpty(rootName)) return false;
            foreach (var r in uguiRoots)
                if (r.Name.Equals(rootName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string DescribeUguiNode(GameObject go, string refId)
        {
            var sb = new StringBuilder();
            sb.Append("{\"status\":\"succeeded\"");
            sb.Append(",\"ref\":\"").Append(PiUnityJsonHelper.EscapeJson(refId)).Append("\"");
            sb.Append(",\"source\":\"ugui\"");
            sb.Append(",\"type\":\"").Append(PiUnityJsonHelper.EscapeJson(go.GetType().Name)).Append("\"");
            sb.Append(",\"name\":\"").Append(PiUnityJsonHelper.EscapeJson(go.name)).Append("\"");
            sb.Append(",\"active\":").Append(UiTreeJson.BoolStr(go.activeInHierarchy));

            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                sb.Append(",\"local_position\":{\"x\":").Append(UiTreeJson.FloatStr(rt.localPosition.x));
                sb.Append(",\"y\":").Append(UiTreeJson.FloatStr(rt.localPosition.y)).Append("}");
                sb.Append(",\"size_delta\":{\"x\":").Append(UiTreeJson.FloatStr(rt.sizeDelta.x));
                sb.Append(",\"y\":").Append(UiTreeJson.FloatStr(rt.sizeDelta.y)).Append("}");
                AppendUguiInteractionDiagnostics(sb, go, rt);
            }

            sb.Append(",\"children_count\":").Append(go.transform.childCount);

            // List children
            if (go.transform.childCount > 0)
            {
                sb.Append(",\"children\":[");
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (i > 0) sb.Append(",");
                    var child = go.transform.GetChild(i).gameObject;
                    var childRef = UiTreeRefManager.FindOrAssignRef(child);
                    sb.Append("{\"ref\":\"").Append(PiUnityJsonHelper.EscapeJson(childRef));
                    sb.Append("\",\"name\":\"").Append(PiUnityJsonHelper.EscapeJson(child.name));
                    sb.Append("\",\"active\":").Append(UiTreeJson.BoolStr(child.activeInHierarchy));
                    sb.Append("}");
                }
                sb.Append("]");
            }

            // Components summary
            var components = go.GetComponents<Component>();
            sb.Append(",\"components\":[");
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == null) continue;
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(PiUnityJsonHelper.EscapeJson(components[i].GetType().Name)).Append("\"");
            }
            sb.Append("]");

            sb.Append("}");
            return sb.ToString();
        }

        private static void AppendUguiInteractionDiagnostics(StringBuilder sb, GameObject go, RectTransform rt)
        {
            bool groupInteractable = UguiBackend.IsCanvasGroupInteractable(go);
            bool groupBlocksRaycasts = UguiBackend.DoesCanvasGroupBlockRaycasts(go);
            bool likelyClickable = go.activeInHierarchy && groupInteractable && groupBlocksRaycasts;
            string blockedReason = null;

            if (!go.activeInHierarchy)
                blockedReason = "inactive";
            else if (!groupInteractable)
                blockedReason = "canvas_group_not_interactable";
            else if (!groupBlocksRaycasts)
                blockedReason = "canvas_group_blocks_raycasts_false";

            var selectable = go.GetComponent<Selectable>();
            if (blockedReason == null && selectable != null && !selectable.interactable)
                blockedReason = "selectable_not_interactable";

            var graphic = go.GetComponent<Graphic>();
            Vector2 center = RectTransformToTopLeftCenter(rt);
            string topHitName = null;
            bool topHitMatchesTarget = false;
            int hitCount = GetEventSystemHit(center, go, out topHitName, out topHitMatchesTarget);
            bool eventSystemPresent = hitCount >= 0;

            if (blockedReason == null && !eventSystemPresent)
                blockedReason = "event_system_missing";
            if (hitCount >= 0)
            {
                if (blockedReason == null && hitCount > 0 && !topHitMatchesTarget)
                    blockedReason = "blocked_by_" + topHitName;
                else if (blockedReason == null && hitCount == 0)
                    blockedReason = "no_eventsystem_hit";
            }

            sb.Append(",\"interaction_diagnostics\":{");
            sb.Append("\"likely_clickable\":").Append(UiTreeJson.BoolStr(likelyClickable && blockedReason == null));
            sb.Append(",\"blocked_reason\":");
            if (blockedReason != null)
                sb.Append("\"").Append(PiUnityJsonHelper.EscapeJson(blockedReason)).Append("\"");
            else
                sb.Append("null");
            sb.Append(",\"event_system_present\":").Append(UiTreeJson.BoolStr(eventSystemPresent));
            sb.Append(",\"raycast_target\":").Append(UiTreeJson.BoolStr(graphic != null && graphic.raycastTarget));
            sb.Append(",\"canvas_group_interactable\":").Append(UiTreeJson.BoolStr(groupInteractable));
            sb.Append(",\"canvas_group_blocks_raycasts\":").Append(UiTreeJson.BoolStr(groupBlocksRaycasts));
            sb.Append(",\"probe_point\":{\"x\":").Append(UiTreeJson.FloatStr(center.x));
            sb.Append(",\"y\":").Append(UiTreeJson.FloatStr(center.y)).Append("}");
            if (hitCount >= 0)
            {
                sb.Append(",\"eventsystem_hit_count\":").Append(hitCount);
                if (topHitName != null)
                    sb.Append(",\"eventsystem_top_hit\":\"").Append(PiUnityJsonHelper.EscapeJson(topHitName)).Append("\"");
                sb.Append(",\"eventsystem_top_hit_matches_target\":").Append(UiTreeJson.BoolStr(topHitMatchesTarget));
            }
            sb.Append("}");
        }

        private static int GetEventSystemHit(Vector2 topLeftPoint, GameObject target,
            out string topHitName, out bool topHitMatchesTarget)
        {
            topHitName = null;
            topHitMatchesTarget = false;
            if (EventSystem.current == null)
                return -1;

            var eventData = new PointerEventData(EventSystem.current)
            {
                position = new Vector2(topLeftPoint.x, UguiBackend.GetGameViewHeight() - topLeftPoint.y)
            };
            RaycastResult hit = PiUiRaycastHelper.RaycastUI(eventData.position, EventSystem.current);
            if (hit.gameObject != null)
            {
                var hitObject = hit.gameObject;
                topHitName = hitObject.name;
                topHitMatchesTarget = hitObject == target ||
                    hitObject.transform.IsChildOf(target.transform) ||
                    target.transform.IsChildOf(hitObject.transform);
                return 1;
            }
            return 0;
        }

        private static Vector2 RectTransformToTopLeftCenter(RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            var canvas = rt.GetComponentInParent<Canvas>();
            var camera = canvas != null ? canvas.worldCamera : null;
            var screenMin = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var screenMax = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            float x = screenMin.x + (screenMax.x - screenMin.x) / 2f;
            float y = UguiBackend.GetGameViewHeight() - screenMax.y + (screenMax.y - screenMin.y) / 2f;
            return new Vector2(x, y);
        }

        private static string TextFromVisualElement(VisualElement ve, string refId)
        {
            string text = null;

            if (ve is TextElement textElement)
                text = textElement.text;
            else if (ve is BaseField<string> stringField)
                text = stringField.value;
            else
            {
                var childText = ve.Q<TextElement>();
                if (childText != null)
                    text = childText.text;
            }

            if (text == null)
                return UiTreeJson.Error("Element has no text content: " + refId +
                    " (" + ve.GetType().Name + ")", "no_text");

            return "{\"status\":\"succeeded\",\"ref\":\"" + PiUnityJsonHelper.EscapeJson(refId) +
                   "\",\"type\":\"" + PiUnityJsonHelper.EscapeJson(ve.GetType().Name) +
                   "\",\"text\":\"" + PiUnityJsonHelper.EscapeJson(text) + "\"}";
        }

        private static string TextFromGameObject(GameObject go, string refId)
        {
            // Try Text component
            var textComp = go.GetComponent<Text>();
            if (textComp != null)
            {
                return "{\"status\":\"succeeded\",\"ref\":\"" + PiUnityJsonHelper.EscapeJson(refId) +
                       "\",\"type\":\"Text\",\"text\":\"" + PiUnityJsonHelper.EscapeJson(textComp.text) + "\"}";
            }

            // Try InputField
            var inputField = go.GetComponent<InputField>();
            if (inputField != null)
            {
                return "{\"status\":\"succeeded\",\"ref\":\"" + PiUnityJsonHelper.EscapeJson(refId) +
                       "\",\"type\":\"InputField\",\"text\":\"" + PiUnityJsonHelper.EscapeJson(inputField.text) + "\"}";
            }

            // Try TMP via reflection
            var tmpTextType = System.Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (tmpTextType != null)
            {
                var tmpComp = go.GetComponent(tmpTextType);
                if (tmpComp != null)
                {
                    var textProp = tmpTextType.GetProperty("text",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (textProp != null)
                    {
                        string text = textProp.GetValue(tmpComp) as string;
                        if (text != null)
                        {
                            return "{\"status\":\"succeeded\",\"ref\":\"" + PiUnityJsonHelper.EscapeJson(refId) +
                                   "\",\"type\":\"TMP_Text\",\"text\":\"" + PiUnityJsonHelper.EscapeJson(text) + "\"}";
                        }
                    }
                }
            }

            return UiTreeJson.Error("Element has no text content: " + refId + " (" + go.name + ")", "no_text");
        }
    }
}

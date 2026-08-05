using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Pi.UnityHarness.Editor.Capabilities.Shared;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Pi.UnityHarness.Editor.Capabilities.UiTree
{
    /// <summary>
    /// UGUI traversal backend. Walks active Canvas hierarchies, extracts
    /// interactive element summaries, and converts coordinates to top-left
    /// origin (matching com.harness.vision screenshot coordinates).
    /// TMP types are detected via reflection to avoid hard asmdef dependency.
    /// </summary>
    internal static class UguiBackend
    {
        // Cached TMP types (reflection, lazy init)
        private static Type s_tmpTextType;
        private static Type s_tmpInputType;
        private static bool s_tmpProbed;

        // ─── Root discovery ───

        internal struct UguiRoot
        {
            public Canvas Canvas;
            public string Name;
            public int ElementCount;
            public bool Active;
        }

        internal static List<UguiRoot> GetRoots()
        {
            var results = new List<UguiRoot>();
#pragma warning disable CS0618 // FindObjectsOfType is deprecated but needed for 2021.3 compat
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
#pragma warning restore CS0618

            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;
                // Only root canvases (not nested)
                if (canvas.transform.parent != null &&
                    canvas.transform.parent.GetComponentInParent<Canvas>() != null)
                    continue;

                int count = CountElements(canvas.gameObject);
                results.Add(new UguiRoot
                {
                    Canvas = canvas,
                    Name = canvas.gameObject.name,
                    ElementCount = count,
                    Active = canvas.gameObject.activeInHierarchy
                });
            }

            return results;
        }

        // ─── Node collection ───

        internal struct UguiNode
        {
            public string Ref;
            public string Type;
            public string Name;
            public string Text;
            public string Path;
            public bool Enabled;
            public bool Visible;
            public bool Interactive;
            public string Interaction;
            public float RectX, RectY, RectW, RectH;
            public float CenterX, CenterY;
            public int ChildrenCount;
            public int SortingOrder;
            public int SiblingIndex;
            public bool RaycastTarget;
            public bool CanvasGroupInteractable;
            public bool CanvasGroupBlocksRaycasts;
            public bool HasValueRange;
            public float CurrentValue;
            public float MinValue;
            public float MaxValue;
            public bool WholeNumbers;
            public string[] Handlers;
        }

        internal static List<UguiNode> CollectNodes(Canvas canvas,
            bool interactiveOnly, int maxDepth, int limit,
            string selector, bool includeInvisible)
        {
            var nodes = new List<UguiNode>();
            float gameViewHeight = GetGameViewHeight();
            int sortingOrder = canvas.sortingOrder;

            CollectRecursive(canvas.gameObject, canvas, nodes, interactiveOnly,
                maxDepth, 0, limit, selector, includeInvisible, "", gameViewHeight, sortingOrder);

            return nodes;
        }

        private static void CollectRecursive(GameObject go, Canvas rootCanvas,
            List<UguiNode> nodes, bool interactiveOnly, int maxDepth, int depth,
            int limit, string selector, bool includeInvisible,
            string parentPath, float gameViewHeight, int sortingOrder)
        {
            if (nodes.Count >= limit) return;
            if (maxDepth >= 0 && depth > maxDepth) return;

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;

            string typeName = DetermineUguiType(go);
            string currentPath = string.IsNullOrEmpty(parentPath)
                ? go.name
                : parentPath + " > " + go.name;

            bool canvasGroupInteractable = IsCanvasGroupInteractable(go);
            bool canvasGroupBlocksRaycasts = DoesCanvasGroupBlockRaycasts(go);
            bool isVisible = go.activeInHierarchy && IsVisibleOnScreen(rt);
            if (!includeInvisible && !isVisible)
            {
                // Still recurse children
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    CollectRecursive(go.transform.GetChild(i).gameObject, rootCanvas,
                        nodes, interactiveOnly, maxDepth, depth + 1, limit,
                        selector, includeInvisible, currentPath, gameViewHeight, sortingOrder);
                }
                return;
            }

            bool isInteractive = IsInteractive(go);
            string interaction = DetermineInteraction(go);
            string text = ExtractText(go);
            TryGetValueRange(go, out bool hasValueRange, out float currentValue,
                out float minValue, out float maxValue, out bool wholeNumbers);

            // Selector filter
            if (!string.IsNullOrEmpty(selector) && !MatchesSelector(go, typeName, text, selector))
            {
                // Still recurse children
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    CollectRecursive(go.transform.GetChild(i).gameObject, rootCanvas,
                        nodes, interactiveOnly, maxDepth, depth + 1, limit,
                        selector, includeInvisible, currentPath, gameViewHeight, sortingOrder);
                }
                return;
            }

            if (interactiveOnly && !isInteractive)
            {
                // Still recurse children
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    CollectRecursive(go.transform.GetChild(i).gameObject, rootCanvas,
                        nodes, interactiveOnly, maxDepth, depth + 1, limit,
                        selector, includeInvisible, currentPath, gameViewHeight, sortingOrder);
                }
                return;
            }

            // Convert world rect to top-left origin coordinates
            var worldCorners = new Vector3[4];
            rt.GetWorldCorners(worldCorners);

            var screenMin = RectTransformUtility.WorldToScreenPoint(rootCanvas.worldCamera, worldCorners[0]);
            var screenMax = RectTransformUtility.WorldToScreenPoint(rootCanvas.worldCamera, worldCorners[2]);

            // Unity screen coords are bottom-left origin; convert to top-left
            float rectX = screenMin.x;
            float rectY = gameViewHeight - screenMax.y;
            float rectW = screenMax.x - screenMin.x;
            float rectH = screenMax.y - screenMin.y;
            float centerX = rectX + rectW / 2f;
            float centerY = rectY + rectH / 2f;

            var graphic = go.GetComponent<Graphic>();
            string[] handlers = GetEventHandlerNames(go);

            nodes.Add(new UguiNode
            {
                Ref = UiTreeRefManager.FindOrAssignRef(go),
                Type = typeName,
                Name = go.name,
                Text = text,
                Path = currentPath,
                Enabled = go.activeInHierarchy,
                Visible = isVisible,
                Interactive = isInteractive,
                Interaction = interaction,
                RectX = rectX, RectY = rectY, RectW = rectW, RectH = rectH,
                CenterX = centerX, CenterY = centerY,
                ChildrenCount = go.transform.childCount,
                SortingOrder = sortingOrder,
                SiblingIndex = go.transform.GetSiblingIndex(),
                RaycastTarget = graphic != null && graphic.raycastTarget,
                CanvasGroupInteractable = canvasGroupInteractable,
                CanvasGroupBlocksRaycasts = canvasGroupBlocksRaycasts,
                HasValueRange = hasValueRange,
                CurrentValue = currentValue,
                MinValue = minValue,
                MaxValue = maxValue,
                WholeNumbers = wholeNumbers,
                Handlers = handlers
            });

            // Recurse children
            for (int i = 0; i < go.transform.childCount; i++)
            {
                CollectRecursive(go.transform.GetChild(i).gameObject, rootCanvas,
                    nodes, interactiveOnly, maxDepth, depth + 1, limit,
                    selector, includeInvisible, currentPath, gameViewHeight, sortingOrder);
            }
        }

        internal static int CountMatchingNodes(Canvas canvas,
            bool interactiveOnly, int maxDepth, string selector, bool includeInvisible)
        {
            int count = 0;
            CountMatchingRecursive(canvas.gameObject, interactiveOnly, maxDepth, 0,
                selector, includeInvisible, ref count);
            return count;
        }

        private static void CountMatchingRecursive(GameObject go,
            bool interactiveOnly, int maxDepth, int depth,
            string selector, bool includeInvisible, ref int count)
        {
            if (maxDepth >= 0 && depth > maxDepth) return;

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;

            bool isVisible = go.activeInHierarchy && IsVisibleOnScreen(rt);
            string typeName = DetermineUguiType(go);
            string text = ExtractText(go);
            bool selectorMatches = string.IsNullOrEmpty(selector) ||
                MatchesSelector(go, typeName, text, selector);
            bool interactiveMatches = !interactiveOnly || IsInteractive(go);

            if ((includeInvisible || isVisible) && selectorMatches && interactiveMatches)
                count++;

            for (int i = 0; i < go.transform.childCount; i++)
                CountMatchingRecursive(go.transform.GetChild(i).gameObject,
                    interactiveOnly, maxDepth, depth + 1, selector, includeInvisible, ref count);
        }

        // ─── Search ───

        internal static List<UguiNode> Search(Canvas canvas, string query,
            int limit, bool includeInvisible)
        {
            var nodes = new List<UguiNode>();
            float gameViewHeight = GetGameViewHeight();
            int sortingOrder = canvas.sortingOrder;
            SearchRecursive(canvas.gameObject, canvas, nodes, query, limit,
                includeInvisible, "", gameViewHeight, sortingOrder);
            return nodes;
        }

        private static void SearchRecursive(GameObject go, Canvas rootCanvas,
            List<UguiNode> nodes, string query, int limit,
            bool includeInvisible, string parentPath, float gameViewHeight, int sortingOrder)
        {
            if (nodes.Count >= limit) return;

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;

            string currentPath = string.IsNullOrEmpty(parentPath)
                ? go.name
                : parentPath + " > " + go.name;

            bool canvasGroupInteractable = IsCanvasGroupInteractable(go);
            bool canvasGroupBlocksRaycasts = DoesCanvasGroupBlockRaycasts(go);
            bool isVisible = go.activeInHierarchy && IsVisibleOnScreen(rt);
            if (!includeInvisible && !isVisible)
            {
                for (int i = 0; i < go.transform.childCount; i++)
                    SearchRecursive(go.transform.GetChild(i).gameObject, rootCanvas,
                        nodes, query, limit, includeInvisible, currentPath, gameViewHeight, sortingOrder);
                return;
            }

            string typeName = DetermineUguiType(go);
            string text = ExtractText(go);

            if (MatchesQuery(go, typeName, text, currentPath, query))
            {
                bool isInteractive = IsInteractive(go);
                string interaction = DetermineInteraction(go);
                TryGetValueRange(go, out bool hasValueRange, out float currentValue,
                    out float minValue, out float maxValue, out bool wholeNumbers);

                var worldCorners = new Vector3[4];
                rt.GetWorldCorners(worldCorners);
                var screenMin = RectTransformUtility.WorldToScreenPoint(rootCanvas.worldCamera, worldCorners[0]);
                var screenMax = RectTransformUtility.WorldToScreenPoint(rootCanvas.worldCamera, worldCorners[2]);

                float rectX = screenMin.x;
                float rectY = gameViewHeight - screenMax.y;
                float rectW = screenMax.x - screenMin.x;
                float rectH = screenMax.y - screenMin.y;

                var graphic = go.GetComponent<Graphic>();
                string[] handlers = GetEventHandlerNames(go);

                nodes.Add(new UguiNode
                {
                    Ref = UiTreeRefManager.FindOrAssignRef(go),
                    Type = typeName,
                    Name = go.name,
                    Text = text,
                    Path = currentPath,
                    Enabled = go.activeInHierarchy,
                    Visible = isVisible,
                    Interactive = isInteractive,
                    Interaction = interaction,
                    RectX = rectX, RectY = rectY, RectW = rectW, RectH = rectH,
                    CenterX = rectX + rectW / 2f, CenterY = rectY + rectH / 2f,
                    ChildrenCount = go.transform.childCount,
                    SortingOrder = sortingOrder,
                    SiblingIndex = go.transform.GetSiblingIndex(),
                    RaycastTarget = graphic != null && graphic.raycastTarget,
                    CanvasGroupInteractable = canvasGroupInteractable,
                    CanvasGroupBlocksRaycasts = canvasGroupBlocksRaycasts,
                    HasValueRange = hasValueRange,
                    CurrentValue = currentValue,
                    MinValue = minValue,
                    MaxValue = maxValue,
                    WholeNumbers = wholeNumbers,
                    Handlers = handlers
                });
            }

            for (int i = 0; i < go.transform.childCount; i++)
                SearchRecursive(go.transform.GetChild(i).gameObject, rootCanvas,
                    nodes, query, limit, includeInvisible, currentPath, gameViewHeight, sortingOrder);
        }

        internal static int CountSearchMatches(Canvas canvas, string query, bool includeInvisible)
        {
            int count = 0;
            CountSearchMatchesRecursive(canvas.gameObject, query, includeInvisible, "", ref count);
            return count;
        }

        private static void CountSearchMatchesRecursive(GameObject go, string query,
            bool includeInvisible, string parentPath, ref int count)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;

            string currentPath = string.IsNullOrEmpty(parentPath)
                ? go.name
                : parentPath + " > " + go.name;

            bool isVisible = go.activeInHierarchy && IsVisibleOnScreen(rt);
            string typeName = DetermineUguiType(go);
            string text = ExtractText(go);

            if ((includeInvisible || isVisible) && MatchesQuery(go, typeName, text, currentPath, query))
                count++;

            for (int i = 0; i < go.transform.childCount; i++)
                CountSearchMatchesRecursive(go.transform.GetChild(i).gameObject,
                    query, includeInvisible, currentPath, ref count);
        }

        // ─── Node serialization ───

        internal static void AppendNodeJson(StringBuilder sb, UguiNode node, bool includeExtra = true)
        {
            sb.Append("{\"ref\":\"").Append(UiTreeJson.Escape(node.Ref));
            sb.Append("\",\"source\":\"ugui\"");
            sb.Append(",\"type\":\"").Append(UiTreeJson.Escape(node.Type));
            sb.Append("\",\"name\":\"").Append(UiTreeJson.Escape(node.Name));
            sb.Append("\",\"text\":");
            if (node.Text != null)
                sb.Append("\"").Append(UiTreeJson.Escape(node.Text)).Append("\"");
            else
                sb.Append("null");
            sb.Append(",\"path\":\"").Append(UiTreeJson.Escape(node.Path));
            sb.Append("\",\"enabled\":").Append(UiTreeJson.BoolStr(node.Enabled));
            sb.Append(",\"visible\":").Append(UiTreeJson.BoolStr(node.Visible));
            sb.Append(",\"interactive\":").Append(UiTreeJson.BoolStr(node.Interactive));
            sb.Append(",\"interaction\":");
            if (node.Interaction != null)
                sb.Append("\"").Append(UiTreeJson.Escape(node.Interaction)).Append("\"");
            else
                sb.Append("null");
            sb.Append(",\"rect\":{\"x\":").Append(UiTreeJson.FloatStr(node.RectX));
            sb.Append(",\"y\":").Append(UiTreeJson.FloatStr(node.RectY));
            sb.Append(",\"w\":").Append(UiTreeJson.FloatStr(node.RectW));
            sb.Append(",\"h\":").Append(UiTreeJson.FloatStr(node.RectH)).Append("}");
            sb.Append(",\"center\":{\"x\":").Append(UiTreeJson.FloatStr(node.CenterX));
            sb.Append(",\"y\":").Append(UiTreeJson.FloatStr(node.CenterY)).Append("}");
            sb.Append(",\"children_count\":").Append(node.ChildrenCount);

            if (includeExtra)
            {
                sb.Append(",\"sorting_order\":").Append(node.SortingOrder);
                sb.Append(",\"sibling_index\":").Append(node.SiblingIndex);
                sb.Append(",\"raycast_target\":").Append(UiTreeJson.BoolStr(node.RaycastTarget));
                sb.Append(",\"canvas_group_interactable\":").Append(UiTreeJson.BoolStr(node.CanvasGroupInteractable));
                sb.Append(",\"canvas_group_blocks_raycasts\":").Append(UiTreeJson.BoolStr(node.CanvasGroupBlocksRaycasts));
            }

            if (node.Interactive && node.Interaction != null)
            {
                Vector2 gameViewSize = PiGameViewCoordinates.GetGameViewSize();
                float dragDistance = node.Interaction == "drag"
                    ? Math.Max(1f, Math.Min(node.RectW, node.RectH) * 0.4f)
                    : 0f;
                UiTreeJson.AppendInputHint(sb, node.Interaction,
                    includeCoordinates: true,
                    x: node.CenterX,
                    y: node.CenterY,
                    coordinateSpace: "gameview_top_left",
                    source: "ugui",
                    requiresFocus: node.Interaction == "type_text",
                    targetRef: node.Ref,
                    targetName: node.Name,
                    targetType: node.Type,
                    button: node.Interaction == "click" ? "left" : null,
                    gameViewWidth: gameViewSize.x,
                    gameViewHeight: gameViewSize.y,
                    handlers: node.Handlers,
                    dragDistance: dragDistance);

                UiTreeJson.AppendInputTemplate(sb, node.Interaction,
                    node.CenterX,
                    node.CenterY,
                    node.HasValueRange,
                    node.CurrentValue,
                    node.MinValue,
                    node.MaxValue,
                    node.WholeNumbers);
            }

            sb.Append("}");
        }

        // ─── Type detection ───

        private static string DetermineUguiType(GameObject go)
        {
            ProbeTMP();

            // Check specific types first
            if (go.GetComponent<Button>()) return "Button";
            if (go.GetComponent<Toggle>()) return "Toggle";
            if (go.GetComponent<Slider>()) return "Slider";
            if (go.GetComponent<Dropdown>()) return "Dropdown";
            if (go.GetComponent<ScrollRect>()) return "ScrollRect";
            if (go.GetComponent<Scrollbar>()) return "Scrollbar";
            if (go.GetComponent<InputField>()) return "InputField";

            // TMP types via reflection
            if (s_tmpInputType != null && go.GetComponent(s_tmpInputType)) return "TMP_InputField";
            if (s_tmpTextType != null && go.GetComponent(s_tmpTextType)) return "TMP_Text";

            if (go.GetComponent<Text>()) return "Text";
            if (go.GetComponent<Image>()) return "Image";
            if (go.GetComponent<RawImage>()) return "RawImage";

            if (go.GetComponent<Selectable>()) return "Selectable";
            if (go.GetComponent<Graphic>()) return "Graphic";
            if (go.GetComponent<Canvas>()) return "Canvas";

            return "RectTransform";
        }

        private static string DetermineInteraction(GameObject go)
        {
            ProbeTMP();

            if (!IsSelectableInteractable(go) ||
                !IsCanvasGroupInteractable(go) ||
                !DoesCanvasGroupBlockRaycasts(go))
                return null;

            if (go.GetComponent<Button>()) return "click";
            if (go.GetComponent<Toggle>()) return "click";

            if (go.GetComponent<InputField>()) return "type_text";
            if (s_tmpInputType != null && go.GetComponent(s_tmpInputType)) return "type_text";

            if (go.GetComponent<Slider>()) return "drag";
            if (go.GetComponent<Scrollbar>()) return "drag";
            if (go.GetComponent<ScrollRect>()) return "scroll";
            if (go.GetComponent<Dropdown>()) return "click";

            // Custom UGUI controls often expose EventSystems handlers directly
            // (for example, virtual joysticks implement IDragHandler).
            if (HasHandler<IDragHandler>(go)) return "drag";
            if (HasHandler<IScrollHandler>(go)) return "scroll";
            if (HasHandler<IPointerClickHandler>(go)) return "click";

            var selectable = go.GetComponent<Selectable>();
            if (selectable != null && selectable.interactable) return "click";

            return null;
        }

        private static bool IsInteractive(GameObject go)
        {
            return DetermineInteraction(go) != null;
        }

        private static bool HasHandler<T>(GameObject go)
        {
            var components = go.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] is T) return true;
            }
            return false;
        }

        private static string[] GetEventHandlerNames(GameObject go)
        {
            var names = new List<string>();
            if (HasHandler<IPointerClickHandler>(go)) names.Add("IPointerClickHandler");
            if (HasHandler<IPointerDownHandler>(go)) names.Add("IPointerDownHandler");
            if (HasHandler<IPointerUpHandler>(go)) names.Add("IPointerUpHandler");
            if (HasHandler<IBeginDragHandler>(go)) names.Add("IBeginDragHandler");
            if (HasHandler<IDragHandler>(go)) names.Add("IDragHandler");
            if (HasHandler<IEndDragHandler>(go)) names.Add("IEndDragHandler");
            if (HasHandler<IDropHandler>(go)) names.Add("IDropHandler");
            if (HasHandler<IScrollHandler>(go)) names.Add("IScrollHandler");
            return names.ToArray();
        }

        private static string ExtractText(GameObject go)
        {
            ProbeTMP();

            var text = go.GetComponent<Text>();
            if (text != null) return text.text;

            // TMP text via reflection
            if (s_tmpTextType != null)
            {
                var tmpComponent = go.GetComponent(s_tmpTextType);
                if (tmpComponent != null)
                {
                    var textProp = s_tmpTextType.GetProperty("text",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (textProp != null)
                        return textProp.GetValue(tmpComponent) as string;
                }
            }

            var inputField = go.GetComponent<InputField>();
            if (inputField != null) return inputField.text;

            if (s_tmpInputType != null)
            {
                var tmpInput = go.GetComponent(s_tmpInputType);
                if (tmpInput != null)
                {
                    var textProp = s_tmpInputType.GetProperty("text",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (textProp != null)
                        return textProp.GetValue(tmpInput) as string;
                }
            }

            return null;
        }

        private static bool IsVisibleOnScreen(RectTransform rt)
        {
            if (!rt.gameObject.activeInHierarchy) return false;

            var canvasGroup = rt.GetComponentInParent<CanvasGroup>();
            if (canvasGroup != null && canvasGroup.alpha < 0.01f) return false;

            return true;
        }

        // ─── Selector / Query matching ───

        private static bool MatchesSelector(GameObject go, string typeName, string text, string selector)
        {
            if (string.IsNullOrEmpty(selector)) return true;
            if (selector.StartsWith("#", StringComparison.Ordinal))
            {
                string nameFilter = selector.Substring(1);
                return go.name.Equals(nameFilter, StringComparison.OrdinalIgnoreCase);
            }

            if (selector.StartsWith(".", StringComparison.Ordinal))
                return false;

            return go.name.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (text != null && text.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool MatchesQuery(GameObject go, string typeName, string text,
            string path, string query)
        {
            if (string.IsNullOrEmpty(query)) return false;
            if (query.StartsWith("#", StringComparison.Ordinal))
            {
                string nameFilter = query.Substring(1);
                return go.name.Equals(nameFilter, StringComparison.OrdinalIgnoreCase);
            }

            if (query.StartsWith(".", StringComparison.Ordinal))
                return false;

            return go.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ─── Helpers ───

        internal static float GetGameViewHeight()
        {
            return PiGameViewCoordinates.GetGameViewHeight();
        }

        private static int CountElements(GameObject go)
        {
            int count = 1;
            for (int i = 0; i < go.transform.childCount; i++)
                count += CountElements(go.transform.GetChild(i).gameObject);
            return count;
        }

        private static bool IsSelectableInteractable(GameObject go)
        {
            var selectable = go.GetComponent<Selectable>();
            return selectable == null || selectable.interactable;
        }

        internal static bool IsCanvasGroupInteractable(GameObject go)
        {
            var groups = go.GetComponentsInParent<CanvasGroup>();
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (group == null) continue;
                if (!group.interactable) return false;
                if (group.ignoreParentGroups) break;
            }
            return true;
        }

        internal static bool DoesCanvasGroupBlockRaycasts(GameObject go)
        {
            var groups = go.GetComponentsInParent<CanvasGroup>();
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (group == null) continue;
                if (!group.blocksRaycasts) return false;
                if (group.ignoreParentGroups) break;
            }
            return true;
        }

        private static void TryGetValueRange(GameObject go, out bool hasValueRange,
            out float currentValue, out float minValue, out float maxValue, out bool wholeNumbers)
        {
            var slider = go.GetComponent<Slider>();
            if (slider != null)
            {
                hasValueRange = true;
                currentValue = slider.value;
                minValue = slider.minValue;
                maxValue = slider.maxValue;
                wholeNumbers = slider.wholeNumbers;
                return;
            }

            var scrollbar = go.GetComponent<Scrollbar>();
            if (scrollbar != null)
            {
                hasValueRange = true;
                currentValue = scrollbar.value;
                minValue = 0f;
                maxValue = 1f;
                wholeNumbers = false;
                return;
            }

            hasValueRange = false;
            currentValue = 0f;
            minValue = 0f;
            maxValue = 0f;
            wholeNumbers = false;
        }

        private static void ProbeTMP()
        {
            if (s_tmpProbed) return;
            s_tmpProbed = true;

            // Try to find TMP_Text (TextMeshPro or TextMeshProUGUI)
            s_tmpTextType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (s_tmpTextType == null)
                s_tmpTextType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");

            s_tmpInputType = Type.GetType("TMPro.TMP_InputField, Unity.TextMeshPro");
        }
    }
}

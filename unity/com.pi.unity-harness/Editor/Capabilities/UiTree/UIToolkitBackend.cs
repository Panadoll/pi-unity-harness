using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Pi.UnityHarness.Editor.Capabilities.UiTree
{
    /// <summary>
    /// UI Toolkit traversal backend. Discovers editor panels via reflection
    /// (UIElementsUtility.GetPanelsIterator) and runtime panels via UIDocument.
    /// Pattern adapted from unity-cli UITree.cs.
    /// </summary>
    internal static class UIToolkitBackend
    {
        private static readonly Dictionary<Type, PropertyInfo> s_actualViewPropCache = new();
        private static readonly Dictionary<Type, (PropertyInfo contextType, PropertyInfo visualTree, PropertyInfo ownerObject)> s_panelPropCache = new();
        private static bool s_loggedPanelReflectionWarning;

        // ─── Root discovery ───

        internal struct ToolkitRoot
        {
            public VisualElement Root;
            public string Name;
            public string ContextType;
            public int ElementCount;
            public bool Active;
        }

        internal static List<ToolkitRoot> GetRoots()
        {
            var results = new List<ToolkitRoot>();

            // Editor panels via reflection
            var editorPanels = GetEditorPanels();
            foreach (var p in editorPanels)
            {
                results.Add(new ToolkitRoot
                {
                    Root = p.Root,
                    Name = p.Name,
                    ContextType = p.ContextType,
                    ElementCount = CountElements(p.Root),
                    Active = true
                });
            }

            // Runtime panels via UIDocument
            var runtimePanels = GetRuntimePanels();
            foreach (var p in runtimePanels)
            {
                results.Add(new ToolkitRoot
                {
                    Root = p.Root,
                    Name = p.Name,
                    ContextType = "Player",
                    ElementCount = CountElements(p.Root),
                    Active = true
                });
            }

            return results;
        }

        // ─── Node collection ───

        internal struct ToolkitNode
        {
            public string Ref;
            public string Type;
            public string Name;
            public string Text;
            public string[] Classes;
            public string Path;
            public bool Enabled;
            public bool Visible;
            public bool Interactive;
            public string Interaction;
            public float RectX, RectY, RectW, RectH;
            public float CenterX, CenterY;
            public int ChildrenCount;
            public bool Focusable;
            public string ContextType;
        }

        internal static List<ToolkitNode> CollectNodes(VisualElement root,
            bool interactiveOnly, int maxDepth, int limit,
            string selector, bool includeInvisible, string contextType)
        {
            var nodes = new List<ToolkitNode>();
            CollectRecursive(root, nodes, interactiveOnly, maxDepth, 0, limit,
                selector, includeInvisible, "", contextType);
            return nodes;
        }

        private static void CollectRecursive(VisualElement ve, List<ToolkitNode> nodes,
            bool interactiveOnly, int maxDepth, int depth, int limit,
            string selector, bool includeInvisible, string parentPath, string contextType)
        {
            if (nodes.Count >= limit) return;
            if (maxDepth >= 0 && depth > maxDepth) return;

            string typeName = ve.GetType().Name;
            string currentPath = string.IsNullOrEmpty(parentPath)
                ? typeName
                : parentPath + " > " + typeName;

            bool isVisible = ve.visible && ve.resolvedStyle.display != DisplayStyle.None;
            if (!includeInvisible && !isVisible)
            {
                foreach (var child in ve.Children())
                    CollectRecursive(child, nodes, interactiveOnly, maxDepth, depth + 1,
                        limit, selector, includeInvisible, currentPath, contextType);
                return;
            }

            bool isInteractive = IsInteractive(ve);
            string interaction = DetermineInteraction(ve);
            string text = ExtractText(ve);

            // Selector filter
            if (!string.IsNullOrEmpty(selector) && !MatchesSelector(ve, typeName, text, selector))
            {
                foreach (var child in ve.Children())
                    CollectRecursive(child, nodes, interactiveOnly, maxDepth, depth + 1,
                        limit, selector, includeInvisible, currentPath, contextType);
                return;
            }

            if (interactiveOnly && !isInteractive)
            {
                foreach (var child in ve.Children())
                    CollectRecursive(child, nodes, interactiveOnly, maxDepth, depth + 1,
                        limit, selector, includeInvisible, currentPath, contextType);
                return;
            }

            var layout = ve.layout;
            var worldBound = ve.worldBound;

            nodes.Add(new ToolkitNode
            {
                Ref = UiTreeRefManager.FindOrAssignRef(ve),
                Type = typeName,
                Name = string.IsNullOrEmpty(ve.name) ? null : ve.name,
                Text = text,
                Classes = ve.GetClasses().ToArray(),
                Path = currentPath,
                Enabled = ve.enabledInHierarchy,
                Visible = isVisible,
                Interactive = isInteractive,
                Interaction = interaction,
                RectX = worldBound.x,
                RectY = worldBound.y,
                RectW = worldBound.width,
                RectH = worldBound.height,
                CenterX = worldBound.center.x,
                CenterY = worldBound.center.y,
                ChildrenCount = ve.childCount,
                Focusable = ve.focusable,
                ContextType = contextType
            });

            foreach (var child in ve.Children())
                CollectRecursive(child, nodes, interactiveOnly, maxDepth, depth + 1,
                    limit, selector, includeInvisible, currentPath, contextType);
        }

        internal static int CountMatchingNodes(VisualElement root,
            bool interactiveOnly, int maxDepth, string selector, bool includeInvisible)
        {
            int count = 0;
            CountMatchingRecursive(root, interactiveOnly, maxDepth, 0, selector, includeInvisible, ref count);
            return count;
        }

        private static void CountMatchingRecursive(VisualElement ve,
            bool interactiveOnly, int maxDepth, int depth,
            string selector, bool includeInvisible, ref int count)
        {
            if (maxDepth >= 0 && depth > maxDepth) return;

            string typeName = ve.GetType().Name;
            bool isVisible = ve.visible && ve.resolvedStyle.display != DisplayStyle.None;
            string text = NeedsTextForSelector(selector) ? ExtractText(ve) : null;
            bool selectorMatches = string.IsNullOrEmpty(selector) ||
                MatchesSelector(ve, typeName, text, selector);
            bool interactiveMatches = !interactiveOnly || IsInteractive(ve);

            if ((includeInvisible || isVisible) && selectorMatches && interactiveMatches)
                count++;

            foreach (var child in ve.Children())
                CountMatchingRecursive(child, interactiveOnly, maxDepth, depth + 1,
                    selector, includeInvisible, ref count);
        }

        // ─── Search ───

        internal static List<ToolkitNode> Search(VisualElement root, string query,
            int limit, bool includeInvisible, string contextType)
        {
            var nodes = new List<ToolkitNode>();
            SearchRecursive(root, nodes, query, limit, includeInvisible, "", contextType);
            return nodes;
        }

        private static void SearchRecursive(VisualElement ve, List<ToolkitNode> nodes,
            string query, int limit, bool includeInvisible, string parentPath, string contextType)
        {
            if (nodes.Count >= limit) return;

            string typeName = ve.GetType().Name;
            string currentPath = string.IsNullOrEmpty(parentPath)
                ? typeName
                : parentPath + " > " + typeName;

            bool isVisible = ve.visible && ve.resolvedStyle.display != DisplayStyle.None;
            if (!includeInvisible && !isVisible)
            {
                foreach (var child in ve.Children())
                    SearchRecursive(child, nodes, query, limit, includeInvisible, currentPath, contextType);
                return;
            }

            string text = ExtractText(ve);
            if (MatchesQuery(ve, typeName, text, currentPath, query))
            {
                var worldBound = ve.worldBound;
                nodes.Add(new ToolkitNode
                {
                    Ref = UiTreeRefManager.FindOrAssignRef(ve),
                    Type = typeName,
                    Name = string.IsNullOrEmpty(ve.name) ? null : ve.name,
                    Text = text,
                    Classes = ve.GetClasses().ToArray(),
                    Path = currentPath,
                    Enabled = ve.enabledInHierarchy,
                    Visible = isVisible,
                    Interactive = IsInteractive(ve),
                    Interaction = DetermineInteraction(ve),
                    RectX = worldBound.x,
                    RectY = worldBound.y,
                    RectW = worldBound.width,
                    RectH = worldBound.height,
                    CenterX = worldBound.center.x,
                    CenterY = worldBound.center.y,
                    ChildrenCount = ve.childCount,
                    Focusable = ve.focusable,
                    ContextType = contextType
                });
            }

            foreach (var child in ve.Children())
                SearchRecursive(child, nodes, query, limit, includeInvisible, currentPath, contextType);
        }

        internal static int CountSearchMatches(VisualElement root, string query, bool includeInvisible)
        {
            int count = 0;
            CountSearchMatchesRecursive(root, query, includeInvisible, "", ref count);
            return count;
        }

        private static void CountSearchMatchesRecursive(VisualElement ve, string query,
            bool includeInvisible, string parentPath, ref int count)
        {
            string typeName = ve.GetType().Name;
            string currentPath = string.IsNullOrEmpty(parentPath)
                ? typeName
                : parentPath + " > " + typeName;

            bool isVisible = ve.visible && ve.resolvedStyle.display != DisplayStyle.None;
            string text = NeedsTextForSelector(query) ? ExtractText(ve) : null;

            if ((includeInvisible || isVisible) && MatchesQuery(ve, typeName, text, currentPath, query))
                count++;

            foreach (var child in ve.Children())
                CountSearchMatchesRecursive(child, query, includeInvisible, currentPath, ref count);
        }

        // ─── Describe (single node detail) ───

        internal static string DescribeNode(VisualElement ve, string refId, int maxDepth, int limit)
        {
            var sb = new StringBuilder();
            sb.Append("{\"status\":\"succeeded\"");
            sb.Append(",\"ref\":\"").Append(UiTreeJson.Escape(refId)).Append("\"");
            sb.Append(",\"source\":\"uitoolkit\"");
            sb.Append(",\"type\":\"").Append(UiTreeJson.Escape(ve.GetType().Name)).Append("\"");
            sb.Append(",\"name\":");
            if (!string.IsNullOrEmpty(ve.name))
                sb.Append("\"").Append(UiTreeJson.Escape(ve.name)).Append("\"");
            else
                sb.Append("null");

            sb.Append(",\"classes\":");
            UiTreeJson.AppendStringArray(sb, ve.GetClasses());

            sb.Append(",\"visible\":").Append(UiTreeJson.BoolStr(ve.visible));
            sb.Append(",\"enabled_self\":").Append(UiTreeJson.BoolStr(ve.enabledSelf));
            sb.Append(",\"enabled_in_hierarchy\":").Append(UiTreeJson.BoolStr(ve.enabledInHierarchy));
            sb.Append(",\"focusable\":").Append(UiTreeJson.BoolStr(ve.focusable));
            sb.Append(",\"tooltip\":\"").Append(UiTreeJson.Escape(ve.tooltip ?? "")).Append("\"");
            sb.Append(",\"path\":\"").Append(UiTreeJson.Escape(BuildElementPath(ve))).Append("\"");

            var layout = ve.layout;
            sb.Append(",\"layout\":{\"x\":").Append(UiTreeJson.FloatStr(layout.x));
            sb.Append(",\"y\":").Append(UiTreeJson.FloatStr(layout.y));
            sb.Append(",\"w\":").Append(UiTreeJson.FloatStr(layout.width));
            sb.Append(",\"h\":").Append(UiTreeJson.FloatStr(layout.height)).Append("}");

            var wb = ve.worldBound;
            sb.Append(",\"world_bound\":{\"x\":").Append(UiTreeJson.FloatStr(wb.x));
            sb.Append(",\"y\":").Append(UiTreeJson.FloatStr(wb.y));
            sb.Append(",\"w\":").Append(UiTreeJson.FloatStr(wb.width));
            sb.Append(",\"h\":").Append(UiTreeJson.FloatStr(wb.height)).Append("}");

            sb.Append(",\"children_count\":").Append(ve.childCount);

            string text = ExtractText(ve);
            sb.Append(",\"text\":");
            if (text != null)
                sb.Append("\"").Append(UiTreeJson.Escape(text)).Append("\"");
            else
                sb.Append("null");

            // Children summary (up to limit)
            if (ve.childCount > 0 && maxDepth > 0)
            {
                sb.Append(",\"children\":[");
                int count = 0;
                foreach (var child in ve.Children())
                {
                    if (count >= limit) break;
                    if (count > 0) sb.Append(",");
                    var childRef = UiTreeRefManager.FindOrAssignRef(child);
                    sb.Append("{\"ref\":\"").Append(UiTreeJson.Escape(childRef));
                    sb.Append("\",\"type\":\"").Append(UiTreeJson.Escape(child.GetType().Name));
                    sb.Append("\",\"name\":");
                    if (!string.IsNullOrEmpty(child.name))
                        sb.Append("\"").Append(UiTreeJson.Escape(child.name)).Append("\"");
                    else
                        sb.Append("null");
                    sb.Append(",\"classes\":");
                    UiTreeJson.AppendStringArray(sb, child.GetClasses());
                    sb.Append("}");
                    count++;
                }
                sb.Append("]");
            }

            sb.Append("}");
            return sb.ToString();
        }

        // ─── Node serialization ───

        internal static void AppendNodeJson(StringBuilder sb, ToolkitNode node, bool includeExtra = true)
        {
            sb.Append("{\"ref\":\"").Append(UiTreeJson.Escape(node.Ref));
            sb.Append("\",\"source\":\"uitoolkit\"");
            sb.Append(",\"type\":\"").Append(UiTreeJson.Escape(node.Type));
            sb.Append("\",\"name\":");
            if (node.Name != null)
                sb.Append("\"").Append(UiTreeJson.Escape(node.Name)).Append("\"");
            else
                sb.Append("null");
            sb.Append(",\"text\":");
            if (node.Text != null)
                sb.Append("\"").Append(UiTreeJson.Escape(node.Text)).Append("\"");
            else
                sb.Append("null");
            sb.Append(",\"classes\":");
            UiTreeJson.AppendStringArray(sb, node.Classes);
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
                sb.Append(",\"focusable\":").Append(UiTreeJson.BoolStr(node.Focusable));
                sb.Append(",\"context_type\":\"").Append(UiTreeJson.Escape(node.ContextType ?? "")).Append("\"");
            }

            if (node.Interactive && node.Interaction != null)
            {
                bool runtimePanel = string.Equals(node.ContextType, "Player", StringComparison.OrdinalIgnoreCase);
                UiTreeJson.AppendInputHint(sb, node.Interaction,
                    includeCoordinates: runtimePanel,
                    x: node.CenterX,
                    y: node.CenterY,
                    coordinateSpace: runtimePanel ? "uitoolkit_world_bound" : "editor_panel_not_injectable",
                    source: "uitoolkit",
                    requiresFocus: node.Interaction == "type_text",
                    targetRef: node.Ref,
                    targetName: node.Name ?? "",
                    targetType: node.Type,
                    button: node.Interaction == "click" ? "left" : null);
            }

            sb.Append("}");
        }

        // ─── Panel discovery (reflection) ───

        private struct PanelInfo
        {
            public string Name;
            public string ContextType;
            public VisualElement Root;
        }

        private static List<PanelInfo> GetEditorPanels()
        {
            var results = new List<PanelInfo>();

            var utilityType = Type.GetType(
                "UnityEngine.UIElements.UIElementsUtility, UnityEngine.UIElementsModule");
            if (utilityType == null)
                return results;

            var getIteratorMethod = utilityType.GetMethod(
                "GetPanelsIterator",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (getIteratorMethod == null)
                return results;

            object iterator;
            try
            {
                iterator = getIteratorMethod.Invoke(null, null);
            }
            catch (Exception ex)
            {
                LogPanelReflectionWarning("GetPanelsIterator invocation failed", ex);
                return results;
            }

            try
            {
                if (iterator is IEnumerator enumerator)
                {
                    while (enumerator.MoveNext())
                        ProcessPanelEntry(enumerator.Current, results);
                }
                else
                {
                    var iteratorType = iterator.GetType();
                    var moveNextMethod = iteratorType.GetMethod("MoveNext");
                    var currentProp = iteratorType.GetProperty("Current");
                    if (moveNextMethod != null && currentProp != null)
                    {
                        while ((bool)moveNextMethod.Invoke(iterator, null))
                        {
                            var kvp = currentProp.GetValue(iterator);
                            ProcessPanelEntry(kvp, results);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogPanelReflectionWarning("GetPanelsIterator enumeration failed", ex);
            }

            return results;
        }

        private static void LogPanelReflectionWarning(string message, Exception ex)
        {
            if (s_loggedPanelReflectionWarning) return;
            s_loggedPanelReflectionWarning = true;
            Debug.LogWarning("[Harness.UiTree] Editor panel discovery via internal UIElementsUtility reflection failed: " +
                message + ". UI Toolkit editor panels may be missing. " + ex.Message);
        }

        private static void ProcessPanelEntry(object kvp, List<PanelInfo> results)
        {
            if (kvp == null) return;

            var kvpType = kvp.GetType();
            var valueProp = kvpType.GetProperty("Value");
            var panel = valueProp?.GetValue(kvp);
            if (panel == null) return;

            var panelType = panel.GetType();
            var props = GetPanelProperties(panelType);

            var contextType = props.contextType?.GetValue(panel)?.ToString() ?? "Unknown";
            var visualTree = props.visualTree?.GetValue(panel) as VisualElement;
            if (visualTree == null) return;

            var ownerObject = props.ownerObject?.GetValue(panel) as ScriptableObject;
            string panelName = DeriveEnrichedPanelName(ownerObject, contextType);

            results.Add(new PanelInfo
            {
                Name = panelName,
                ContextType = contextType,
                Root = visualTree
            });
        }

        private static (PropertyInfo contextType, PropertyInfo visualTree, PropertyInfo ownerObject)
            GetPanelProperties(Type panelType)
        {
            if (s_panelPropCache.TryGetValue(panelType, out var cached))
                return cached;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var entry = (
                contextType: panelType.GetProperty("contextType", flags),
                visualTree: panelType.GetProperty("visualTree", flags),
                ownerObject: panelType.GetProperty("ownerObject", flags)
            );
            s_panelPropCache[panelType] = entry;
            return entry;
        }

        private static string DeriveEnrichedPanelName(ScriptableObject ownerObject, string contextType)
        {
            if (ownerObject == null)
                return "Panel_" + contextType;

            var ownerTypeName = ownerObject.GetType().Name;
            string viewTypeName = null;

            try
            {
                var ownerType = ownerObject.GetType();
                if (!s_actualViewPropCache.TryGetValue(ownerType, out var prop))
                {
                    prop = ownerType.GetProperty("actualView",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_actualViewPropCache[ownerType] = prop;
                }

                if (prop != null)
                {
                    var view = prop.GetValue(ownerObject);
                    if (view != null)
                        viewTypeName = view.GetType().Name;
                }
            }
            catch
            {
                // Reflection may fail
            }

            if (viewTypeName != null)
                return ownerTypeName + ":" + viewTypeName;

            return ownerTypeName;
        }

        private static List<PanelInfo> GetRuntimePanels()
        {
            var results = new List<PanelInfo>();

            UIDocument[] uiDocuments;
            try
            {
#pragma warning disable CS0618 // FindObjectsOfType is deprecated but needed for 2021.3 compat
                uiDocuments = UnityEngine.Object.FindObjectsOfType<UIDocument>();
#pragma warning restore CS0618
            }
            catch
            {
                // FindObjectsOfType may fail in certain editor states
                return results;
            }

            foreach (var doc in uiDocuments)
            {
                if (doc == null || doc.rootVisualElement == null) continue;

                var panelSettingsName = doc.panelSettings != null
                    ? doc.panelSettings.name
                    : "Unknown";
                var panelName = "UIDocument " + panelSettingsName + " (" + doc.gameObject.name + ")";

                results.Add(new PanelInfo
                {
                    Name = panelName,
                    ContextType = "Player",
                    Root = doc.rootVisualElement
                });
            }

            return results;
        }

        // ─── Interaction detection ───

        private static bool IsInteractive(VisualElement ve)
        {
            return DetermineInteraction(ve) != null;
        }

        private static string DetermineInteraction(VisualElement ve)
        {
            if (ve is Button) return "click";
            if (ve is Toggle) return "click";
            if (ve is TextField) return "type_text";
            if (ve is Slider || ve is SliderInt) return "drag";
            if (ve is ScrollView) return "scroll";
            if (ve is DropdownField) return "click";
            if (ve is Foldout) return "click";

            // Generic clickable check
            if (ve.focusable && ve.enabledInHierarchy)
            {
                // Check if it has a clickable manipulator
                // This is a heuristic; focusable + enabled = likely interactive
                var typeName = ve.GetType().Name;
                if (typeName.Contains("Button") || typeName.Contains("Toggle"))
                    return "click";
            }

            return null;
        }

        private static string ExtractText(VisualElement ve)
        {
            if (ve is TextElement textElement)
                return textElement.text;

            if (ve is BaseField<string> stringField)
                return stringField.value;

            // Try first child TextElement
            var childText = ve.Q<TextElement>();
            if (childText != null && childText.parent == ve)
                return childText.text;

            return null;
        }

        // ─── Helpers ───

        private static bool NeedsTextForSelector(string selector)
        {
            return !string.IsNullOrEmpty(selector) &&
                !selector.StartsWith("#", StringComparison.Ordinal) &&
                !selector.StartsWith(".", StringComparison.Ordinal);
        }

        private static bool MatchesSelector(VisualElement ve, string typeName, string text, string selector)
        {
            if (string.IsNullOrEmpty(selector)) return true;

            // Strip # or . prefix for name/class matching
            if (selector.StartsWith("#"))
            {
                string nameFilter = selector.Substring(1);
                return !string.IsNullOrEmpty(ve.name) &&
                       ve.name.Equals(nameFilter, StringComparison.OrdinalIgnoreCase);
            }

            if (selector.StartsWith("."))
            {
                string classFilter = selector.Substring(1);
                return ve.GetClasses().Any(c =>
                    c.Equals(classFilter, StringComparison.OrdinalIgnoreCase));
            }

            // General partial match
            return typeName.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (!string.IsNullOrEmpty(ve.name) &&
                    ve.name.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (text != null && text.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   ve.GetClasses().Any(c =>
                       c.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool MatchesQuery(VisualElement ve, string typeName, string text,
            string path, string query)
        {
            if (string.IsNullOrEmpty(query)) return false;

            // Support # and . prefix
            if (query.StartsWith("#"))
            {
                string nameFilter = query.Substring(1);
                return !string.IsNullOrEmpty(ve.name) &&
                       ve.name.Equals(nameFilter, StringComparison.OrdinalIgnoreCase);
            }

            if (query.StartsWith("."))
            {
                string classFilter = query.Substring(1);
                return ve.GetClasses().Any(c =>
                    c.Equals(classFilter, StringComparison.OrdinalIgnoreCase));
            }

            return typeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (!string.IsNullOrEmpty(ve.name) &&
                    ve.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string BuildElementPath(VisualElement ve)
        {
            var parts = new List<string>();
            var current = ve;
            while (current != null)
            {
                parts.Add(current.GetType().Name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join(" > ", parts);
        }

        private static int CountElements(VisualElement root)
        {
            int count = 1;
            foreach (var child in root.Children())
                count += CountElements(child);
            return count;
        }

        /// <summary>
        /// Find a root VisualElement by name (case-insensitive, partial match fallback).
        /// </summary>
        internal static VisualElement FindRoot(string rootName)
        {
            if (string.IsNullOrEmpty(rootName)) return null;

            var roots = GetRoots();

            // Exact match
            foreach (var r in roots)
                if (r.Name.Equals(rootName, StringComparison.OrdinalIgnoreCase))
                    return r.Root;

            // Partial match
            foreach (var r in roots)
                if (r.Name.IndexOf(rootName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return r.Root;

            return null;
        }
    }
}

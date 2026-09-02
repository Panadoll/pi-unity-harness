using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Pi.UnityHarness.Editor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    /// <summary>
    /// Collects UI + 3D click candidates and optionally draws markers onto a capture PNG.
    /// Annotation coordinates use top-left GameView space (same as input_click / input_raycast).
    /// </summary>
    public static class PiVisionAnnotator
    {
        private const int DefaultGridColumns = 5;
        private const int DefaultGridRows = 5;
        private const float DefaultMaxDistance = PiGameViewPhysicsRaycast.DefaultMaxDistance;

        public static PiVisionAnnotationSet Collect(
            int gridColumns = DefaultGridColumns,
            int gridRows = DefaultGridRows,
            float maxDistance = DefaultMaxDistance,
            bool includeUi = true,
            bool includePhysics = true)
        {
            Vector2 gameViewSize = PiGameViewCoordinates.GetGameViewSize();
            var set = new PiVisionAnnotationSet
            {
                GameViewWidth = gameViewSize.x,
                GameViewHeight = gameViewSize.y,
                InputCoordinateSystem = PiGameViewCoordinates.InputCoordinateSystem,
                UnityCoordinateSystem = PiGameViewCoordinates.UnityCoordinateSystem,
                ConversionFormula = PiGameViewCoordinates.ConversionFormula,
            };

            if (includeUi)
                CollectUiAnnotations(set);

            if (includePhysics)
                CollectPhysicsGrid(set, gridColumns, gridRows, maxDistance);

            return set;
        }

        public static string AttachToCaptureJson(string captureJson, PiVisionAnnotationSet annotations, bool drawOnImage)
        {
            if (string.IsNullOrEmpty(captureJson) || annotations == null)
                return captureJson;

            if (drawOnImage)
            {
                string path = TryExtractPath(captureJson);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try
                    {
                        DrawMarkersOnPng(path, annotations);
                    }
                    catch (Exception)
                    {
                        // Keep JSON annotations even if drawing fails.
                    }
                }
            }

            string annotationsJson = ToJsonObject(annotations);
            if (captureJson.EndsWith("}", StringComparison.Ordinal))
            {
                return captureJson.Substring(0, captureJson.Length - 1) +
                       ",\"annotations\":" + annotationsJson +
                       ",\"input_coordinate_system\":\"" + PiUnityJsonHelper.EscapeJson(annotations.InputCoordinateSystem) + "\"" +
                       ",\"unity_coordinate_system\":\"" + PiUnityJsonHelper.EscapeJson(annotations.UnityCoordinateSystem) + "\"" +
                       ",\"coordinate_conversion_formula\":\"" + PiUnityJsonHelper.EscapeJson(annotations.ConversionFormula) + "\"" +
                       "}";
            }

            return captureJson;
        }

        public static string ToJsonObject(PiVisionAnnotationSet set)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"schema\":\"harness.vision.annotations.v1\"");
            sb.Append(",\"game_view_width\":").Append(F(set.GameViewWidth));
            sb.Append(",\"game_view_height\":").Append(F(set.GameViewHeight));
            sb.Append(",\"ui\":[");
            for (int i = 0; i < set.Ui.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendUi(sb, set.Ui[i]);
            }
            sb.Append("],\"physics\":[");
            for (int i = 0; i < set.Physics.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendPhysics(sb, set.Physics[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void CollectUiAnnotations(PiVisionAnnotationSet set)
        {
            EventSystem eventSystem = EventSystem.current;
            Selectable[] selectables = UnityEngine.Object.FindObjectsOfType<Selectable>();
            int labelIndex = 0;

            foreach (Selectable selectable in selectables)
            {
                if (selectable == null || !selectable.isActiveAndEnabled || !selectable.interactable)
                    continue;

                var rt = selectable.transform as RectTransform;
                if (rt == null)
                    continue;

                Vector2 centerTopLeft = RectTransformToTopLeftCenter(rt, set.GameViewHeight);
                bool reachable = false;
                string blockedBy = null;
                if (eventSystem == null)
                {
                    blockedBy = "event_system_missing";
                }
                else
                {
                    RaycastResult hit = PiUiRaycastHelper.RaycastUI(
                        new Vector2(centerTopLeft.x, set.GameViewHeight - centerTopLeft.y),
                        eventSystem);
                    if (hit.gameObject == null)
                    {
                        blockedBy = "no_eventsystem_hit";
                    }
                    else if (hit.gameObject == selectable.gameObject ||
                             hit.gameObject.transform.IsChildOf(selectable.transform) ||
                             selectable.transform.IsChildOf(hit.gameObject.transform))
                    {
                        reachable = true;
                    }
                    else
                    {
                        blockedBy = "blocked_by_" + hit.gameObject.name;
                    }
                }

                if (!reachable)
                    continue;

                set.Ui.Add(new PiVisionUiAnnotation
                {
                    Label = GenerateAlphaLabel(labelIndex++),
                    Name = selectable.gameObject.name,
                    Path = PiGameViewPhysicsRaycast.GetHierarchyPath(selectable.gameObject),
                    InputX = centerTopLeft.x,
                    InputY = centerTopLeft.y,
                    Interaction = "click",
                    Reachable = true,
                    BlockedBy = blockedBy,
                });
            }
        }

        private static void CollectPhysicsGrid(
            PiVisionAnnotationSet set,
            int columns,
            int rows,
            float maxDistance)
        {
            columns = Mathf.Clamp(columns, 1, 20);
            rows = Mathf.Clamp(rows, 1, 20);
            Physics.SyncTransforms();

            int labelIndex = 1;
            for (int row = 1; row <= rows; row++)
            {
                for (int col = 1; col <= columns; col++)
                {
                    float x = set.GameViewWidth * col / (columns + 1f);
                    float y = set.GameViewHeight * row / (rows + 1f);
                    var result = PiGameViewPhysicsRaycast.RaycastFromInput(
                        x, y, maxDistance, Physics.DefaultRaycastLayers, false);

                    // Skip empty cells to keep agent context small.
                    if (!result.Success || !result.Hit)
                        continue;

                    // Prefer UI when overlay blocks this cell.
                    EventSystem es = EventSystem.current;
                    if (es != null)
                    {
                        RaycastResult uiHit = PiUiRaycastHelper.RaycastUI(
                            result.Conversion.UnityPosition, es);
                        if (uiHit.gameObject != null)
                            continue;
                    }

                    var hit = result.NearestHit.Value;
                    var go = hit.collider.gameObject;
                    set.Physics.Add(new PiVisionPhysicsAnnotation
                    {
                        Label = "R" + labelIndex,
                        InputX = x,
                        InputY = y,
                        Name = go.name,
                        Path = PiGameViewPhysicsRaycast.GetHierarchyPath(go),
                        Layer = go.layer,
                        LayerName = LayerMask.LayerToName(go.layer),
                        Distance = hit.distance,
                        HitPointX = hit.point.x,
                        HitPointY = hit.point.y,
                        HitPointZ = hit.point.z,
                    });
                    labelIndex++;
                }
            }
        }

        public static void DrawMarkersOnPng(string path, PiVisionAnnotationSet set)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return;
            }

            float scaleX = set.GameViewWidth > 0 ? texture.width / set.GameViewWidth : 1f;
            float scaleY = set.GameViewHeight > 0 ? texture.height / set.GameViewHeight : 1f;

            foreach (var ui in set.Ui)
                DrawCross(texture, ui.InputX * scaleX, ui.InputY * scaleY, new Color(0.1f, 0.95f, 1f, 1f), 8);

            foreach (var phys in set.Physics)
                DrawCross(texture, phys.InputX * scaleX, phys.InputY * scaleY, new Color(1f, 0.45f, 0.1f, 1f), 8);

            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static void DrawCross(Texture2D texture, float x, float y, Color color, int arm)
        {
            int cx = Mathf.Clamp(Mathf.RoundToInt(x), 0, texture.width - 1);
            // PNG texture origin is bottom-left; input y is top-left.
            int cyTopLeft = Mathf.Clamp(Mathf.RoundToInt(y), 0, texture.height - 1);
            int cy = texture.height - 1 - cyTopLeft;

            for (int i = -arm; i <= arm; i++)
            {
                SetPixelSafe(texture, cx + i, cy, color);
                SetPixelSafe(texture, cx, cy + i, color);
            }
            texture.Apply();
        }

        private static void SetPixelSafe(Texture2D texture, int x, int y, Color color)
        {
            if (x < 0 || y < 0 || x >= texture.width || y >= texture.height)
                return;
            texture.SetPixel(x, y, color);
        }

        private static Vector2 RectTransformToTopLeftCenter(RectTransform rt, float gameViewHeight)
        {
            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector3 center = (corners[0] + corners[2]) * 0.5f;
            Camera eventCamera = null;
            Canvas canvas = rt.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                eventCamera = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;

            Vector2 screen = RectTransformUtility.WorldToScreenPoint(eventCamera, center);
            return new Vector2(screen.x, gameViewHeight - screen.y);
        }

        private static string GenerateAlphaLabel(int index)
        {
            string label = "";
            int remaining = index;
            do
            {
                label = (char)('A' + remaining % 26) + label;
                remaining = remaining / 26 - 1;
            } while (remaining >= 0);
            return label;
        }

        private static string TryExtractPath(string captureJson)
        {
            const string key = "\"path\":\"";
            int idx = captureJson.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + key.Length;
            int end = captureJson.IndexOf('"', start);
            if (end < 0) return null;
            return captureJson.Substring(start, end - start).Replace("\\/", "/").Replace("\\\\", "\\");
        }

        private static void AppendUi(StringBuilder sb, PiVisionUiAnnotation ui)
        {
            sb.Append('{');
            sb.Append("\"label\":\"").Append(PiUnityJsonHelper.EscapeJson(ui.Label)).Append('"');
            sb.Append(",\"name\":\"").Append(PiUnityJsonHelper.EscapeJson(ui.Name)).Append('"');
            sb.Append(",\"path\":\"").Append(PiUnityJsonHelper.EscapeJson(ui.Path)).Append('"');
            sb.Append(",\"input_x\":").Append(F(ui.InputX));
            sb.Append(",\"input_y\":").Append(F(ui.InputY));
            sb.Append(",\"interaction\":\"").Append(PiUnityJsonHelper.EscapeJson(ui.Interaction)).Append('"');
            sb.Append(",\"reachable\":").Append(ui.Reachable ? "true" : "false");
            sb.Append('}');
        }

        private static void AppendPhysics(StringBuilder sb, PiVisionPhysicsAnnotation p)
        {
            sb.Append('{');
            sb.Append("\"label\":\"").Append(PiUnityJsonHelper.EscapeJson(p.Label)).Append('"');
            sb.Append(",\"name\":\"").Append(PiUnityJsonHelper.EscapeJson(p.Name)).Append('"');
            sb.Append(",\"path\":\"").Append(PiUnityJsonHelper.EscapeJson(p.Path)).Append('"');
            sb.Append(",\"input_x\":").Append(F(p.InputX));
            sb.Append(",\"input_y\":").Append(F(p.InputY));
            sb.Append(",\"layer\":").Append(p.Layer);
            sb.Append(",\"layer_name\":\"").Append(PiUnityJsonHelper.EscapeJson(p.LayerName ?? string.Empty)).Append('"');
            sb.Append(",\"distance\":").Append(F(p.Distance));
            sb.Append(",\"hit_point\":{\"x\":").Append(F(p.HitPointX))
                .Append(",\"y\":").Append(F(p.HitPointY))
                .Append(",\"z\":").Append(F(p.HitPointZ)).Append('}');
            sb.Append('}');
        }

        private static string F(float value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public sealed class PiVisionAnnotationSet
    {
        public float GameViewWidth;
        public float GameViewHeight;
        public string InputCoordinateSystem;
        public string UnityCoordinateSystem;
        public string ConversionFormula;
        public readonly List<PiVisionUiAnnotation> Ui = new List<PiVisionUiAnnotation>();
        public readonly List<PiVisionPhysicsAnnotation> Physics = new List<PiVisionPhysicsAnnotation>();
    }

    public sealed class PiVisionUiAnnotation
    {
        public string Label;
        public string Name;
        public string Path;
        public float InputX;
        public float InputY;
        public string Interaction;
        public bool Reachable;
        public string BlockedBy;
    }

    public sealed class PiVisionPhysicsAnnotation
    {
        public string Label;
        public string Name;
        public string Path;
        public float InputX;
        public float InputY;
        public int Layer;
        public string LayerName;
        public float Distance;
        public float HitPointX;
        public float HitPointY;
        public float HitPointZ;
    }
}

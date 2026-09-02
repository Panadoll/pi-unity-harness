using System.Text;
using Pi.UnityHarness.Editor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    /// <summary>
    /// Physics raycast from top-left GameView coordinates through the active game camera.
    /// Mirrors the unity-cli-loop GameViewRaycast approach without MCP tooling.
    /// </summary>
    public static class PiGameViewPhysicsRaycast
    {
        public const float DefaultMaxDistance = 1000f;

        public static PiGameViewPhysicsRaycastResult RaycastFromInput(
            float x,
            float y,
            float maxDistance = DefaultMaxDistance,
            int layerMask = Physics.DefaultRaycastLayers,
            bool syncTransforms = true,
            Camera camera = null)
        {
            return RaycastFromInput(new Vector2(x, y), maxDistance, layerMask, syncTransforms, camera);
        }

        public static PiGameViewPhysicsRaycastResult RaycastFromInput(
            Vector2 inputPosition,
            float maxDistance = DefaultMaxDistance,
            int layerMask = Physics.DefaultRaycastLayers,
            bool syncTransforms = true,
            Camera camera = null)
        {
            if (maxDistance <= 0f || float.IsNaN(maxDistance) || float.IsInfinity(maxDistance))
            {
                return PiGameViewPhysicsRaycastResult.Failed(
                    PiGameViewCoordinates.ConvertInputToUnity(inputPosition),
                    "MaxDistance must be positive and finite.");
            }

            PiGameViewCoordinateConversion conversion = PiGameViewCoordinates.ConvertInputToUnity(inputPosition);
            Camera cam = camera != null ? camera : Camera.main;
            if (cam == null || !cam.enabled || cam.gameObject == null || !cam.gameObject.activeInHierarchy)
            {
                return PiGameViewPhysicsRaycastResult.NoCamera(conversion);
            }

            if (syncTransforms)
                Physics.SyncTransforms();

            int visibleMask = layerMask & cam.cullingMask;
            Ray ray = cam.ScreenPointToRay(conversion.UnityPosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance, visibleMask);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            return PiGameViewPhysicsRaycastResult.FromHits(conversion, cam, hits);
        }

        public static string ToJson(PiGameViewPhysicsRaycastResult result)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendString(sb, "status", result.Success ? "succeeded" : "failed", true);
            AppendString(sb, "schema", "harness.input.raycast.v1");
            AppendBool(sb, "hit", result.Hit);
            AppendBool(sb, "camera_found", result.CameraFound);
            AppendString(sb, "input_coordinate_system", PiGameViewCoordinates.InputCoordinateSystem);
            AppendString(sb, "unity_coordinate_system", PiGameViewCoordinates.UnityCoordinateSystem);
            AppendString(sb, "coordinate_conversion_formula", PiGameViewCoordinates.ConversionFormula);
            AppendNumber(sb, "game_view_width", result.Conversion.GameViewSize.x);
            AppendNumber(sb, "game_view_height", result.Conversion.GameViewSize.y);
            AppendNumber(sb, "input_x", result.Conversion.InputPosition.x);
            AppendNumber(sb, "input_y", result.Conversion.InputPosition.y);
            AppendNumber(sb, "unity_x", result.Conversion.UnityPosition.x);
            AppendNumber(sb, "unity_y", result.Conversion.UnityPosition.y);

            if (!string.IsNullOrEmpty(result.Message))
                AppendString(sb, "message", result.Message);
            if (!string.IsNullOrEmpty(result.Error))
            {
                AppendString(sb, "error", result.Error);
                AppendString(sb, "error_type", result.ErrorType ?? "runtime");
            }

            if (result.Hit && result.NearestHit.HasValue)
            {
                RaycastHit hit = result.NearestHit.Value;
                GameObject go = hit.collider != null ? hit.collider.gameObject : null;
                AppendString(sb, "hit_name", go != null ? go.name : null);
                AppendString(sb, "hit_path", go != null ? GetHierarchyPath(go) : null);
                AppendNumber(sb, "hit_layer", go != null ? go.layer : -1);
                AppendString(sb, "hit_layer_name", go != null ? LayerMask.LayerToName(go.layer) : null);
                AppendNumber(sb, "distance", hit.distance);
                AppendNumber(sb, "hit_point_x", hit.point.x);
                AppendNumber(sb, "hit_point_y", hit.point.y);
                AppendNumber(sb, "hit_point_z", hit.point.z);
                AppendNumber(sb, "hit_normal_x", hit.normal.x);
                AppendNumber(sb, "hit_normal_y", hit.normal.y);
                AppendNumber(sb, "hit_normal_z", hit.normal.z);
            }

            AppendNumber(sb, "hit_count", result.Hits != null ? result.Hits.Length : 0);
            sb.Append('}');
            return sb.ToString();
        }

        public static string GetHierarchyPath(GameObject go)
        {
            if (go == null)
                return null;

            var path = go.name;
            Transform current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        private static void AppendString(StringBuilder sb, string name, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(name).Append("\":");
            if (value == null)
                sb.Append("null");
            else
                sb.Append('"').Append(PiUnityJsonHelper.EscapeJson(value)).Append('"');
        }

        private static void AppendBool(StringBuilder sb, string name, bool value)
        {
            sb.Append(',').Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
        }

        private static void AppendNumber(StringBuilder sb, string name, float value)
        {
            sb.Append(',').Append('"').Append(name).Append("\":")
                .Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void AppendNumber(StringBuilder sb, string name, int value)
        {
            sb.Append(',').Append('"').Append(name).Append("\":").Append(value);
        }
    }

    public readonly struct PiGameViewPhysicsRaycastResult
    {
        public readonly bool Success;
        public readonly bool CameraFound;
        public readonly bool Hit;
        public readonly PiGameViewCoordinateConversion Conversion;
        public readonly RaycastHit[] Hits;
        public readonly RaycastHit? NearestHit;
        public readonly string Message;
        public readonly string Error;
        public readonly string ErrorType;

        private PiGameViewPhysicsRaycastResult(
            bool success,
            bool cameraFound,
            bool hit,
            PiGameViewCoordinateConversion conversion,
            RaycastHit[] hits,
            string message,
            string error,
            string errorType)
        {
            Success = success;
            CameraFound = cameraFound;
            Hit = hit;
            Conversion = conversion;
            Hits = hits ?? System.Array.Empty<RaycastHit>();
            NearestHit = Hits.Length > 0 ? Hits[0] : (RaycastHit?)null;
            Message = message;
            Error = error;
            ErrorType = errorType;
        }

        public static PiGameViewPhysicsRaycastResult FromHits(
            PiGameViewCoordinateConversion conversion,
            Camera camera,
            RaycastHit[] hits)
        {
            bool hit = hits != null && hits.Length > 0;
            string message = hit
                ? "Hit " + hits[0].collider.gameObject.name + " at (" +
                  conversion.InputPosition.x.ToString("F1") + ", " +
                  conversion.InputPosition.y.ToString("F1") + ")."
                : "No physics hit at (" +
                  conversion.InputPosition.x.ToString("F1") + ", " +
                  conversion.InputPosition.y.ToString("F1") + ").";
            return new PiGameViewPhysicsRaycastResult(
                true, true, hit, conversion, hits, message, null, null);
        }

        public static PiGameViewPhysicsRaycastResult NoCamera(PiGameViewCoordinateConversion conversion)
        {
            return new PiGameViewPhysicsRaycastResult(
                false, false, false, conversion, null,
                null,
                "Camera.main was not found. Add an active MainCamera before using raycast.",
                "not_supported");
        }

        public static PiGameViewPhysicsRaycastResult Failed(
            PiGameViewCoordinateConversion conversion,
            string error,
            string errorType = "usage")
        {
            return new PiGameViewPhysicsRaycastResult(
                false, true, false, conversion, null, null, error, errorType);
        }
    }
}

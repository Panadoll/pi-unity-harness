using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    /// <summary>
    /// Pre-click probe: same coordinate path as input injection, reporting UI and 3D hits.
    /// </summary>
    public static class PiInputProbe
    {
        public static PiInputProbeResult ProbeAt(
            float x,
            float y,
            float maxDistance = PiGameViewPhysicsRaycast.DefaultMaxDistance,
            int layerMask = Physics.DefaultRaycastLayers,
            bool includePhysics = true)
        {
            PiGameViewCoordinateConversion conversion = PiGameViewCoordinates.ConvertInputToUnity(new Vector2(x, y));
            EventSystem eventSystem = EventSystem.current;

            GameObject uiHit = null;
            string uiHitPath = null;
            bool hasUiHit = false;
            if (eventSystem != null)
            {
                RaycastResult uiResult = PiUiRaycastHelper.RaycastUI(conversion.UnityPosition, eventSystem);
                if (uiResult.gameObject != null)
                {
                    hasUiHit = true;
                    uiHit = uiResult.gameObject;
                    uiHitPath = PiGameViewPhysicsRaycast.GetHierarchyPath(uiHit);
                }
            }

            PiGameViewPhysicsRaycastResult physics = default;
            bool physicsRan = false;
            if (includePhysics)
            {
                physics = PiGameViewPhysicsRaycast.RaycastFromInput(
                    conversion.InputPosition, maxDistance, layerMask, true, null);
                physicsRan = true;
            }

            // UI overlays block 3D click targets — match unity-cli-loop behavior.
            string wouldHitKind;
            string wouldHitName = null;
            string wouldHitPath = null;
            string blockedBy = null;
            bool wouldHit;

            if (hasUiHit)
            {
                wouldHit = true;
                wouldHitKind = "ui";
                wouldHitName = uiHit.name;
                wouldHitPath = uiHitPath;
                if (physicsRan && physics.Hit)
                    blockedBy = null; // UI wins; 3D is behind overlay
            }
            else if (physicsRan && physics.Success && physics.Hit)
            {
                wouldHit = true;
                wouldHitKind = "physics";
                wouldHitName = physics.NearestHit.Value.collider.gameObject.name;
                wouldHitPath = PiGameViewPhysicsRaycast.GetHierarchyPath(
                    physics.NearestHit.Value.collider.gameObject);
            }
            else if (physicsRan && !physics.Success && !physics.CameraFound)
            {
                wouldHit = false;
                wouldHitKind = "none";
                blockedBy = "no_camera";
            }
            else if (eventSystem == null && (!physicsRan || !physics.Hit))
            {
                wouldHit = false;
                wouldHitKind = "none";
                blockedBy = "event_system_missing_and_no_physics_hit";
            }
            else
            {
                wouldHit = false;
                wouldHitKind = "none";
                blockedBy = "no_hit";
            }

            return new PiInputProbeResult(
                conversion,
                wouldHit,
                wouldHitKind,
                wouldHitName,
                wouldHitPath,
                blockedBy,
                hasUiHit,
                uiHitName: uiHit != null ? uiHit.name : null,
                uiHitPath,
                eventSystem != null,
                physicsRan,
                physics);
        }

        public static string ToJson(PiInputProbeResult result)
        {
            var sb = new StringBuilder(640);
            sb.Append('{');
            AppendString(sb, "status", "succeeded", true);
            AppendString(sb, "schema", "harness.input.probe.v1");
            AppendBool(sb, "would_hit", result.WouldHit);
            AppendString(sb, "would_hit_kind", result.WouldHitKind);
            AppendString(sb, "would_hit_name", result.WouldHitName);
            AppendString(sb, "would_hit_path", result.WouldHitPath);
            AppendString(sb, "blocked_by", result.BlockedBy);
            AppendString(sb, "input_coordinate_system", PiGameViewCoordinates.InputCoordinateSystem);
            AppendString(sb, "unity_coordinate_system", PiGameViewCoordinates.UnityCoordinateSystem);
            AppendString(sb, "coordinate_conversion_formula", PiGameViewCoordinates.ConversionFormula);
            AppendNumber(sb, "game_view_width", result.Conversion.GameViewSize.x);
            AppendNumber(sb, "game_view_height", result.Conversion.GameViewSize.y);
            AppendNumber(sb, "input_x", result.Conversion.InputPosition.x);
            AppendNumber(sb, "input_y", result.Conversion.InputPosition.y);
            AppendNumber(sb, "unity_x", result.Conversion.UnityPosition.x);
            AppendNumber(sb, "unity_y", result.Conversion.UnityPosition.y);
            AppendBool(sb, "event_system_present", result.EventSystemPresent);
            AppendBool(sb, "ui_hit", result.UiHit);
            AppendString(sb, "ui_hit_name", result.UiHitName);
            AppendString(sb, "ui_hit_path", result.UiHitPath);
            AppendBool(sb, "physics_ran", result.PhysicsRan);
            if (result.PhysicsRan)
            {
                AppendBool(sb, "physics_hit", result.Physics.Hit);
                AppendBool(sb, "physics_camera_found", result.Physics.CameraFound);
                if (result.Physics.Hit && result.Physics.NearestHit.HasValue)
                {
                    var hit = result.Physics.NearestHit.Value;
                    var go = hit.collider != null ? hit.collider.gameObject : null;
                    AppendString(sb, "physics_hit_name", go != null ? go.name : null);
                    AppendString(sb, "physics_hit_path", go != null ? PiGameViewPhysicsRaycast.GetHierarchyPath(go) : null);
                    AppendNumber(sb, "physics_distance", hit.distance);
                }
                if (!result.Physics.Success && !string.IsNullOrEmpty(result.Physics.Error))
                    AppendString(sb, "physics_error", result.Physics.Error);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendString(StringBuilder sb, string name, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(name).Append("\":");
            if (value == null)
                sb.Append("null");
            else
                sb.Append('"').Append(PiAbilityJson.Escape(value)).Append('"');
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
    }

    public readonly struct PiInputProbeResult
    {
        public readonly PiGameViewCoordinateConversion Conversion;
        public readonly bool WouldHit;
        public readonly string WouldHitKind;
        public readonly string WouldHitName;
        public readonly string WouldHitPath;
        public readonly string BlockedBy;
        public readonly bool UiHit;
        public readonly string UiHitName;
        public readonly string UiHitPath;
        public readonly bool EventSystemPresent;
        public readonly bool PhysicsRan;
        public readonly PiGameViewPhysicsRaycastResult Physics;

        public PiInputProbeResult(
            PiGameViewCoordinateConversion conversion,
            bool wouldHit,
            string wouldHitKind,
            string wouldHitName,
            string wouldHitPath,
            string blockedBy,
            bool uiHit,
            string uiHitName,
            string uiHitPath,
            bool eventSystemPresent,
            bool physicsRan,
            PiGameViewPhysicsRaycastResult physics)
        {
            Conversion = conversion;
            WouldHit = wouldHit;
            WouldHitKind = wouldHitKind;
            WouldHitName = wouldHitName;
            WouldHitPath = wouldHitPath;
            BlockedBy = blockedBy;
            UiHit = uiHit;
            UiHitName = uiHitName;
            UiHitPath = uiHitPath;
            EventSystemPresent = eventSystemPresent;
            PhysicsRan = physicsRan;
            Physics = physics;
        }
    }
}

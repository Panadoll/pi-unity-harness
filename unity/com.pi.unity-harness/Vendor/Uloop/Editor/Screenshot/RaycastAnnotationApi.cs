using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using io.github.hatayama.UnityCliLoop.ToolContracts;

namespace io.github.hatayama.UnityCliLoop.FirstPartyTools
{
    /// <summary>
    /// Public entry for uloop-style clustered physics raycast annotations.
    /// </summary>
    public static class RaycastAnnotationApi
    {
        public static string CollectJson(int layerMask = Physics.DefaultRaycastLayers)
        {
            Vector2 size = GameViewCoordinateUtility.GetMainGameViewSize();
            List<UIElementInfo> elements = RaycastGridAnnotator.CollectPhysicsColliderElements(
                size,
                0,
                layerMask);
            List<RaycastLayerSummaryInfo> summaries = RaycastGridAnnotator.CollectRaycastLayerSummaries(size, 0);
            return JsonConvert.SerializeObject(new
            {
                schema = "uloop.raycast_annotation.v1",
                game_view_width = size.x,
                game_view_height = size.y,
                physics_colliders = elements,
                layer_summaries = summaries
            });
        }
    }
}

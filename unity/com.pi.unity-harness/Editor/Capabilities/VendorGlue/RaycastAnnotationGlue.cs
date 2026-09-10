using System.Collections.Generic;
using io.github.hatayama.UnityCliLoop.FirstPartyTools;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using Newtonsoft.Json;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.VendorGlue
{
    /// <summary>
    /// uloop 风格的分簇物理射线标注入口。
    ///
    /// 上游 uloop 没有把这一组合暴露成公开 API（RaycastGridAnnotator / GameViewCoordinateUtility
    /// 都是 internal），harness 通过 vendor 程序集 AssemblyInfo 里的
    /// InternalsVisibleTo("Pi.UnityHarness.Editor") 直接调用其内部实现。
    /// 上游若调整这些内部签名，此处需要跟着改。
    /// </summary>
    internal static class RaycastAnnotationGlue
    {
        public static string CollectJson(int layerMask = Physics.DefaultRaycastLayers)
        {
            Vector2 size = GameViewCoordinateUtility.GetMainGameViewSize();
            List<UIElementInfo> elements = RaycastGridAnnotator.CollectPhysicsColliderElements(size, 0, layerMask);
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

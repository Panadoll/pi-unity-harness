using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    public static class PiUiRaycastHelper
    {
        public static RaycastResult RaycastUI(Vector2 screenPosition, EventSystem eventSystem)
        {
            var pointerData = new PointerEventData(eventSystem)
            {
                position = screenPosition
            };
            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, results);

            bool hasCanvasSpaceHit = TryRaycastCanvasSpace(screenPosition, out RaycastResult canvasSpaceHit);
            if (results.Count > 0)
            {
                RaycastResult firstHit = results[0];
                if (hasCanvasSpaceHit && ShouldPreferCanvasSpaceHit(canvasSpaceHit, firstHit))
                    return canvasSpaceHit;

                return firstHit;
            }

            return hasCanvasSpaceHit ? canvasSpaceHit : new RaycastResult();
        }

        public static bool TryRaycastCanvasSpace(Vector2 canvasPosition, out RaycastResult hit)
        {
            bool found = false;
            RaycastResult bestHit = new RaycastResult();
            Canvas[] canvases = Object.FindObjectsOfType<Canvas>();

            foreach (Canvas canvas in canvases)
            {
                if (!canvas.gameObject.activeInHierarchy)
                    continue;

                var raycaster = canvas.GetComponent<GraphicRaycaster>();
                if (raycaster == null || !raycaster.enabled)
                    continue;

                var graphics = canvas.GetComponentsInChildren<Graphic>();
                foreach (Graphic graphic in graphics)
                {
                    Camera eventCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    if (!IsRaycastCandidate(graphic, canvasPosition, eventCamera))
                        continue;

                    var candidate = new RaycastResult
                    {
                        gameObject = graphic.gameObject,
                        module = raycaster,
                        screenPosition = canvasPosition,
                        sortingLayer = canvas.sortingLayerID,
                        sortingOrder = canvas.sortingOrder,
                        depth = graphic.depth
                    };

                    if (!found || CompareRaycastPriority(candidate, bestHit) > 0)
                    {
                        bestHit = candidate;
                        found = true;
                    }
                }
            }

            hit = bestHit;
            return found;
        }

        private static bool IsRaycastCandidate(Graphic graphic, Vector2 canvasPosition, Camera eventCamera)
        {
            if (!graphic.gameObject.activeInHierarchy || !graphic.enabled)
                return false;

            if (!graphic.raycastTarget || graphic.depth == -1 || graphic.canvasRenderer.cull)
                return false;

            if (!RectTransformUtility.RectangleContainsScreenPoint(graphic.rectTransform, canvasPosition, eventCamera))
                return false;

            return graphic.Raycast(canvasPosition, eventCamera);
        }

        private static bool ShouldPreferCanvasSpaceHit(RaycastResult canvasSpaceHit, RaycastResult eventSystemHit)
        {
            if (!(canvasSpaceHit.module is GraphicRaycaster))
                return false;

            if (!(eventSystemHit.module is GraphicRaycaster))
                return true;

            return CompareRaycastPriority(canvasSpaceHit, eventSystemHit) > 0;
        }

        private static int CompareRaycastPriority(RaycastResult left, RaycastResult right)
        {
            int sortOrderPriority = DefaultCompare(GetSortOrderPriority(left), GetSortOrderPriority(right));
            if (sortOrderPriority != 0) return sortOrderPriority;

            int renderOrderPriority = DefaultCompare(GetRenderOrderPriority(left), GetRenderOrderPriority(right));
            if (renderOrderPriority != 0) return renderOrderPriority;

            int sortingLayer = DefaultCompare(GetSortingLayerValue(left), GetSortingLayerValue(right));
            if (sortingLayer != 0) return sortingLayer;

            int sortingOrder = DefaultCompare(left.sortingOrder, right.sortingOrder);
            if (sortingOrder != 0) return sortingOrder;

            return DefaultCompare(left.depth, right.depth);
        }

        private static int GetSortOrderPriority(RaycastResult result)
        {
            return result.module != null ? result.module.sortOrderPriority : 0;
        }

        private static int GetRenderOrderPriority(RaycastResult result)
        {
            return result.module != null ? result.module.renderOrderPriority : 0;
        }

        private static int GetSortingLayerValue(RaycastResult result)
        {
            return SortingLayer.GetLayerValueFromID(result.sortingLayer);
        }

        private static int DefaultCompare(int left, int right) => System.Collections.Generic.Comparer<int>.Default.Compare(left, right);
    }
}

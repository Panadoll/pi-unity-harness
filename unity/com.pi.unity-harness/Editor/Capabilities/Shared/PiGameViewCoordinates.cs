using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    public static class PiGameViewCoordinates
    {
        public static Vector2 GetGameViewSize()
        {
            Vector2 size = Handles.GetMainGameViewSize();
            if (size.x > 0f && size.y > 0f)
                return size;

            if (Camera.main != null && Camera.main.pixelWidth > 0 && Camera.main.pixelHeight > 0)
                return new Vector2(Camera.main.pixelWidth, Camera.main.pixelHeight);

            float width = Screen.width > 0 ? Screen.width : 1f;
            float height = Screen.height > 0 ? Screen.height : 1f;
            return new Vector2(width, height);
        }

        public static float GetGameViewWidth()
        {
            return GetGameViewSize().x;
        }

        public static float GetGameViewHeight()
        {
            return GetGameViewSize().y;
        }

        public static float TopLeftYToScreenY(float y)
        {
            return GetGameViewHeight() - y;
        }
    }
}

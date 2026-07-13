using UnityEditor;
using UnityEngine;

namespace Pi.UnityHarness.Editor.Capabilities.Shared
{
    /// <summary>
    /// GameView coordinate helpers. Public input space is top-left origin (screenshot / agent friendly);
    /// Unity EventSystem / ScreenPointToRay space is bottom-left.
    /// </summary>
    public static class PiGameViewCoordinates
    {
        public const string InputCoordinateSystem = "top_left_game_view";
        public const string UnityCoordinateSystem = "bottom_left_game_view";
        public const string ConversionFormula = "unity_y = game_view_height - input_y";

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

        public static float GetGameViewHeight()
        {
            return GetGameViewSize().y;
        }

        public static float TopLeftYToScreenY(float y)
        {
            return GetGameViewHeight() - y;
        }

        public static PiGameViewCoordinateConversion ConvertInputToUnity(Vector2 inputPosition)
        {
            return ConvertInputToUnity(inputPosition, GetGameViewSize());
        }

        public static PiGameViewCoordinateConversion ConvertInputToUnity(Vector2 inputPosition, Vector2 gameViewSize)
        {
            Vector2 unityPosition = new Vector2(inputPosition.x, gameViewSize.y - inputPosition.y);
            return new PiGameViewCoordinateConversion(inputPosition, unityPosition, gameViewSize);
        }
    }

    public readonly struct PiGameViewCoordinateConversion
    {
        public readonly Vector2 InputPosition;
        public readonly Vector2 UnityPosition;
        public readonly Vector2 GameViewSize;

        public PiGameViewCoordinateConversion(Vector2 inputPosition, Vector2 unityPosition, Vector2 gameViewSize)
        {
            InputPosition = inputPosition;
            UnityPosition = unityPosition;
            GameViewSize = gameViewSize;
        }
    }
}

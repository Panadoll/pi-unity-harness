using UnityEngine;

namespace io.github.hatayama.UnityCliLoop.FirstPartyTools
{
    /// <summary>
    /// Describes the Game View geometry used to map rendering screenshots back to input coordinates.
    /// </summary>
    internal readonly struct GameRenderingImageInfo
    {
        public readonly Vector2 GameViewSize;
        public readonly Vector2 RenderingImageSize;
        public readonly int ImageToInputOffsetY;

        public GameRenderingImageInfo(
            Vector2 gameViewSize,
            Vector2 renderingImageSize,
            int imageToInputOffsetY)
        {
            GameViewSize = gameViewSize;
            RenderingImageSize = renderingImageSize;
            ImageToInputOffsetY = imageToInputOffsetY;
        }
    }
}

using System;

namespace Rgdsplus.VerticalWorld
{
    // No game dependencies: the camera and scaling contract can be tested offline.
    internal static class ScreenGeometry
    {
        internal static int PanelWidth => DisplayProfile.Current.UpperReferenceWidth;
        internal static int PanelHeight => DisplayProfile.Current.UpperReferenceHeight;
        internal static int LowerPanelWidth => DisplayProfile.Current.LowerReferenceWidth;
        internal static int LowerPanelHeight => DisplayProfile.Current.LowerReferenceHeight;
        internal static float ValidScale(float scale) => float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0 ? 1f : scale;
        internal static int Width(float scale) => (int)Math.Ceiling(PanelWidth / (double)ValidScale(scale));
        internal static int Height(float scale) => (int)Math.Ceiling(PanelHeight / (double)ValidScale(scale));
        internal static int LowerWidth(float scale) => (int)Math.Ceiling(LowerPanelWidth / (double)ValidScale(scale));
        internal static int LowerHeight(float scale) => (int)Math.Ceiling(LowerPanelHeight / (double)ValidScale(scale));
        internal static int CameraX(float playerX, int mapWidth, int width) => Clamp((int)playerX + 32 - width / 2, mapWidth - width);
        internal static int DualCameraX(float playerX, int mapWidth, int upperWidth)
        {
            if (mapWidth <= upperWidth) return (mapWidth - upperWidth) / 2;
            return Clamp((int)playerX + 32 - upperWidth / 2, mapWidth - upperWidth);
        }
        internal static int CameraY(float playerY, int mapHeight, int panelHeight, bool extendLower = true) => Clamp((int)playerY + 32 - panelHeight / 2, mapHeight - (extendLower ? 2 : 1) * panelHeight);
        internal static int CameraY(float playerY, int mapHeight, int upperHeight, int lowerHeight) =>
            Clamp((int)playerY + 32 - upperHeight / 2, mapHeight - upperHeight - lowerHeight);
        internal static int TrackDualScreenY(float playerY, int mapHeight, int upperHeight, int lowerHeight,
            int currentY)
        {
            int totalHeight = upperHeight + lowerHeight;
            int topDeadZone = Math.Max(64, upperHeight / 3);
            int bottomDeadZone = totalHeight - Math.Max(64, lowerHeight / 3);
            int playerCenter = (int)playerY + 32;
            int screenY = playerCenter - currentY;
            if (screenY < topDeadZone) currentY = playerCenter - topDeadZone;
            else if (screenY > bottomDeadZone) currentY = playerCenter - bottomDeadZone;
            return Clamp(currentY, mapHeight - totalHeight);
        }
        internal static int AlignedLowerX(int upperX, int upperWidth, int lowerWidth, int mapWidth) =>
            upperX + (upperWidth - lowerWidth) / 2;
        private static int Clamp(int value, int maximum) => Math.Max(0, Math.Min(Math.Max(0, maximum), value));
    }
}

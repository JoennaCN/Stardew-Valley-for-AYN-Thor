using Microsoft.Xna.Framework;

namespace Rgdsplus.VerticalWorld
{
    internal static class LowerPanelGeometry
    {
        // Touch devices report either compositor coordinates or lower-panel-local
        // coordinates. Both forms map through the fitted content rectangle, so
        // letterboxing and unequal panel resolutions do not shift hit targets.
        internal static bool TryMap(float x, float y, int uiWidth, int uiHeight, out Point point)
        {
            point = Point.Zero;
            if (!DisplayProfile.Current.TryMapLower(x, y, uiWidth, uiHeight, out int mappedX, out int mappedY))
                return false;
            point = new Point(mappedX, mappedY);
            return true;
        }
    }
}

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace Rgdsplus.VerticalWorld
{
    internal static class DisplayColorControls
    {
        internal static void DrawOverlay(SpriteBatch batch, Rectangle bounds, bool upper)
        {
            if (Game1.staminaRect == null || bounds.Width <= 0 || bounds.Height <= 0) return;
            var p = CompanionPreferences.Current;
            int brightness = upper ? p.UpperBrightness : p.LowerBrightness;
            int temperature = upper ? p.UpperTemperature : p.LowerTemperature;
            int tint = upper ? p.UpperTint : p.LowerTint;

            if (brightness < 100)
                batch.Draw(Game1.staminaRect, bounds, Color.Black * ((100 - brightness) / 100f * .75f));
            else if (brightness > 100)
                batch.Draw(Game1.staminaRect, bounds, Color.White * ((brightness - 100) / 50f * .25f));

            float warmth = Math.Clamp((temperature - 6500) / 3000f, -1f, 1f);
            if (warmth > 0)
                batch.Draw(Game1.staminaRect, bounds, new Color(255, 150, 72) * (warmth * .16f));
            else if (warmth < 0)
                batch.Draw(Game1.staminaRect, bounds, new Color(72, 150, 255) * (-warmth * .16f));

            float tintAmount = Math.Clamp(tint / 100f, -1f, 1f);
            if (tintAmount > 0)
                batch.Draw(Game1.staminaRect, bounds, new Color(255, 80, 210) * (tintAmount * .12f));
            else if (tintAmount < 0)
                batch.Draw(Game1.staminaRect, bounds, new Color(80, 255, 140) * (-tintAmount * .12f));
        }
    }
}

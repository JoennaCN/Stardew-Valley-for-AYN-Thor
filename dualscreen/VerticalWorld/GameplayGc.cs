using System;
using System.Runtime;
using StardewModdingAPI;
using StardewValley;

namespace Rgdsplus.VerticalWorld
{
    internal static class GameplayGc
    {
        private static GCLatencyMode previous = GCLatencyMode.Interactive;
        private static bool noGcRegion;
        private static int stationaryTicks;
        private static readonly long MovementBudget = Math.Clamp(
            long.TryParse(Environment.GetEnvironmentVariable("SDV_MOVEMENT_NOGC_MB"), out long mb) ? mb : 192,
            64, 512) * 1024L * 1024L;
        private static bool MovementNoGc => Environment.GetEnvironmentVariable("SDV_MOVEMENT_NOGC") == "1";

        internal static void Initialize(IModHelper helper)
        {
            previous = GCSettings.LatencyMode;
            helper.Events.GameLoop.SaveLoaded += (_, __) => Set(GCLatencyMode.SustainedLowLatency);
            helper.Events.GameLoop.UpdateTicked += (_, __) => UpdateMovementRegion();
            helper.Events.GameLoop.DayEnding += (_, __) => StopMovementRegion("day-ending");
            helper.Events.GameLoop.ReturnedToTitle += (_, __) => { StopMovementRegion("title"); Set(previous); };
            AppDomain.CurrentDomain.ProcessExit += (_, __) => { StopMovementRegion("exit"); Set(previous); };
        }

        // The game allocates steadily while walking. Short gen-0 collections are
        // disproportionately expensive under FEX, so defer them until the player
        // has stopped. The bounded region is never allowed to span saving/title.
        private static void UpdateMovementRegion()
        {
            if (!MovementNoGc || !Context.IsWorldReady || Game1.player == null) return;
            bool moving = Game1.player.isMoving() && Game1.activeClickableMenu == null && Game1.eventUp == false;
            if (moving)
            {
                stationaryTicks = 0;
                if (!noGcRegion)
                {
                    try
                    {
                        noGcRegion = GC.TryStartNoGCRegion(MovementBudget, disallowFullBlockingGC: true);
                        if (noGcRegion) ModEntry.Write("gc-movement-region=start budget-mb=" + MovementBudget / 1048576);
                    }
                    catch (Exception error)
                    {
                        ModEntry.Write("gc-movement-region-start-failed " + error.GetType().Name + ": " + error.Message);
                    }
                }
            }
            else if (noGcRegion && ++stationaryTicks >= 30)
                StopMovementRegion("stationary");
        }

        private static void StopMovementRegion(string reason)
        {
            if (!noGcRegion) return;
            noGcRegion = false;
            stationaryTicks = 0;
            try
            {
                GC.EndNoGCRegion();
                ModEntry.Write("gc-movement-region=stop reason=" + reason);
            }
            catch (Exception error)
            {
                // The runtime automatically leaves the region if its allocation
                // budget is exhausted; this is informational, not a game failure.
                ModEntry.Write("gc-movement-region-ended reason=" + reason + " " + error.GetType().Name);
            }
        }

        private static void Set(GCLatencyMode mode)
        {
            try
            {
                if (GCSettings.LatencyMode != mode) GCSettings.LatencyMode = mode;
                ModEntry.Write("gc-latency=" + GCSettings.LatencyMode);
            }
            catch (Exception error)
            {
                ModEntry.Write("gc-latency-failed " + error.GetType().Name + ": " + error.Message);
            }
        }
    }
}

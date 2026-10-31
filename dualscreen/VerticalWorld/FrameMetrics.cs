using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using StardewValley;

namespace Rgdsplus.VerticalWorld
{
    internal static class FrameMetrics
    {
        private static string path;
        private static long frameStart, previousFrame, windowStart, updateStart, worldStart, presentStart;
        private static double updateMs, drawMs, worldMs, presentMs;
        private static double updateMaxMs, drawMaxMs, worldMaxMs, presentMaxMs;
        private static readonly double[] intervals = new double[2048];
        private static int count, updates, frames, gc0;
        private static int activeFrames;
        private static object windowLocation;
        private static Type windowMenu;
        private static float windowZoom, windowUiScale;
        private static bool mixedContext;
        private static LowerScreenMode windowMode;
        private static bool enabled;
        private static readonly int WindowMilliseconds = int.TryParse(
            Environment.GetEnvironmentVariable("SDV_FRAME_METRICS_WINDOW_MS"), out int requestedWindow)
            ? Math.Clamp(requestedWindow, 1000, 10000) : 10000;
        internal static int TargetAllocations;
        internal static void Initialize(string directory)
        {
            enabled = Environment.GetEnvironmentVariable("SDV_FRAME_METRICS") == "1";
            if (!enabled) return;
            // Device diagnostics must not touch the game/mod filesystem on the
            // render thread. ArmadaOS exposes tmpfs at /dev/shm, so ten-second
            // samples remain observable without introducing storage stalls.
            path = File.Exists("/dev/shm") || Directory.Exists("/dev/shm")
                ? "/dev/shm/stardew-frame-metrics.csv"
                : Path.Combine(directory, "frame-metrics.csv");
            try
            {
                if (File.Exists(path))
                    File.Move(path, Path.Combine(directory, "frame-metrics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".csv"));
                File.WriteAllText(path, "utc,location,tile_x,tile_y,menu,passes,frames,updates,fps,frame_p50_ms,frame_p95_ms,frame_p99_ms,frame_max_ms,update_avg_ms,draw_avg_ms,world_avg_ms,present_avg_ms,update_max_ms,draw_max_ms,world_max_ms,present_max_ms,managed_mb,gc0,target_allocations,active_frames,inactive_sleep_ms,zoom,ui_scale,mixed_context,layout\n");
            }
            catch { path = null; }
            windowStart = Stopwatch.GetTimestamp();
            gc0 = GC.CollectionCount(0);
        }
        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        internal static void BeginUpdate() { if (enabled) updateStart = Stopwatch.GetTimestamp(); }
        internal static void EndUpdate() { if (enabled) { double ms = Milliseconds(Stopwatch.GetTimestamp() - updateStart); updateMs += ms; updateMaxMs = Math.Max(updateMaxMs, ms); updates++; } }
        internal static void BeginWorld() { if (enabled) worldStart = Stopwatch.GetTimestamp(); }
        internal static void EndWorld() { if (enabled) { double ms = Milliseconds(Stopwatch.GetTimestamp() - worldStart); worldMs += ms; worldMaxMs = Math.Max(worldMaxMs, ms); } }
        internal static void BeginPresent() { if (enabled) presentStart = Stopwatch.GetTimestamp(); }
        internal static void EndPresent() { if (enabled) { double ms = Milliseconds(Stopwatch.GetTimestamp() - presentStart); presentMs += ms; presentMaxMs = Math.Max(presentMaxMs, ms); } }
        internal static void BeginFrame()
        {
            if (!enabled) return;
            frameStart = Stopwatch.GetTimestamp();
            // Exclude windows spanning a load/menu/zoom change from scene comparisons.
            var location = Game1.currentLocation;
            var menu = Game1.activeClickableMenu?.GetType();
            float zoom = Game1.options?.zoomLevel ?? 1f;
            float uiScale = Game1.options?.uiScale ?? 1f;
            if (frames == 0)
            {
                windowLocation = location;
                windowMenu = menu;
                windowZoom = zoom;
                windowUiScale = uiScale;
                windowMode = DisplayModeController.State.Mode;
            }
            else if (!ReferenceEquals(windowLocation, location) || windowMenu != menu || windowZoom != zoom || windowUiScale != uiScale || windowMode != DisplayModeController.State.Mode)
                mixedContext = true;
            if (GameRunner.instance?.IsActive == true) activeFrames++;
            if (previousFrame != 0 && count < intervals.Length) intervals[count++] = Milliseconds(frameStart - previousFrame);
            previousFrame = frameStart;
        }
        internal static void EndFrame()
        {
            NativeTouch.AfterFrame();
            if (!enabled) return;
            double draw = Milliseconds(Stopwatch.GetTimestamp() - frameStart);
            drawMs += draw;
            drawMaxMs = Math.Max(drawMaxMs, draw);
            frames++;
            double elapsed = Milliseconds(frameStart - windowStart);
            if (elapsed < WindowMilliseconds || count == 0 || path == null) return;
            Array.Sort(intervals, 0, count);
            string row = string.Format(CultureInfo.InvariantCulture,
                "{0:O},{1},{2},{3},{4},{5},{6},{7},{8:F2},{9:F2},{10:F2},{11:F2},{12:F2},{13:F2},{14:F2},{15:F2},{16:F2},{17:F2},{18:F2},{19:F2},{20:F2},{21:F1},{22},{23},{24},{25:F1},{26:F3},{27:F3},{28},{29}\n",
                DateTime.UtcNow, Game1.currentLocation?.Name ?? "Title",
                Game1.player?.TilePoint.X ?? -1, Game1.player?.TilePoint.Y ?? -1,
                Game1.activeClickableMenu?.GetType().Name ?? "none",
                VerticalWorldRenderer.TwoPass && windowMode == LowerScreenMode.SceneHud ? 2 : 1, frames, updates, frames * 1000.0 / elapsed,
                intervals[(count - 1) / 2], intervals[(int)((count - 1) * .95)],
                intervals[(int)((count - 1) * .99)], intervals[count - 1],
                updateMs / Math.Max(1, updates), drawMs / frames, worldMs / frames, presentMs / frames,
                updateMaxMs, drawMaxMs, worldMaxMs, presentMaxMs,
                GC.GetTotalMemory(false) / 1048576.0, GC.CollectionCount(0) - gc0, TargetAllocations,
                activeFrames, GameRunner.instance?.InactiveSleepTime.TotalMilliseconds ?? 0,
                windowZoom, windowUiScale, mixedContext ? 1 : 0, windowMode);
            try { File.AppendAllText(path, row); } catch { }
            windowStart = frameStart;
            frames = updates = count = TargetAllocations = 0;
            activeFrames = 0;
            mixedContext = false;
            updateMs = drawMs = worldMs = presentMs = 0;
            updateMaxMs = drawMaxMs = worldMaxMs = presentMaxMs = 0;
            gc0 = GC.CollectionCount(0);
        }
    }
}

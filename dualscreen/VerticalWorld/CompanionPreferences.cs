using System;
using StardewModdingAPI;
using StardewValley;

namespace Rgdsplus.VerticalWorld
{
    internal sealed class CompanionPreferences
    {
        public bool NativeFrames { get; set; } = true;
        public float UiScale { get; set; } = 1.5f;
        public int LayoutVersion { get; set; }
        public string DeviceProfile { get; set; } = "auto";
        public int UpperBrightness { get; set; } = 100;
        public int UpperTemperature { get; set; } = 6500;
        public int UpperTint { get; set; } = 0;
        public int LowerBrightness { get; set; } = 100;
        public int LowerTemperature { get; set; } = 6500;
        public int LowerTint { get; set; } = 0;
        public bool[] VisiblePages { get; set; } = new bool[Dashboard.PageCount];
        private static IModHelper helper;
        internal static CompanionPreferences Current = new CompanionPreferences();
        public CompanionPreferences() => Array.Fill(VisiblePages, true);
        internal static void Initialize(IModHelper modHelper)
        {
            helper = modHelper;
            Current = helper.ReadConfig<CompanionPreferences>();
            DisplayProfile.Configure(Current.DeviceProfile);
            // Old page indices describe Today/Farm/Skills; they cannot be reused for v2.
            if (Current.LayoutVersion < 2)
            {
                Current.VisiblePages = new CompanionPreferences().VisiblePages;
                Current.UiScale = 1.5f;
                Current.LayoutVersion = 2;
                Save();
            }
            if (Current.VisiblePages == null || Current.VisiblePages.Length != Dashboard.PageCount)
            {
                var previous = Current.VisiblePages;
                Current.VisiblePages = new CompanionPreferences().VisiblePages;
                if (previous != null) Array.Copy(previous, Current.VisiblePages, Math.Min(previous.Length, Current.VisiblePages.Length));
            }
            Current.VisiblePages[12] = Current.VisiblePages[13] = true;
            Current.VisiblePages[0] = Current.VisiblePages[11] = true;
            Current.UiScale = ValidUiScale(Current.UiScale);
            Current.UpperBrightness = Math.Clamp(Current.UpperBrightness, 50, 150);
            Current.LowerBrightness = Math.Clamp(Current.LowerBrightness, 50, 150);
            Current.UpperTemperature = Math.Clamp(Current.UpperTemperature, 3500, 9500);
            Current.LowerTemperature = Math.Clamp(Current.LowerTemperature, 3500, 9500);
            Current.UpperTint = Math.Clamp(Current.UpperTint, -100, 100);
            Current.LowerTint = Math.Clamp(Current.LowerTint, -100, 100);
            helper.Events.GameLoop.GameLaunched += (_, __) => ApplyUiScale();
            helper.Events.GameLoop.SaveLoaded += (_, __) => ApplyUiScale();
            helper.Events.GameLoop.DayStarted += (_, __) => ApplyUiScale();
        }
        internal static float ValidUiScale(float scale) => float.IsFinite(scale) ? Math.Clamp(scale, .75f, 1.5f) : 1.5f;
        internal static void ApplyUiScale()
        {
            if (!VerticalWorldRenderer.Active || Game1.options == null) return;
            Game1.options.singlePlayerDesiredUIScale = Game1.options.localCoopDesiredUIScale = Game1.options.baseUIScale = Current.UiScale;
            UiPatches.EnsureViewports();
        }
        internal static void SetUiScale(float scale)
        {
            Current.UiScale = ValidUiScale(scale);
            ApplyUiScale();
            Save();
        }
        internal static void RememberUiScale(float scale)
        {
            if (!VerticalWorldRenderer.Active || !Context.IsWorldReady || helper == null || !float.IsFinite(scale) || scale < .75f || scale > 1.5f) return;
            if (Math.Abs(Current.UiScale - scale) < .001f) return;
            Current.UiScale = scale;
            Save();
        }
        internal static bool Visible(int page) => Current.VisiblePages[page];
        internal static void Save() => helper?.WriteConfig(Current);
    }
}
